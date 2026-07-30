using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Utils;

namespace AssetsManager.Services.Hashes
{
    public sealed class MetaSchemaHashSnapshot
    {
        public string Version { get; init; } = "unavailable";
        public HashSet<ulong> UnknownTypes { get; init; } = new();
        public HashSet<ulong> UnknownFields { get; init; } = new();
        public IReadOnlyList<string> KnownTypeNames { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> KnownFieldNames { get; init; } = Array.Empty<string>();
        public IReadOnlyDictionary<ulong, IReadOnlyList<string>> TypeContexts { get; init; } =
            new Dictionary<ulong, IReadOnlyList<string>>();
    }

    public sealed class MetaSchemaHashSource
    {
        private const string DatabaseUrl = "https://meta-api.leaguetoolkit.dev/v1/db";
        private readonly HttpClient _httpClient;
        private readonly DirectoriesCreator _directories;
        private readonly LogService _log;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private MetaSchemaHashSnapshot _cached;

        public MetaSchemaHashSource(HttpClient httpClient, DirectoriesCreator directories, LogService log)
        {
            _httpClient = httpClient;
            _directories = directories;
            _log = log;
        }

        public async Task<MetaSchemaHashSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            if (_cached != null) return _cached;
            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (_cached != null) return _cached;
                string cachePath = Path.Combine(_directories.HashLabPath, "meta-schema.json");
                try
                {
                    using Stream response = await _httpClient.GetStreamAsync(DatabaseUrl, cancellationToken);
                    using var memory = new MemoryStream();
                    await response.CopyToAsync(memory, cancellationToken);
                    memory.Position = 0;
                    _cached = Parse(memory);
                    Directory.CreateDirectory(_directories.HashLabPath);
                    string temporaryPath = cachePath + ".tmp";
                    try
                    {
                        await File.WriteAllBytesAsync(temporaryPath, memory.ToArray(), cancellationToken);
                        File.Move(temporaryPath, cachePath, true);
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                    }
                    return _cached;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (File.Exists(cachePath))
                    {
                        await using Stream cachedFile = File.OpenRead(cachePath);
                        _cached = Parse(cachedFile);
                        _log.LogWarning($"Meta Schema download failed; cached version '{_cached.Version}' is being used: {ex.Message}");
                        return _cached;
                    }
                    _log.LogWarning($"Meta Schema is unavailable; runtime BIN inventory will still be used: {ex.Message}");
                    return _cached = new MetaSchemaHashSnapshot();
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        internal static MetaSchemaHashSnapshot Parse(Stream stream)
        {
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            string version = root.TryGetProperty("latest", out JsonElement latest)
                ? latest.ToString()
                : "unknown";
            var unknownTypes = new HashSet<ulong>();
            var unknownFields = new HashSet<ulong>();
            var knownTypes = new HashSet<string>(StringComparer.Ordinal);
            var knownFields = new HashSet<string>(StringComparer.Ordinal);
            var typeContexts = new Dictionary<ulong, HashSet<string>>();

            if (root.TryGetProperty("classes", out JsonElement classes) &&
                classes.ValueKind == JsonValueKind.Object)
            {
                var classNamesByHash = classes.EnumerateObject()
                    .Where(property => IsActive(property.Value) && TryParseHash(property.Name, out _))
                    .ToDictionary(
                        property =>
                        {
                            TryParseHash(property.Name, out ulong hash);
                            return hash;
                        },
                        property => TryReadName(property.Value, out string name) ? name : property.Name);

                foreach (JsonProperty classProperty in classes.EnumerateObject())
                {
                    JsonElement classValue = classProperty.Value;
                    if (!IsActive(classValue)) continue;
                    bool hasClassHash = TryParseHash(classProperty.Name, out ulong currentClassHash);
                    if (TryReadName(classValue, out string className))
                        knownTypes.Add(className);
                    else if (hasClassHash)
                        unknownTypes.Add(currentClassHash);

                    string ownerName = TryReadName(classValue, out className)
                        ? className
                        : classProperty.Name;
                    if (TryGetActiveRevision(classValue, out JsonElement classRevision) &&
                        classRevision.TryGetProperty("bases", out JsonElement bases))
                    {
                        foreach (ulong referencedHash in EnumerateHashes(bases))
                        {
                            if (referencedHash == 0) continue;
                            AddTypeContext(referencedHash, $"base of {ownerName}");
                            if (hasClassHash)
                            {
                                string baseName = classNamesByHash.TryGetValue(referencedHash, out string knownBase)
                                    ? knownBase
                                    : $"0x{referencedHash:x8}";
                                AddTypeContext(currentClassHash, $"inherits {baseName}");
                            }
                        }
                    }

                    if (!classValue.TryGetProperty("properties", out JsonElement properties) ||
                        properties.ValueKind != JsonValueKind.Object) continue;
                    foreach (JsonProperty fieldProperty in properties.EnumerateObject())
                    {
                        JsonElement fieldValue = fieldProperty.Value;
                        if (!IsActive(fieldValue)) continue;
                        if (TryReadName(fieldValue, out string fieldName))
                            knownFields.Add(fieldName);
                        else if (TryParseHash(fieldProperty.Name, out ulong fieldHash))
                            unknownFields.Add(fieldHash);

                        string propertyName = TryReadName(fieldValue, out fieldName)
                            ? fieldName
                            : fieldProperty.Name;
                        if (TryGetActiveRevision(fieldValue, out JsonElement fieldRevision) &&
                            fieldRevision.TryGetProperty("type", out JsonElement type))
                        {
                            foreach (ulong referencedHash in EnumerateHashes(type))
                            {
                                if (referencedHash == 0) continue;
                                AddTypeContext(referencedHash, $"{ownerName}.{propertyName}");
                            }
                        }
                    }
                }
            }
            return new MetaSchemaHashSnapshot
            {
                Version = version,
                UnknownTypes = unknownTypes,
                UnknownFields = unknownFields,
                KnownTypeNames = knownTypes.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                KnownFieldNames = knownFields.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                TypeContexts = typeContexts.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)pair.Value.OrderBy(value => value, StringComparer.Ordinal).ToArray())
            };

