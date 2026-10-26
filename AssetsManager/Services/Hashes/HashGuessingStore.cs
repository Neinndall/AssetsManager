using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        public async Task<int> LoadUnknownCountAsync(HashGuessDomain domain, CancellationToken cancellationToken)
        {
            var pending = await LoadUnknownHashesAsync(domain, cancellationToken);
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
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
