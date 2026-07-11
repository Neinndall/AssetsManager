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
                    var newEntries = group.Select(match => $"{match.Hash:x16} {match.Path}").ToList();
                    var pathsToUpdate = new[] { GetConfirmedResearchPath(group.Key), GetKnownHashFilePath(group.Key) };

                    foreach (var targetPath in pathsToUpdate)
                    {
                        var dictionary = new Dictionary<ulong, string>();

                        // 1. Read existing hashes if file exists
                        if (File.Exists(targetPath))
                        {
                            foreach (string line in await File.ReadAllLinesAsync(targetPath, cancellationToken))
                            {
                                if (string.IsNullOrWhiteSpace(line) || line.Length < 17) continue;
                                string hex = line[..16];
                                string path = line[17..].Trim();
                                if (ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out ulong hash))
                                {
                                    dictionary[hash] = path;
                                }
                            }
                        }

                        // 2. Merge new entries
                        foreach (string entry in newEntries)
                        {
                            if (string.IsNullOrWhiteSpace(entry) || entry.Length < 17) continue;
                            string hex = entry[..16];
                            string path = entry[17..].Trim();
                            if (ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out ulong hash))
                            {
                                dictionary[hash] = path;
                            }
                        }

                        // 3. Sort alphabetically by path string (just like sorted(..., key=lambda kv: kv[1]))
                        var sortedLines = dictionary
                            .OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                            .Select(kv => $"{kv.Key:x16} {kv.Value}");

                        // 4. Overwrite target path
                        string tempPath = targetPath + ".tmp";
                        await File.WriteAllLinesAsync(tempPath, sortedLines, cancellationToken);
                        File.Move(tempPath, targetPath, true);
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

        public async Task SaveUnknownHashesAsync(HashGuessDomain domain, IEnumerable<ulong> unknownHashes, CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(_directoriesCreator.HashLabPath);
                string path = Path.Combine(_directoriesCreator.HashLabPath, domain == HashGuessDomain.Game ? "unknowns.game.txt" : "unknowns.lcu.txt");
                var lines = unknownHashes.Select(hash => $"{hash:x16}").OrderBy(x => x);
                await File.WriteAllLinesAsync(path, lines, cancellationToken);
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
