using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;

namespace AssetsManager.Services.Hashes
{
    public sealed class BinRstHashGuessingStore
    {
        private readonly DirectoriesCreator _directories;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public BinRstHashGuessingStore(DirectoriesCreator directories) => _directories = directories;

        public string GetKnownPath(InternalHashKind kind) => Path.Combine(_directories.HashesPath, GetKnownFileName(kind));
        public string GetOverridePath(InternalHashKind kind) =>
            Path.Combine(_directories.HashLabPath, "overrides", GetKnownFileName(kind));

        public async Task<Dictionary<ulong, string>> LoadKnownAsync(InternalHashKind kind, CancellationToken cancellationToken)
        {
            var result = new Dictionary<ulong, string>();
            int width = IsRst(kind) ? 16 : 8;
            foreach (string path in new[] { GetKnownPath(kind), GetOverridePath(kind) })
            {
                if (!File.Exists(path)) continue;
                using var reader = new StreamReader(path);
                while (await reader.ReadLineAsync(cancellationToken) is string line)
                {
                    if (line.Length <= width || line[width] != ' ') continue;
                    if (ulong.TryParse(line.AsSpan(0, width), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                        result.TryAdd(hash, line[(width + 1)..]);
                }
            }
            return result;
        }

        public async Task<HashSet<ulong>> LoadUnknownAsync(InternalHashKind kind, CancellationToken cancellationToken)
        {
            var result = new HashSet<ulong>();
            foreach (string path in GetUnknownPaths(kind))
            {
                if (!File.Exists(path)) continue;
                using var reader = new StreamReader(path);
                while (await reader.ReadLineAsync(cancellationToken) is string line)
                    if (ulong.TryParse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash)) result.Add(hash);
            }
            return result;
        }

        public async Task<HashSet<ulong>> LoadCurrentUnknownAsync(InternalHashKind kind, CancellationToken cancellationToken)
        {
            var result = new HashSet<ulong>();
            string path = GetCurrentPath(kind);
            if (!File.Exists(path)) return result;
            using var reader = new StreamReader(path);
            while (await reader.ReadLineAsync(cancellationToken) is string line)
                if (ulong.TryParse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash)) result.Add(hash);
            return result;
        }

