using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using System.Text.RegularExpressions;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes
{
    public sealed class HashGuessEngine
    {


        private readonly HashSet<ulong> _unknownHashes;
        private readonly Dictionary<ulong, HashGuessMatch> _matches = new();
        private readonly Action<HashGuessMatch> _matchFound;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public HashGuessEngine(HashGuessDomain domain, HashSet<ulong> unknownHashes, Action<HashGuessMatch> matchFound = null)
        {
            Domain = domain;
            _unknownHashes = unknownHashes ?? throw new ArgumentNullException(nameof(unknownHashes));
            _matchFound = matchFound;
        }

        public HashGuessDomain Domain { get; }
        public IReadOnlyDictionary<ulong, HashGuessMatch> Matches => _matches;
        public int RemainingUnknownCount => _unknownHashes.Count;
        public IReadOnlyCollection<ulong> UnknownHashes => _unknownHashes;
        public long CheckedCandidates { get; private set; }
        public long DiscardedCandidates { get; private set; }

        public bool Check(string candidate, HashGuessStrategy strategy, string source = "Generated", ulong sourceChunkHash = 0)
        {
            return CheckExact(candidate, strategy, source, sourceChunkHash);
        }

        internal bool CheckExact(string path, HashGuessStrategy strategy, string source = "Generated", ulong sourceChunkHash = 0)
        {
            CheckedCandidates++;
            string normalizedPath = PathUtils.NormalizePath(path);
            if (normalizedPath.Length == 0)
            {
                DiscardedCandidates++;
                return false;
            }

            ulong hash = XxHash64Ext.Hash(normalizedPath);
            if (!_unknownHashes.Remove(hash))
            {
                DiscardedCandidates++;
                return false;
            }

            AddMatch(hash, normalizedPath, strategy, source, sourceChunkHash);
            return true;
        }

        internal bool CheckCombined(
            string directory,
            string relativePath,
            HashGuessStrategy strategy,
            string source,
            ulong sourceChunkHash)
        {
            CheckedCandidates++;
            string combinedPath = string.IsNullOrEmpty(directory)
                ? relativePath
                : string.Concat(directory, "/", relativePath);
            string normalizedPath = PathUtils.NormalizePath(combinedPath);
            if (normalizedPath.Length == 0)
            {
                DiscardedCandidates++;
                return false;
            }

            int byteCount = Encoding.UTF8.GetByteCount(normalizedPath);
            byte[] rented = null;
            Span<byte> utf8 = byteCount <= 1024
                ? stackalloc byte[byteCount]
                : (rented = ArrayPool<byte>.Shared.Rent(byteCount));
            try
            {
                int written = Encoding.UTF8.GetBytes(normalizedPath, utf8);
                ulong hash = XxHash64.HashToUInt64(utf8[..written]);
                if (!_unknownHashes.Remove(hash))
                {
                    DiscardedCandidates++;
                    return false;
                }

                AddMatch(hash, normalizedPath, strategy, source, sourceChunkHash);
                return true;
            }
            finally
            {
                if (rented != null) ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private void AddMatch(
            ulong hash,
            string path,
            HashGuessStrategy strategy,
            string source,
            ulong sourceChunkHash)
        {
            var match = new HashGuessMatch
            {
                Hash = hash,
                Path = path,
                Domain = Domain,
                Strategy = strategy,
                SourceWadPath = source,
                SourceChunkHash = sourceChunkHash
            };
            _matches[hash] = match;
            _matchFound?.Invoke(match);
        }

        public HashGuessProgress CreateProgress(
            string stage,
            int processedChunks = 0,
            int processedWads = 0,
            int totalWads = 0)
        {
            TimeSpan elapsed = _stopwatch.Elapsed;
            return new HashGuessProgress
            {
                ProcessedWads = processedWads,
                TotalWads = totalWads,
                ProcessedChunks = processedChunks,
                FoundMatches = _matches.Count,
                RemainingUnknowns = _unknownHashes.Count,
                CurrentWad = stage,
                CheckedCandidates = CheckedCandidates,
                DiscardedCandidates = DiscardedCandidates,
                CandidatesPerSecond = elapsed.TotalSeconds > 0 ? CheckedCandidates / elapsed.TotalSeconds : 0,
                Elapsed = elapsed,
                ManagedMemoryBytes = GC.GetTotalMemory(false)
            };
        }

        public static IReadOnlyList<string> BuildWordlist(IEnumerable<string> paths)
        {
            var words = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                List<string> tokens = TokenizePath(path);
                for (int index = 0; index < tokens.Count - 1; index++)
                    words.Add(tokens[index]);
            }

            return words.OrderBy(word => word, StringComparer.Ordinal).ToList();
        }

        public static IReadOnlyList<string> BuildBasenameWordlist(IEnumerable<string> paths, int minimumLength = 1, int maximumLength = 48)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                string basename = System.IO.Path.GetFileNameWithoutExtension(path ?? string.Empty);
                foreach (string word in TokenizePath(basename))
                {
                    if (word.Length < minimumLength || word.Length > maximumLength) continue;
                    counts.TryGetValue(word, out int count);
                    counts[word] = count + 1;
                }
            }
            return counts.OrderByDescending(pair => pair.Value)
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
            var directories = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                int separator = path?.LastIndexOf('/') ?? -1;
                while (separator >= 0)
                {
                    directories.Add(path[..separator]);
                    if (separator == 0) break;
                    separator = path.LastIndexOf('/', separator - 1);
                }
            }
            directories.Add(string.Empty);
            return directories.OrderBy(path => path, StringComparer.Ordinal).ToList();
        }

        public static IReadOnlyList<string> BuildRankedBasenames(IEnumerable<string> paths)
        {
            return paths.Select(System.IO.Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key)
                .ToList();
        }

        public static IReadOnlyList<string> BuildRankedDirectoryList(IEnumerable<string> paths)
        {
            var scores = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                int separator = path?.LastIndexOf('/') ?? -1;
                while (separator >= 0)
                {
                    string directory = path[..separator];
                    scores.TryGetValue(directory, out int score);
                    scores[directory] = score + 1;
                    if (separator == 0) break;
                    separator = path.LastIndexOf('/', separator - 1);
                }
            }
            scores.TryAdd(string.Empty, 0);
            return scores.OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key)
                .ToList();
        }

    }
}
