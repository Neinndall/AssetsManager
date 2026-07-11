using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes
{
    public sealed class HashGuessEngine
    {


        private readonly HashSet<ulong> _unknownHashes;
        private readonly Dictionary<ulong, HashGuessMatch> _matches = new();

        public HashGuessEngine(HashGuessDomain domain, HashSet<ulong> unknownHashes)
        {
            Domain = domain;
            _unknownHashes = unknownHashes ?? throw new ArgumentNullException(nameof(unknownHashes));
        }

        public HashGuessDomain Domain { get; }
        public IReadOnlyDictionary<ulong, HashGuessMatch> Matches => _matches;
        public int RemainingUnknownCount => _unknownHashes.Count;
        public IReadOnlyCollection<ulong> UnknownHashes => _unknownHashes;

        public bool Check(string candidate, HashGuessStrategy strategy, string source = "Generated", ulong sourceChunkHash = 0)
        {
            string path = NormalizePath(candidate);
            if (path.Length == 0) return false;

            ulong hash = XxHash64Ext.Hash(path);
            if (!_unknownHashes.Remove(hash)) return false;

            _matches[hash] = new HashGuessMatch
            {
                Hash = hash,
                Path = path,
                Domain = Domain,
                Strategy = strategy,
                SourceWadPath = source,
                SourceChunkHash = sourceChunkHash
            };
            return true;
        }

        public int CheckMany(IEnumerable<string> candidates, HashGuessStrategy strategy, string source = "Generated")
        {
            int checkedCandidates = 0;
            foreach (string candidate in candidates)
            {
                Check(candidate, strategy, source);
                checkedCandidates++;
                if (_unknownHashes.Count == 0) break;
            }
            return checkedCandidates;
        }

        public static IReadOnlyList<string> BuildWordlist(IEnumerable<string> paths)
        {
            return paths.AsParallel()
                .SelectMany(TokenizePath)
                .GroupBy(word => word, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .ToList();
        }

        public static IReadOnlyList<string> BuildBasenameWordlist(IEnumerable<string> paths, int minimumLength = 2, int maximumLength = 48)
        {
            return paths.AsParallel()
                .Select(path => System.IO.Path.GetFileNameWithoutExtension(path ?? string.Empty))
                .SelectMany(TokenizePath)
                .Where(word => word.Length >= minimumLength && word.Length <= maximumLength)
                .Where(word => word.Any(char.IsLetter))
                .GroupBy(word => word, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Key)
                .ToList();
        }

        public static IReadOnlyList<string> BuildContextualWordlist(
            IEnumerable<string> scopePaths,
            IEnumerable<string> fallbackPaths,
            int minimumLength = 2,
            int maximumLength = 48)
        {
            var score = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            static void AddTokens(Dictionary<string, int> scores, IEnumerable<string> paths, int weight, int min, int max)
            {
                foreach (string word in paths.SelectMany(TokenizePath))
                {
                    if (word.Length < min || word.Length > max || !word.Any(char.IsLetter)) continue;
                    scores.TryGetValue(word, out int current);
                    scores[word] = current + weight;
                }
            }

            AddTokens(score, fallbackPaths ?? Enumerable.Empty<string>(), 1, minimumLength, maximumLength);
            AddTokens(score, scopePaths ?? Enumerable.Empty<string>(), 6, minimumLength, maximumLength);

            return score.OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key)
                .ToList();
        }

        private static List<string> TokenizePath(string path)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(path)) return list;

            int start = 0;
            for (int i = 0; i <= path.Length; i++)
            {
                if (i == path.Length || path[i] == '/' || path[i] == '_' || path[i] == '.' || path[i] == '-')
                {
                    int length = i - start;
                    if (length > 0)
                    {
                        bool isNumericFilter = false;
                        if (length >= 3)
                        {
                            isNumericFilter = true;
                            for (int j = start; j < i; j++)
                            {
                                if (path[j] < '0' || path[j] > '9')
                                {
                                    isNumericFilter = false;
                                    break;
                                }
                            }
                        }

                        if (!isNumericFilter)
                        {
                            list.Add(path.Substring(start, length));
                        }
                    }
                    start = i + 1;
                }
            }
            return list;
        }

        public static IReadOnlyList<string> BuildDirectoryList(IEnumerable<string> paths)
        {
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                int separator = path?.LastIndexOf('/') ?? -1;
                while (separator > 0)
                {
                    directories.Add(path[..separator]);
                    separator = path.LastIndexOf('/', separator - 1);
                }
            }
            return directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string NormalizePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string path = value.Trim().Replace('\\', '/').ToLowerInvariant().Replace("data_soon/", "data/");
            return path.Contains("//", StringComparison.Ordinal) || path.Length > 512 ? string.Empty : path;
        }
    }
}