        public async Task<IReadOnlyList<InternalHashGuessMatch>> LoadResearchAsync(CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                string path = Path.Combine(_directories.HashLabPath, "internal.research.json");
                if (!File.Exists(path)) return Array.Empty<InternalHashGuessMatch>();
                await using var input = File.OpenRead(path);
                return await JsonSerializer.DeserializeAsync<List<InternalHashGuessMatch>>(
                    input, cancellationToken: cancellationToken) ?? new();
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task SaveInventoryAsync(
            IReadOnlyDictionary<InternalHashKind, HashSet<ulong>> observed,
            string patchFingerprint,
            string domain,
            CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(_directories.HashLabPath);
                await SaveResearchAsync(Array.Empty<InternalHashGuessMatch>(), cancellationToken);
                foreach (var pair in observed)
                {
                    var known = await LoadKnownAsync(pair.Key, cancellationToken);
                    var knownLookups = BuildKnownLookupSet(pair.Key, known.Keys);
                    var current = pair.Value.Where(hash => !knownLookups.Contains(hash)).ToHashSet();
                    var historical = await LoadUnknownAsync(pair.Key, cancellationToken);
                    historical.UnionWith(current);
                    historical.ExceptWith(knownLookups);
                    if (!IsRst(pair.Key))
                    {
                        current.Remove(0);
                        historical.Remove(0);
                    }
                    await WriteUnknownAtomicallyAsync(GetPrimaryUnknownPath(pair.Key), historical, pair.Key, cancellationToken);
                    await WriteUnknownAtomicallyAsync(GetCurrentPath(pair.Key), current, pair.Key, cancellationToken);
                }
                await WriteTextAtomicallyAsync(Path.Combine(_directories.HashLabPath, $"internal.{domain}.patch.txt"), new[] { patchFingerprint }, cancellationToken);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task SaveMatchesAsync(IEnumerable<InternalHashGuessMatch> matches, CancellationToken cancellationToken)
        {
            var materialized = matches
                .Where(match => match.VerificationSchema >= InternalHashGuessMatch.CurrentVerificationSchema)
                .ToList();
            await _lock.WaitAsync(cancellationToken);
            try
            {
                await SaveResearchAsync(materialized, cancellationToken);
                var groups = materialized.Where(match => match.CanPromote).GroupBy(match => match.Kind).ToList();
                foreach (var group in groups)
                {
                    var incoming = group.GroupBy(match => match.Hash).ToDictionary(g => g.Key, g => g.Last().Value);
                    await MergeKnownAsync(GetOverridePath(group.Key), incoming, IsRst(group.Key) ? 16 : 8, cancellationToken);
                    var resolvedLookups = group.Select(match => match.LookupHash).ToHashSet();
                    foreach (string path in GetUnknownPaths(group.Key).Append(GetCurrentPath(group.Key)))
                    {
                        if (!File.Exists(path)) continue;
                        var remaining = new HashSet<ulong>();
                        using (var reader = new StreamReader(path))
                            while (await reader.ReadLineAsync(cancellationToken) is string line)
                                if (ulong.TryParse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash) &&
                                    !resolvedLookups.Contains(hash)) remaining.Add(hash);
                        await WriteUnknownAtomicallyAsync(path, remaining, group.Key, cancellationToken);
                    }
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task SaveResearchAsync(IEnumerable<InternalHashGuessMatch> incoming, CancellationToken cancellationToken)
        {
            string path = Path.Combine(_directories.HashLabPath, "internal.research.json");
            var existing = new List<InternalHashGuessMatch>();
            if (File.Exists(path))
            {
                await using var input = File.OpenRead(path);
                existing = await JsonSerializer.DeserializeAsync<List<InternalHashGuessMatch>>(
                    input, cancellationToken: cancellationToken) ?? new();
            }
            var legacy = existing.Where(match =>
                match.VerificationSchema < InternalHashGuessMatch.CurrentVerificationSchema).ToList();
            if (legacy.Count > 0)
                await SaveLegacyQuarantineAsync(legacy, cancellationToken);

            var merged = existing
                .Where(match => match.VerificationSchema >= InternalHashGuessMatch.CurrentVerificationSchema)
                .Concat(incoming).GroupBy(match => new { match.Kind, match.Hash, match.Value })
                .Select(group => group.OrderByDescending(match => match.FoundAtUtc).First())
                .OrderBy(match => match.Kind).ThenBy(match => match.Value, StringComparer.Ordinal).ToList();
            string temporary = path + ".tmp";
            try
            {
                await using (var output = File.Create(temporary))
                    await JsonSerializer.SerializeAsync(output, merged, cancellationToken: cancellationToken);
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private async Task SaveLegacyQuarantineAsync(
            IEnumerable<InternalHashGuessMatch> incoming,
            CancellationToken cancellationToken)
        {
            string path = Path.Combine(_directories.HashLabPath, "internal.legacy-quarantine.json");
            var existing = new List<InternalHashGuessMatch>();
            if (File.Exists(path))
            {
                await using var input = File.OpenRead(path);
                existing = await JsonSerializer.DeserializeAsync<List<InternalHashGuessMatch>>(
                    input, cancellationToken: cancellationToken) ?? new();
            }
            var merged = existing.Concat(incoming)
                .GroupBy(match => new { match.Kind, match.Hash, match.Value })
                .Select(group => group.OrderByDescending(match => match.FoundAtUtc).First())
                .OrderBy(match => match.Kind).ThenBy(match => match.Value, StringComparer.Ordinal)
                .ToList();
            string temporary = path + ".tmp";
            try
            {
                await using (var output = File.Create(temporary))
                    await JsonSerializer.SerializeAsync(output, merged, cancellationToken: cancellationToken);
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public async Task<InternalHashSummary> LoadSummaryAsync(CancellationToken cancellationToken)
        {
            var counts = new Dictionary<InternalHashKind, int>();
            foreach (InternalHashKind kind in Enum.GetValues<InternalHashKind>())
                counts[kind] = (await LoadUnknownAsync(kind, cancellationToken)).Count;
            return new InternalHashSummary
            {
                BinEntries = counts[InternalHashKind.BinEntries], BinFields = counts[InternalHashKind.BinFields],
                BinTypes = counts[InternalHashKind.BinTypes], BinHashes = counts[InternalHashKind.BinHashes],
                RstXxh3 = counts[InternalHashKind.RstXxh3], RstXxh64 = counts[InternalHashKind.RstXxh64]
            };
        }

        private static HashSet<ulong> BuildKnownLookupSet(InternalHashKind kind, IEnumerable<ulong> known)
        {
            if (!IsRst(kind)) return known.ToHashSet();
            if (kind == InternalHashKind.RstXxh3)
                return known.Select(hash => hash & ((1UL << 38) - 1)).ToHashSet();
            var lookups = new HashSet<ulong>();
            foreach (ulong hash in known)
            {
                lookups.Add(hash);
                lookups.Add(hash & ((1UL << 38) - 1));
                lookups.Add(hash & ((1UL << 39) - 1));
                lookups.Add(hash & ((1UL << 40) - 1));
            }
            return lookups;
        }

        private async Task MergeKnownAsync(string path, IReadOnlyDictionary<ulong, string> incoming, int width, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var values = new Dictionary<ulong, string>();
            if (File.Exists(path))
            {
                using var reader = new StreamReader(path);
                while (await reader.ReadLineAsync(cancellationToken) is string line)
                    if (line.Length > width && ulong.TryParse(line.AsSpan(0, width), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                        values[hash] = line[(width + 1)..];
            }
            foreach (var pair in incoming) values[pair.Key] = pair.Value;
            await WriteTextAtomicallyAsync(path, values.OrderBy(pair => pair.Value, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key.ToString(width == 16 ? "x16" : "x8")} {pair.Value}"), cancellationToken);
        }

        private async Task WriteUnknownAtomicallyAsync(string path, IEnumerable<ulong> hashes, InternalHashKind kind, CancellationToken cancellationToken)
        {
            string format = IsRst(kind) ? "x16" : "x8";
            await WriteTextAtomicallyAsync(path, hashes.OrderBy(hash => hash).Select(hash => hash.ToString(format)), cancellationToken);
        }

        private static async Task WriteTextAtomicallyAsync(string path, IEnumerable<string> lines, CancellationToken cancellationToken)
        {
            string temporary = path + ".tmp";
            try
            {
                await File.WriteAllLinesAsync(temporary, lines, cancellationToken);
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private string GetPrimaryUnknownPath(InternalHashKind kind) => Path.Combine(_directories.HashLabPath, $"unknowns.{GetSuffix(kind)}.txt");
        private string GetCurrentPath(InternalHashKind kind) => Path.Combine(_directories.HashLabPath, $"current.{GetSuffix(kind)}.txt");
        private IEnumerable<string> GetUnknownPaths(InternalHashKind kind)
        {
            yield return GetPrimaryUnknownPath(kind);
            if (kind == InternalHashKind.RstXxh64)
                foreach (int bits in new[] { 38, 39, 40 }) yield return Path.Combine(_directories.HashLabPath, $"unknowns.rst.xxh64.{bits}.txt");
        }

        private static bool IsRst(InternalHashKind kind) => kind is InternalHashKind.RstXxh3 or InternalHashKind.RstXxh64;
        private static string GetSuffix(InternalHashKind kind) => kind switch
        {
            InternalHashKind.BinEntries => "binentries", InternalHashKind.BinFields => "binfields",
            InternalHashKind.BinTypes => "bintypes", InternalHashKind.BinHashes => "binhashes",
            InternalHashKind.RstXxh3 => "rst.xxh3.38", InternalHashKind.RstXxh64 => "rst.xxh64",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        private static string GetKnownFileName(InternalHashKind kind) => kind switch
        {
            InternalHashKind.BinEntries => "hashes.binentries.txt", InternalHashKind.BinFields => "hashes.binfields.txt",
            InternalHashKind.BinTypes => "hashes.bintypes.txt", InternalHashKind.BinHashes => "hashes.binhashes.txt",
            InternalHashKind.RstXxh3 => "hashes.rst.xxh3.txt", InternalHashKind.RstXxh64 => "hashes.rst.xxh64.txt",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }
}
