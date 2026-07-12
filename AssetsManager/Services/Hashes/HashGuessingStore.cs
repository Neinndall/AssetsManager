using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;

namespace AssetsManager.Services.Hashes
{
    public class HashGuessingStore
    {
        private const string ResearchFileName = "research.json";
        private const int RecentPatchThreshold = 3;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public HashGuessingStore(DirectoriesCreator directoriesCreator) => _directoriesCreator = directoriesCreator;

        private string GetKnownHashFilePath(HashGuessDomain domain) => Path.Combine(
            _directoriesCreator.HashesPath,
            domain == HashGuessDomain.Game ? "hashes.game.txt" : "hashes.lcu.txt");

        private string GetConfirmedResearchPath(HashGuessDomain domain) => Path.Combine(
            _directoriesCreator.HashLabPath,
            domain == HashGuessDomain.Game ? "confirmed.game.txt" : "confirmed.lcu.txt");

        public async Task SaveResearchMatchesAsync(IEnumerable<HashGuessMatch> matches, CancellationToken cancellationToken)
        {
            var incoming = matches?.ToList() ?? new List<HashGuessMatch>();
            if (incoming.Count == 0) return;

            await _lock.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(_directoriesCreator.HashLabPath);
                string path = Path.Combine(_directoriesCreator.HashLabPath, ResearchFileName);
                List<HashGuessMatch> existing = new();

                if (File.Exists(path))
                {
                    await using var source = File.OpenRead(path);
                    existing = await JsonSerializer.DeserializeAsync<List<HashGuessMatch>>(source, cancellationToken: cancellationToken) ?? new List<HashGuessMatch>();
                }

                var merged = existing.Concat(incoming)
                    .GroupBy(match => new { match.Domain, match.Hash })
                    .Select(group => group.OrderByDescending(match => match.FoundAtUtc).First())
                    .OrderBy(match => match.Domain)
                    .ThenBy(match => match.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                string temporaryPath = path + ".tmp";
                await using (var target = File.Create(temporaryPath))
                    await JsonSerializer.SerializeAsync(target, merged, cancellationToken: cancellationToken);

                File.Move(temporaryPath, path, true);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task SaveHashesAsync(IEnumerable<HashGuessMatch> matches, CancellationToken cancellationToken)
        {
            var grouped = (matches ?? Enumerable.Empty<HashGuessMatch>()).GroupBy(match => match.Domain);
            await _lock.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(_directoriesCreator.HashesPath);
                Directory.CreateDirectory(_directoriesCreator.HashLabPath);
                foreach (var group in grouped)
                {
                    var newEntries = group.GroupBy(match => match.Hash)
                        .ToDictionary(matchesByHash => matchesByHash.Key, matchesByHash => matchesByHash.Last().Path);
                    var pathsToUpdate = new[] { GetConfirmedResearchPath(group.Key), GetKnownHashFilePath(group.Key) };

                    foreach (var targetPath in pathsToUpdate)
                    {
                        await MergeHashFileAsync(targetPath, newEntries, cancellationToken);
                    }
                }
            }
            finally
            {
                _lock.Release();
            }
        }



        public async Task<HashSet<ulong>> LoadUnknownHashesAsync(HashGuessDomain domain, CancellationToken cancellationToken)
        {
            var result = new HashSet<ulong>();
            string path = Path.Combine(_directoriesCreator.HashLabPath, domain == HashGuessDomain.Game ? "unknowns.game.txt" : "unknowns.lcu.txt");
            if (!File.Exists(path)) return result;

            await _lock.WaitAsync(cancellationToken);
            try
            {
                foreach (string line in await File.ReadAllLinesAsync(path, cancellationToken))
                {
                    if (ulong.TryParse(line.Trim(), System.Globalization.NumberStyles.HexNumber, null, out ulong hash))
                    {
                        result.Add(hash);
                    }
                }
            }
            finally
            {
                _lock.Release();
            }
            return result;
        }

        public async Task<HashUnknownSummary> LoadUnknownSummaryAsync(HashGuessDomain domain, CancellationToken cancellationToken)
        {
            string suffix = domain == HashGuessDomain.Game ? "game" : "lcu";
            await _lock.WaitAsync(cancellationToken);
            try
            {
                int current = await CountHashLinesAsync(Path.Combine(_directoriesCreator.HashLabPath, $"current.{suffix}.txt"), cancellationToken);
                int recent = await CountHashLinesAsync(Path.Combine(_directoriesCreator.HashLabPath, $"recent.{suffix}.txt"), cancellationToken);
                int historical = await CountHashLinesAsync(Path.Combine(_directoriesCreator.HashLabPath, $"historical.{suffix}.txt"), cancellationToken);
                if (current + recent + historical == 0)
                {
                    string legacyPath = Path.Combine(_directoriesCreator.HashLabPath, $"unknowns.{suffix}.txt");
                    current = await CountHashLinesAsync(legacyPath, cancellationToken);
                }
                return new HashUnknownSummary { Current = current, Recent = recent, Historical = historical };
            }
            finally
            {
                _lock.Release();
            }
        }

        internal static Task MergeHashFileAsync(string targetPath, IReadOnlyDictionary<ulong, string> incoming, CancellationToken cancellationToken)
        {
            return Task.Run(() => MergeHashFile(targetPath, incoming, cancellationToken), cancellationToken);
        }

        private static void MergeHashFile(string targetPath, IReadOnlyDictionary<ulong, string> incoming, CancellationToken cancellationToken)
        {
            var additions = incoming.OrderBy(pair => pair.Value, StringComparer.Ordinal).ToList();
            string temporaryPath = targetPath + ".tmp";
            try
            {
                using (var reader = File.Exists(targetPath) ? new StreamReader(targetPath) : null)
                using (var writer = new StreamWriter(temporaryPath, false, new System.Text.UTF8Encoding(false)))
                {
                    int additionIndex = 0;
                    string line;
                    while (reader != null && (line = reader.ReadLine()) != null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!TryParseHashLine(line, out ulong existingHash, out string existingPath)) continue;
                        if (incoming.ContainsKey(existingHash)) continue;

                        while (additionIndex < additions.Count &&
                               StringComparer.Ordinal.Compare(additions[additionIndex].Value, existingPath) <= 0)
                        {
                            var addition = additions[additionIndex++];
                            writer.WriteLine($"{addition.Key:x16} {addition.Value}");
                        }
                        writer.WriteLine(line);
                    }

                    while (additionIndex < additions.Count)
                    {
                        var addition = additions[additionIndex++];
                        writer.WriteLine($"{addition.Key:x16} {addition.Value}");
                    }
                    writer.Flush();
                }
                File.Move(temporaryPath, targetPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private static bool TryParseHashLine(string line, out ulong hash, out string path)
        {
            hash = 0;
            path = string.Empty;
            if (string.IsNullOrWhiteSpace(line) || line.Length < 18 || line[16] != ' ') return false;
            if (!ulong.TryParse(line.AsSpan(0, 16), System.Globalization.NumberStyles.HexNumber, null, out hash)) return false;
            path = line[17..].Trim();
            return path.Length > 0;
        }

        private static async Task<int> CountHashLinesAsync(string path, CancellationToken cancellationToken)
        {
            if (!File.Exists(path)) return 0;
            int count = 0;
            using var reader = new StreamReader(path);
            while (await reader.ReadLineAsync(cancellationToken) != null) count++;
            return count;
        }

        public async Task SaveUnknownHashesAsync(
            HashGuessDomain domain,
            IEnumerable<ulong> unknownHashes,
            IReadOnlySet<ulong> currentHashes,
            string patchFingerprint,
            CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(_directoriesCreator.HashLabPath);
                string path = Path.Combine(_directoriesCreator.HashLabPath, domain == HashGuessDomain.Game ? "unknowns.game.txt" : "unknowns.lcu.txt");
                var pending = unknownHashes.ToHashSet();
                var lines = pending.Select(hash => $"{hash:x16}").OrderBy(x => x).ToList();
                string temporaryPath = path + ".tmp";
                try
                {
                    await File.WriteAllLinesAsync(temporaryPath, lines, cancellationToken);
                    File.Move(temporaryPath, path, true);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }

                await SaveInventoryMetadataAsync(domain, pending, currentHashes, patchFingerprint, cancellationToken);
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task SaveInventoryMetadataAsync(
            HashGuessDomain domain,
            HashSet<ulong> pending,
            IReadOnlySet<ulong> currentHashes,
            string patchFingerprint,
            CancellationToken cancellationToken)
        {
            string suffix = domain == HashGuessDomain.Game ? "game" : "lcu";
            string metadataPath = Path.Combine(_directoriesCreator.HashLabPath, $"inventory.{suffix}.json");
            var existing = new List<HashUnknownRecord>();
            if (File.Exists(metadataPath))
            {
                await using var source = File.OpenRead(metadataPath);
                var stored = await JsonSerializer.DeserializeAsync<List<HashUnknownRecord>>(source, cancellationToken: cancellationToken);
                if (stored != null) existing = stored;
            }

            var ordered = UpdateInventoryRecords(domain, existing, pending, currentHashes, patchFingerprint);
            await WriteJsonAtomicallyAsync(metadataPath, ordered, cancellationToken);

            await WriteHashViewAsync(Path.Combine(_directoriesCreator.HashLabPath, $"current.{suffix}.txt"), ordered.Where(record => record.MissedPatchCount == 0), cancellationToken);
            await WriteHashViewAsync(Path.Combine(_directoriesCreator.HashLabPath, $"recent.{suffix}.txt"), ordered.Where(record => record.MissedPatchCount is > 0 and <= RecentPatchThreshold), cancellationToken);
            await WriteHashViewAsync(Path.Combine(_directoriesCreator.HashLabPath, $"historical.{suffix}.txt"), ordered.Where(record => record.MissedPatchCount > RecentPatchThreshold), cancellationToken);
        }

        internal static List<HashUnknownRecord> UpdateInventoryRecords(
            HashGuessDomain domain,
            IEnumerable<HashUnknownRecord> existing,
            IReadOnlySet<ulong> pending,
            IReadOnlySet<ulong> currentHashes,
            string patchFingerprint)
        {
            var records = existing.ToDictionary(record => record.Hash);
            foreach (ulong hash in pending)
            {
                if (!records.TryGetValue(hash, out HashUnknownRecord record))
                {
                    record = new HashUnknownRecord
                    {
                        Hash = hash,
                        Domain = domain,
                        FirstSeenPatch = currentHashes.Contains(hash) ? patchFingerprint : "legacy"
                    };
                    records[hash] = record;
                }

                if (currentHashes.Contains(hash))
                {
                    if (!string.Equals(record.LastSeenPatch, patchFingerprint, StringComparison.Ordinal))
                        record.SeenPatchCount++;
                    record.LastSeenPatch = patchFingerprint;
                    record.LastObservedPatch = patchFingerprint;
                    record.MissedPatchCount = 0;
                }
                else if (!string.Equals(record.LastObservedPatch, patchFingerprint, StringComparison.Ordinal))
                {
                    record.LastObservedPatch = patchFingerprint;
                    record.MissedPatchCount++;
                }
            }

            foreach (ulong resolved in records.Keys.Where(hash => !pending.Contains(hash)).ToList()) records.Remove(resolved);
            return records.Values.OrderBy(record => record.Hash).ToList();
        }

        private static async Task WriteJsonAtomicallyAsync(string path, List<HashUnknownRecord> records, CancellationToken cancellationToken)
        {
            string temporaryPath = path + ".tmp";
            try
            {
                await using (var target = File.Create(temporaryPath))
                    await JsonSerializer.SerializeAsync(target, records, cancellationToken: cancellationToken);
                File.Move(temporaryPath, path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private static async Task WriteHashViewAsync(string path, IEnumerable<HashUnknownRecord> records, CancellationToken cancellationToken)
        {
            string temporaryPath = path + ".tmp";
            try
            {
                await File.WriteAllLinesAsync(temporaryPath, records.Select(record => $"{record.Hash:x16}"), cancellationToken);
                File.Move(temporaryPath, path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }
}
