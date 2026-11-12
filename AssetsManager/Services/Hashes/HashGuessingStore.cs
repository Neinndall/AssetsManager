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
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public HashGuessingStore(DirectoriesCreator directoriesCreator) => _directoriesCreator = directoriesCreator;

        public bool HasInventory(HashGuessDomain domain) =>
            File.Exists(GetInventoryPath(domain, "unknowns")) ||
            File.Exists(GetInventoryPath(domain, "historical")) ||
            File.Exists(GetInventoryPath(domain, "pending"));

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
                    if (!HasInventory(group.Key)) continue;
                    await RecoverInventoryAsync(group.Key);
                    var current = await ReadHashesAsync(GetInventoryPath(group.Key, "unknowns"), cancellationToken);
                    var historical = await ReadHashesAsync(GetInventoryPath(group.Key, "historical"), cancellationToken);
                    current.ExceptWith(newEntries.Keys);
                    historical.ExceptWith(newEntries.Keys);
                    string patchPath = GetInventoryPath(group.Key, "patch");
                    string patch = File.Exists(patchPath) ? await File.ReadAllTextAsync(patchPath, cancellationToken) : null;
                    await CommitInventoryAsync(group.Key, current, historical, patch, cancellationToken);
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<HashSet<ulong>> LoadUnknownHashesAsync(HashGuessDomain domain, CancellationToken cancellationToken,
            HashGuessScope scope = HashGuessScope.All)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                await RecoverInventoryAsync(domain);
                var result = scope == HashGuessScope.Historical ? new HashSet<ulong>() :
                    await ReadHashesAsync(GetInventoryPath(domain, "unknowns"), cancellationToken);
                if (scope != HashGuessScope.CurrentPatch)
                    result.UnionWith(await ReadHashesAsync(GetInventoryPath(domain, "historical"), cancellationToken));
                return result;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<int> LoadUnknownCountAsync(HashGuessDomain domain, CancellationToken cancellationToken,
            HashGuessScope scope = HashGuessScope.All)
        {
            var pending = await LoadUnknownHashesAsync(domain, cancellationToken, scope);
            return pending.Count;
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

        public async Task SaveUnknownHashesAsync(
            HashGuessDomain domain,
            IEnumerable<ulong> unknownHashes,
            CancellationToken cancellationToken,
            HashGuessScope scope = HashGuessScope.All)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                await RecoverInventoryAsync(domain);
                var pending = unknownHashes.ToHashSet();
                var historical = await ReadHashesAsync(GetInventoryPath(domain, "historical"), cancellationToken);
                var current = await ReadHashesAsync(GetInventoryPath(domain, "unknowns"), cancellationToken);
                if (scope != HashGuessScope.CurrentPatch) historical.IntersectWith(pending);
                if (scope != HashGuessScope.Historical) current = pending.Except(historical).ToHashSet();
                historical.ExceptWith(current);
                string patchPath = GetInventoryPath(domain, "patch");
                string patch = File.Exists(patchPath) ? await File.ReadAllTextAsync(patchPath, cancellationToken) : null;
                await CommitInventoryAsync(domain, current, historical, patch, cancellationToken);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task SaveScannedInventoryAsync(HashGuessDomain domain, IEnumerable<ulong> observed,
            string patch, Func<ulong, bool> isKnown, CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                await RecoverInventoryAsync(domain);
                var current = await ReadHashesAsync(GetInventoryPath(domain, "unknowns"), cancellationToken);
                var historical = await ReadHashesAsync(GetInventoryPath(domain, "historical"), cancellationToken);
                string patchPath = GetInventoryPath(domain, "patch");
                string previousPatch = File.Exists(patchPath) ? await File.ReadAllTextAsync(patchPath, cancellationToken) : null;
                // Unversioned inventories predate patch tracking and form the historical baseline.
                if (!string.Equals(previousPatch, patch, StringComparison.Ordinal))
                {
                    historical.UnionWith(current);
                    current.Clear();
                }
                historical.RemoveWhere(hash => isKnown(hash));
                current.UnionWith(observed);
                current.RemoveWhere(hash => isKnown(hash) || historical.Contains(hash));
                await CommitInventoryAsync(domain, current, historical, patch, cancellationToken);
            }
            finally
            {
                _lock.Release();
            }
        }

        private string GetInventoryPath(HashGuessDomain domain, string prefix) => Path.Combine(
            _directoriesCreator.HashLabPath, $"{prefix}.{(domain == HashGuessDomain.Game ? "game" : "lcu")}.txt");

        private static async Task<HashSet<ulong>> ReadHashesAsync(string path, CancellationToken cancellationToken)
        {
            var hashes = new HashSet<ulong>();
            if (File.Exists(path))
                foreach (string line in await File.ReadAllLinesAsync(path, cancellationToken))
                    if (ulong.TryParse(line.Trim(), System.Globalization.NumberStyles.HexNumber, null, out ulong hash))
                        hashes.Add(hash);
            return hashes;
        }

        private sealed record InventoryCommit(ulong[] Current, ulong[] Historical, string Patch);

        private async Task CommitInventoryAsync(HashGuessDomain domain, HashSet<ulong> current,
            HashSet<ulong> historical, string patch, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_directoriesCreator.HashLabPath);
            string journal = GetInventoryPath(domain, "pending");
            var commit = new InventoryCommit(current.OrderBy(h => h).ToArray(), historical.OrderBy(h => h).ToArray(), patch);
            await WriteAtomicAsync(journal, JsonSerializer.Serialize(commit), cancellationToken);
            // Once journaled, finish all projections together, including when cancellation arrives.
            await RecoverInventoryAsync(domain);
        }

        private async Task RecoverInventoryAsync(HashGuessDomain domain)
        {
            string journal = GetInventoryPath(domain, "pending");
            if (!File.Exists(journal)) return;
            var commit = JsonSerializer.Deserialize<InventoryCommit>(await File.ReadAllTextAsync(journal));
            await WriteAtomicAsync(GetInventoryPath(domain, "unknowns"), FormatHashes(commit.Current), CancellationToken.None);
            await WriteAtomicAsync(GetInventoryPath(domain, "historical"), FormatHashes(commit.Historical), CancellationToken.None);
            await WriteAtomicAsync(GetInventoryPath(domain, "patch"), commit.Patch ?? string.Empty, CancellationToken.None);
            File.Delete(journal);
        }

        private static string FormatHashes(IEnumerable<ulong> hashes) =>
            string.Concat(hashes.Select(hash => $"{hash:x16}\n"));

        private static async Task WriteAtomicAsync(string path, string text, CancellationToken cancellationToken)
        {
            string temporaryPath = path + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, text, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }
}
