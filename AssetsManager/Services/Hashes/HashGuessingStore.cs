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
        private const int RecentPatchThreshold = 3;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public HashGuessingStore(DirectoriesCreator directoriesCreator) => _directoriesCreator = directoriesCreator;

        private string GetKnownHashFilePath(HashGuessDomain domain) => Path.Combine(
            _directoriesCreator.HashesPath,
            domain == HashGuessDomain.Game ? "hashes.game.txt" : "hashes.lcu.txt");

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
                    
                    await MergeHashFileAsync(GetKnownHashFilePath(group.Key), newEntries, cancellationToken);
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

        public async Task<HashUnknownInventory> LoadUnknownInventoryAsync(HashGuessDomain domain, CancellationToken cancellationToken)
        {
            string suffix = domain == HashGuessDomain.Game ? "game" : "lcu";
            string unknownPath = Path.Combine(_directoriesCreator.HashLabPath, $"unknowns.{suffix}.txt");
            string metadataPath = Path.Combine(_directoriesCreator.HashLabPath, $"inventory.{suffix}.json");
            if (!File.Exists(unknownPath) || !File.Exists(metadataPath)) return null;

            await _lock.WaitAsync(cancellationToken);
            try
            {
                var pending = await ReadHashSetAsync(unknownPath, cancellationToken);
                await using var source = File.OpenRead(metadataPath);
                var records = await JsonSerializer.DeserializeAsync<List<HashUnknownRecord>>(source, cancellationToken: cancellationToken)
                    ?? new List<HashUnknownRecord>();
                var current = records.Where(record => record.MissedPatchCount == 0 && pending.Contains(record.Hash))
                    .Select(record => record.Hash)
                    .ToHashSet();
                string fingerprint = records.Select(record => record.LastObservedPatch)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
                return new HashUnknownInventory { All = pending, Current = current, PatchFingerprint = fingerprint };
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<HashUnknownSummary> LoadUnknownSummaryAsync(HashGuessDomain domain, CancellationToken cancellationToken)
        {
            string suffix = domain == HashGuessDomain.Game ? "game" : "lcu";
            string metadataPath = Path.Combine(_directoriesCreator.HashLabPath, $"inventory.{suffix}.json");
            
            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (File.Exists(metadataPath))
                {
                    await using var source = File.OpenRead(metadataPath);
                    var records = await JsonSerializer.DeserializeAsync<List<HashUnknownRecord>>(source, cancellationToken: cancellationToken);
                    if (records != null && records.Count > 0)
                    {
                        int current = records.Count(r => r.MissedPatchCount == 0);
                        int recent = records.Count(r => r.MissedPatchCount is > 0 and <= RecentPatchThreshold);
                        int historical = records.Count(r => r.MissedPatchCount > RecentPatchThreshold);
                        return new HashUnknownSummary { Current = current, Recent = recent, Historical = historical };
                    }
                }

                string legacyPath = Path.Combine(_directoriesCreator.HashLabPath, $"unknowns.{suffix}.txt");
                int count = await CountHashLinesAsync(legacyPath, cancellationToken);
                return new HashUnknownSummary { Current = count, Recent = 0, Historical = 0 };
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
                    writer.NewLine = "\n";
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

        private static async Task<HashSet<ulong>> ReadHashSetAsync(string path, CancellationToken cancellationToken)
        {
            var hashes = new HashSet<ulong>();
            foreach (string line in await File.ReadAllLinesAsync(path, cancellationToken))
            {
                if (ulong.TryParse(line.Trim(), System.Globalization.NumberStyles.HexNumber, null, out ulong hash))
                    hashes.Add(hash);
            }
            return hashes;
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
    }
}