            void AddTypeContext(ulong hash, string context)
            {
                if (!typeContexts.TryGetValue(hash, out HashSet<string> contexts))
                {
                    contexts = new HashSet<string>(StringComparer.Ordinal);
                    typeContexts[hash] = contexts;
                }
                contexts.Add(context);
            }
        }

        private static bool IsActive(JsonElement value)
            => TryGetActiveRevision(value, out _);

        private static bool TryGetActiveRevision(JsonElement value, out JsonElement latest)
        {
            latest = default;
            if (!value.TryGetProperty("revisions", out JsonElement revisions) ||
                revisions.ValueKind != JsonValueKind.Array ||
                revisions.GetArrayLength() == 0) return false;
            latest = revisions[revisions.GetArrayLength() - 1];
            return !latest.TryGetProperty("to", out JsonElement to) ||
                   to.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
        }

        private static IEnumerable<ulong> EnumerateHashes(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.String &&
                TryParseHash(value.GetString(), out ulong hash))
            {
                yield return hash;
                yield break;
            }
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in value.EnumerateArray())
                    foreach (ulong childHash in EnumerateHashes(child))
                        yield return childHash;
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty child in value.EnumerateObject())
                    foreach (ulong childHash in EnumerateHashes(child.Value))
                        yield return childHash;
            }
        }

        private static bool TryReadName(JsonElement value, out string name)
        {
            name = null;
            return value.TryGetProperty("name", out JsonElement nameElement) &&
                   nameElement.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(name = nameElement.GetString());
        }

        private static bool TryParseHash(string value, out ulong hash)
        {
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
            return ulong.TryParse(value, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out hash);
        }
    }
}
