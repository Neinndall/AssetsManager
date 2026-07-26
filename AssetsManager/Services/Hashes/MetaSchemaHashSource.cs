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

            if (root.TryGetProperty("classes", out JsonElement classes) &&
                classes.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty classProperty in classes.EnumerateObject())
                {
                    JsonElement classValue = classProperty.Value;
                    if (!IsActive(classValue)) continue;
                    if (TryReadName(classValue, out string className))
                        knownTypes.Add(className);
                    else if (TryParseHash(classProperty.Name, out ulong classHash))
                        unknownTypes.Add(classHash);

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
                    }
                }
            }
            return new MetaSchemaHashSnapshot
            {
                Version = version,
                UnknownTypes = unknownTypes,
                UnknownFields = unknownFields,
                KnownTypeNames = knownTypes.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                KnownFieldNames = knownFields.OrderBy(value => value, StringComparer.Ordinal).ToArray()
            };
        }

        private static bool IsActive(JsonElement value)
        {
            if (!value.TryGetProperty("revisions", out JsonElement revisions) ||
                revisions.ValueKind != JsonValueKind.Array ||
                revisions.GetArrayLength() == 0) return false;
            JsonElement latest = revisions[revisions.GetArrayLength() - 1];
            return !latest.TryGetProperty("to", out JsonElement to) ||
                   to.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
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
