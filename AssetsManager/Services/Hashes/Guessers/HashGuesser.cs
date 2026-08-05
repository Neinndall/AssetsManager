using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes.Guessers
{
    internal readonly record struct HashGuessCandidate(string Path, HashGuessStrategy Strategy);
    internal sealed record HashWadInventory(IReadOnlyList<string> WadPaths, HashSet<ulong> Hashes, long ChunkCount, ulong HashXor, ulong HashSum);

    internal abstract class HashGuesser
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private readonly object _corpusSync = new();
        private HashCorpusIndex _corpus;

        protected HashGuesser(HashFile hashFile, string wadPattern)
        {
            HashFile = hashFile ?? throw new ArgumentNullException(nameof(hashFile));
            Domain = hashFile.Domain;
            WadPattern = wadPattern;
        }

        protected HashFile HashFile { get; }
        internal HashGuessDomain Domain { get; }
        internal string WadPattern { get; }
        protected HashCorpusIndex Corpus
        {
            get
            {
                IReadOnlyList<string> paths = HashFile.LoadPaths();
                long revision = HashFile.Revision;
                lock (_corpusSync)
                {
                    if (_corpus == null || _corpus.Revision != revision)
                        _corpus = new HashCorpusIndex(revision, paths);
                    return _corpus;
                }
            }
        }

        internal IReadOnlyList<string> KnownPaths => Corpus.Paths;


        internal static HashSet<ulong> UnknownFromExport(string directory) =>
            HashFile.LoadUnknownFromExport(directory);

        internal IReadOnlyList<string> DirectoryList() =>
            Corpus.GetOrCreate("directories", HashGuessEngine.BuildDirectoryList);

        internal string[] FindWads(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
                throw new DirectoryNotFoundException("The selected game directory does not exist.");

            string searchPath = rootDirectory;
            if (WadPattern == "*.wad")
            {
                string pluginsPath = Path.Combine(rootDirectory, "Plugins");
                if (Directory.Exists(pluginsPath))
                {
                    searchPath = pluginsPath;
                }
            }
            else if (WadPattern == "*.wad.client")
            {
                string gamePath = Path.Combine(rootDirectory, "Game");
                if (Directory.Exists(gamePath))
                {
                    searchPath = gamePath;
                }
            }

            return Directory.EnumerateFiles(searchPath, WadPattern, SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        internal HashWadInventory FromWads(
            IEnumerable<string> wadPaths,
            CancellationToken cancellationToken,
            Action<string, Exception> onUnreadableWad = null)
        {
            string expectedSuffix = WadPattern.TrimStart('*');
            string[] paths = wadPaths
                .Where(path => path.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var hashes = new HashSet<ulong>();
            ulong hashXor = 0;
            ulong hashSum = 0;
            long chunkCount = 0;

            foreach (string wadPath in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var stream = File.OpenRead(wadPath);
                    using var wad = new WadFile(stream);
                    foreach (ulong hash in wad.Chunks.Keys)
                    {
                        hashes.Add(hash);
                        hashXor ^= hash;
                        hashSum = unchecked(hashSum + hash);
                        chunkCount++;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    onUnreadableWad?.Invoke(wadPath, exception);
                }
            }

            return new HashWadInventory(paths, hashes, chunkCount, hashXor, hashSum);
        }

        internal static bool TryDecodeWadText(ArraySegment<byte> data, out string text)
        {
            text = string.Empty;
            if (data.Count == 0) return false;
            try
            {
                text = StrictUtf8.GetString(data.Array, data.Offset, data.Count);
                if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
                return text.Length > 0;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        internal abstract bool ShouldSkip(string extension);
        internal abstract IReadOnlyList<string> BuildWordlist();
        internal abstract void GrepWad(HashGuessEngine engine, ArraySegment<byte> data, string sourcePath, string sourceWadPath, ulong sourceChunkHash);

        internal void CheckChunk(
            HashGuessEngine engine,
            ArraySegment<byte> data,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash)
        {
            if (engine.Domain != Domain)
                throw new InvalidOperationException($"Cannot use the {Domain} guesser with a {engine.Domain} engine.");

            foreach (HashGuessCandidate candidate in ExtractCandidates(data, sourcePath))
            {
                CheckCandidate(engine, candidate, sourceWadPath, sourceChunkHash);
            }
        }

        protected virtual void CheckCandidate(
            HashGuessEngine engine,
            HashGuessCandidate candidate,
            string sourceWadPath,
            ulong sourceChunkHash)
        {
            CheckIter(engine, ExpandCandidate(candidate), candidate.Strategy, sourceWadPath, sourceChunkHash);
        }

        protected abstract IEnumerable<HashGuessCandidate> ExtractCandidates(ArraySegment<byte> data, string sourcePath);

        internal abstract IEnumerable<HashGuessCandidate> GenerateCanonicalCandidates(HashGuesser otherDomain, int candidateBudget = int.MaxValue);
        internal abstract IEnumerable<HashGuessCandidate> GenerateLanguageCandidates(int candidateBudget = int.MaxValue);

        internal virtual IEnumerable<HashGuessCandidate> GenerateNumberCandidates(
            int numberLimit,
            int candidateBudget = int.MaxValue,
            int? digits = null,
            bool inferDigits = false,
            bool includeCommonPadding = true) =>
            GenerateNumberCandidates(KnownPaths, numberLimit, candidateBudget, digits, inferDigits, includeCommonPadding);

        protected IEnumerable<HashGuessCandidate> GenerateNumberCandidates(
            IEnumerable<string> knownPaths,
            int numberLimit,
            int candidateBudget,
            int? digits = null,
            bool inferDigits = false,
            bool includeCommonPadding = true)
        {
            if (numberLimit < 0) throw new ArgumentOutOfRangeException(nameof(numberLimit));
            if (candidateBudget < 0) throw new ArgumentOutOfRangeException(nameof(candidateBudget));
            if (digits is < 1) throw new ArgumentOutOfRangeException(nameof(digits));

            int? effectiveDigits = inferDigits
                ? Math.Max(1, Math.Max(0, numberLimit - 1).ToString(CultureInfo.InvariantCulture).Length)
                : digits;
            string digitExpression = effectiveDigits.HasValue ? $"[0-9]{{{effectiveDigits.Value}}}" : "[0-9]+";
            string numberPattern = digitExpression;
            if (AnchorNumberMatchesToFileName) numberPattern += @"(?=[^/]*\.[^/]+$)";
            var formats = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in knownPaths)
            {
                if (!IncludeNumberPath(path)) continue;
                foreach (Match match in Regex.Matches(path, numberPattern))
                {
                    formats.Add(path[..match.Index] + "{number}" + path[(match.Index + match.Length)..]);
                }
            }

            int generated = 0;
            var orderedFormats = formats.OrderBy(path => path, StringComparer.Ordinal).ToList();
            foreach (string format in orderedFormats)
            for (int value = 0; value < numberLimit; value++)
            {
                foreach (string candidate in FormatNumberVariants(format, value, effectiveDigits, includeCommonPadding))
                {
                    yield return new HashGuessCandidate(candidate, HashGuessStrategy.NumberVariant);
                    if (CountCandidate(ref generated, candidateBudget)) yield break;
                }
            }
        }

        protected virtual bool IncludeNumberPath(string path) => true;

        protected virtual bool AnchorNumberMatchesToFileName => true;

        internal IEnumerable<HashGuessCandidate> GenerateExtensionCandidates(int candidateBudget = int.MaxValue)
        {
            var paths = Corpus.GetOrCreate("extension-paths", BuildExtensionPaths);
            return GenerateExtensionCandidates(paths.Prefixes, paths.Extensions, candidateBudget);
        }

        protected static IEnumerable<HashGuessCandidate> GenerateExtensionCandidates(IEnumerable<string> knownPaths, int candidateBudget)
        {
            var paths = BuildExtensionPaths(knownPaths);
            return GenerateExtensionCandidates(paths.Prefixes, paths.Extensions, candidateBudget);
        }

        private static (IReadOnlyList<string> Prefixes, IReadOnlyList<string> Extensions) BuildExtensionPaths(IEnumerable<string> knownPaths)
        {
            var paths = knownPaths as IReadOnlyList<string> ?? knownPaths.ToList();
            var extensions = paths.Select(Path.GetExtension)
                .Where(extension => extension.Length > 0 && !extension.EndsWith("00", StringComparison.Ordinal))
                .GroupBy(extension => extension, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key)
                .ToList();
            var prefixes = paths.Select(path => Path.ChangeExtension(path, null))
                .Where(prefix => !string.IsNullOrEmpty(prefix))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(prefix => prefix, StringComparer.Ordinal)
                .ToList();
            return (prefixes, extensions);
        }

        private static IEnumerable<HashGuessCandidate> GenerateExtensionCandidates(
            IReadOnlyList<string> prefixes,
            IReadOnlyList<string> extensions,
            int candidateBudget)
        {
            int generated = 0;
            foreach (string prefix in prefixes)
            foreach (string extension in extensions)
            {
                yield return new HashGuessCandidate(prefix + extension, HashGuessStrategy.ExtensionVariant);
                if (CountCandidate(ref generated, candidateBudget)) yield break;
            }
        }

        internal bool Check(HashGuessEngine engine, string path, HashGuessStrategy strategy, string source = "Generated") =>
            engine.CheckExact(path, strategy, source);

        internal bool IsKnown(HashGuessEngine engine, string path, HashGuessStrategy strategy, string source = "Generated")
        {
            ulong hash = XxHash64Ext.Hash(PathUtils.NormalizePath(path));
            return engine.CheckExact(path, strategy, source) || HashFile.Load().ContainsKey(hash);
        }

        internal int CheckIter(
            HashGuessEngine engine,
            IEnumerable<string> paths,
            HashGuessStrategy strategy,
            string source = "Generated",
            ulong sourceChunkHash = 0)
        {
            ArgumentNullException.ThrowIfNull(paths);
            int checkedCount = 0;
            foreach (string path in paths)
            {
                engine.CheckExact(path, strategy, source, sourceChunkHash);
                checkedCount++;
                if (engine.RemainingUnknownCount == 0) break;
            }
            return checkedCount;
        }

        internal int CheckIter(
            HashGuessEngine engine,
            IEnumerable<HashGuessCandidate> candidates,
            string source,
            CancellationToken cancellationToken,
            Action<int> progress = null,
            int progressInterval = 0)
        {
            ArgumentNullException.ThrowIfNull(candidates);
            int checkedCount = 0;
            foreach (HashGuessCandidate candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                engine.CheckExact(candidate.Path, candidate.Strategy, source);
                checkedCount++;
                if (progressInterval > 0 && checkedCount % progressInterval == 0)
                    progress?.Invoke(checkedCount);
                if (engine.RemainingUnknownCount == 0) break;
            }
            return checkedCount;
        }

        internal int CheckTextList(HashGuessEngine engine, string text, HashGuessStrategy strategy, string source = "Text list") =>
            CheckIter(engine, text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries), strategy, source);

        internal int CheckXdbgHashes(HashGuessEngine engine, string path, HashGuessStrategy strategy = HashGuessStrategy.EmbeddedPathGrep)
        {
            if (!File.Exists(path)) return 0;
            var candidates = File.ReadLines(path)
                .Where(line => line.StartsWith("hash: ", StringComparison.Ordinal))
                .Select(line => line.Split('"'))
                .Where(parts => parts.Length > 1)
                .Select(parts => parts[1]);
            return CheckIter(engine, candidates, strategy, "XDBG hashes");
        }

        internal int SubstituteBasenames(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            int? maxNames = null,
            int? maxDirectories = null,
            int candidateBudget = int.MaxValue,
            Action<int> progress = null)
        {
            IEnumerable<string> names = Corpus.GetOrCreate("basenames", paths => paths.Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList());
            IEnumerable<string> directories = DirectoryList();
            if (maxNames.HasValue) names = names.Take(Math.Max(0, maxNames.Value));
            if (maxDirectories.HasValue) directories = directories.Take(Math.Max(0, maxDirectories.Value));
            var directoryList = directories.ToList();

            int checkedCount = 0;
            int lastReportedCount = -1;
            var progressClock = Stopwatch.StartNew();
            foreach (string name in names)
            foreach (string directory in directoryList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                engine.CheckCombined(directory, name, HashGuessStrategy.PluginVariant, "Basename substitution", 0);
                bool budgetReached = CountCandidate(ref checkedCount, candidateBudget);
                if ((checkedCount & 0x3fff) == 0 && progressClock.ElapsedMilliseconds >= 100)
                {
                    progress?.Invoke(checkedCount);
                    lastReportedCount = checkedCount;
                    progressClock.Restart();
                }
                if (budgetReached || engine.RemainingUnknownCount == 0)
                {
                    if (lastReportedCount != checkedCount) progress?.Invoke(checkedCount);
                    return checkedCount;
                }
            }
            if (lastReportedCount != checkedCount) progress?.Invoke(checkedCount);
            return checkedCount;
        }

        internal static int RunFocusedWordlistSubstitution(HashGuessEngine engine, IEnumerable<string> paths, IEnumerable<string> words, CancellationToken cancellationToken, int candidateBudget = 500_000)
            => RunBasenameWordSubstitution(engine, paths, words, 1, 1, cancellationToken, candidateBudget, "Focused Wordlist");

        internal static int RunFocusedWordlistDoubleSubstitution(HashGuessEngine engine, IEnumerable<string> paths, IEnumerable<string> words, CancellationToken cancellationToken, int candidateBudget = 500_000)
            => RunBasenameWordSubstitution(engine, paths, words.Take(150), 2, 2, cancellationToken, candidateBudget, "Double Wordlist");

        internal static IReadOnlyList<(string Prefix, string Suffix)> BuildBasenameWordFormats(IEnumerable<string> paths, int oldWordCount, int newWordCount)
        {
            if (oldWordCount < 1) throw new ArgumentOutOfRangeException(nameof(oldWordCount));
            if (newWordCount < 1) throw new ArgumentOutOfRangeException(nameof(newWordCount));
            var counts = new Dictionary<(string Prefix, string Suffix), int>();
            var regex = new Regex($@"([^/_.-]+)(?=((?:[-_][^/_.-]+){{{oldWordCount - 1}}})[^/]*\.[^/]+$)", RegexOptions.Compiled);
            foreach (string path in paths)
            {
                if (path.Contains('%')) continue;
                foreach (Match match in regex.Matches(path))
                {
                    int matchedLength = match.Groups[1].Length + match.Groups[2].Length;
                    var format = (path[..match.Index], path[(match.Index + matchedLength)..]);
                    counts.TryGetValue(format, out int support);
                    counts[format] = support + 1;
                }
            }
            return counts.OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key.Prefix, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Suffix, StringComparer.Ordinal)
                .Select(pair => pair.Key)
                .ToList();
        }

        internal static int RunBasenameWordSubstitution(
            HashGuessEngine engine,
            IEnumerable<string> paths,
            IEnumerable<string> words,
            int oldWordCount,
            int newWordCount,
            CancellationToken cancellationToken,
            int candidateBudget = 500_000,
            string source = "Wordlist substitution",
            Action<int> progress = null)
        {
            var formats = BuildBasenameWordFormats(paths, oldWordCount, newWordCount);
            return RunBasenameWordSubstitutionFormats(engine, formats, words, newWordCount, cancellationToken, candidateBudget, source, progress);
        }

        internal static int RunBasenameWordSubstitutionFormats(
            HashGuessEngine engine,
            IReadOnlyList<(string Prefix, string Suffix)> formats,
            IEnumerable<string> words,
            int newWordCount,
            CancellationToken cancellationToken,
            int candidateBudget = 500_000,
            string source = "Wordlist substitution",
            Action<int> progress = null)
        {
            var wordsList = words.Where(word => !string.IsNullOrEmpty(word)).Distinct(StringComparer.Ordinal).ToList();
            if (formats.Count == 0 || wordsList.Count == 0) return 0;
            int checkedCount = 0;
            foreach ((string prefix, string suffix) in formats)
            foreach (string separator in newWordCount == 1 ? new[] { string.Empty } : new[] { "-", "_" })
            foreach (IReadOnlyList<string> combination in EnumerateWordCombinations(wordsList, newWordCount))
            {
                cancellationToken.ThrowIfCancellationRequested();
                engine.CheckExact(prefix + string.Join(separator, combination) + suffix, HashGuessStrategy.WordlistVariant, source);
                if (checkedCount > 0 && checkedCount % 5000 == 0)
                    progress?.Invoke(checkedCount);
                if (CountCandidate(ref checkedCount, candidateBudget) || engine.RemainingUnknownCount == 0) return checkedCount;
            }
            return checkedCount;
        }

        internal static IReadOnlyList<string> BuildWordAdditionFormats(IEnumerable<string> paths)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var regex = new Regex(@"([^/_.-]+)(?=[^/]*\.[^/]+$)", RegexOptions.Compiled);
            foreach (string path in paths)
            foreach (Match match in regex.Matches(path))
            foreach (string separator in new[] { "-", "_" })
            {
                counts.TryGetValue(path[..match.Index] + "{0}" + separator + path[match.Index..], out int beforeSupport);
                counts[path[..match.Index] + "{0}" + separator + path[match.Index..]] = beforeSupport + 1;
                counts.TryGetValue(path[..(match.Index + match.Length)] + separator + "{0}" + path[(match.Index + match.Length)..], out int afterSupport);
                counts[path[..(match.Index + match.Length)] + separator + "{0}" + path[(match.Index + match.Length)..]] = afterSupport + 1;
            }
            return counts.OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key)
                .ToList();
        }

        internal static int RunWordAdditionAttack(HashGuessEngine engine, IEnumerable<string> paths, IEnumerable<string> words, CancellationToken cancellationToken, int candidateBudget = 500_000)
        {
            var formats = BuildWordAdditionFormats(paths);
            return RunWordAdditionFormats(engine, formats, words, cancellationToken, candidateBudget);
        }

        internal static int RunWordAdditionFormats(
            HashGuessEngine engine,
            IReadOnlyList<string> formats,
            IEnumerable<string> words,
            CancellationToken cancellationToken,
            int candidateBudget = 500_000)
        {
            var wordsList = words.ToList();
            if (formats.Count == 0 || wordsList.Count == 0) return 0;
            int checkedCount = 0;
            foreach (string format in formats)
            foreach (string word in wordsList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                engine.CheckExact(string.Format(format, word), HashGuessStrategy.WordlistVariant, "Word Insertion");
                if (CountCandidate(ref checkedCount, candidateBudget) || engine.RemainingUnknownCount == 0) return checkedCount;
            }
            return checkedCount;
        }

        protected virtual IEnumerable<string> ExpandCandidate(HashGuessCandidate candidate)
        {
            yield return candidate.Path;
        }

        protected static string NormalizePath(string value) => PathUtils.NormalizePath(value);

        protected static bool CountCandidate(ref int count, int candidateBudget)
        {
            count = IncrementSaturating(count);
            return candidateBudget != int.MaxValue && count >= candidateBudget;
        }

        private static int IncrementSaturating(int value) => value == int.MaxValue ? value : value + 1;

        private static IEnumerable<IReadOnlyList<string>> EnumerateWordCombinations(IReadOnlyList<string> words, int length)
        {
            var buffer = new string[length];
            return Enumerate(0);

            IEnumerable<IReadOnlyList<string>> Enumerate(int index)
            {
                if (index == buffer.Length)
                {
                    yield return buffer.ToArray();
                    yield break;
                }

                foreach (string word in words)
                {
                    buffer[index] = word;
                    foreach (IReadOnlyList<string> combination in Enumerate(index + 1))
                        yield return combination;
                }
            }
        }

        private static IEnumerable<string> FormatNumberVariants(string format, int value, int? digits, bool includeCommonPadding)
        {
            if (digits.HasValue)
            {
                yield return format.Replace("{number}", value.ToString($"D{digits.Value}", CultureInfo.InvariantCulture), StringComparison.Ordinal);
                yield break;
            }

            yield return format.Replace("{number}", value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            if (!includeCommonPadding) yield break;
            if (value < 10)
                yield return format.Replace("{number}", value.ToString("D2", CultureInfo.InvariantCulture), StringComparison.Ordinal);
            if (value < 100)
                yield return format.Replace("{number}", value.ToString("D3", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }
    }
}
