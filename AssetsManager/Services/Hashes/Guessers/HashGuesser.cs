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

        protected virtual void CheckCandidate(
            HashGuessEngine engine,
            HashGuessCandidate candidate,
            string sourceWadPath,
            ulong sourceChunkHash)
        {
            CheckIter(engine, ExpandCandidate(candidate), candidate.Strategy, sourceWadPath, sourceChunkHash);
        }

        internal virtual IEnumerable<HashGuessCandidate> GenerateNumberCandidates(
            int numberLimit,
            int candidateBudget = int.MaxValue,
            int? digits = null,
            bool inferDigits = false,
            bool includeCommonPadding = true) =>
            GenerateNumberCandidates(KnownPaths, numberLimit, candidateBudget, digits, inferDigits, includeCommonPadding);

        protected int SubstituteNumbersCore(
            HashGuessEngine engine,
            IEnumerable<string> paths,
            int numberLimit,
            int? digits,
            bool inferDigits,
            CancellationToken cancellationToken,
            string source,
            Action<int> progress = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(paths);
            int? effectiveDigits = ResolveNumberDigits(numberLimit, digits, inferDigits);
            int checkedCount = 0;

            IReadOnlyList<string> formats = BuildNumberFormats(paths, effectiveDigits);
            foreach (string format in ProgressIterator(formats, value => value, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkedCount += CheckIter(
                    engine,
                    GenerateNumberCandidatesForFormat(format, numberLimit, effectiveDigits, includeCommonPadding: false),
                    source,
                    cancellationToken);
                progress?.Invoke(checkedCount);
                if (engine.RemainingUnknownCount == 0) break;
            }

            return checkedCount;
        }

        protected IEnumerable<HashGuessCandidate> GenerateNumberCandidates(
            IEnumerable<string> knownPaths,
            int numberLimit,
            int candidateBudget,
            int? digits = null,
            bool inferDigits = false,
            bool includeCommonPadding = true)
        {
            if (candidateBudget < 0) throw new ArgumentOutOfRangeException(nameof(candidateBudget));
            int? effectiveDigits = ResolveNumberDigits(numberLimit, digits, inferDigits);
            int generated = 0;
            foreach (string format in BuildNumberFormats(knownPaths, effectiveDigits))
            foreach (HashGuessCandidate candidate in GenerateNumberCandidatesForFormat(
                         format,
                         numberLimit,
                         effectiveDigits,
                         includeCommonPadding))
            {
                yield return candidate;
                if (CountCandidate(ref generated, candidateBudget)) yield break;
            }
        }

        private IReadOnlyList<string> BuildNumberFormats(IEnumerable<string> knownPaths, int? effectiveDigits)
        {
            string digitExpression = effectiveDigits.HasValue ? $"[0-9]{{{effectiveDigits.Value}}}" : "[0-9]+";
            string numberPattern = AnchorNumberMatchesToFileName
                ? digitExpression + @"(?=[^/]*\.[^/]+$)"
                : digitExpression;
            var formats = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in knownPaths)
            {
                if (!IncludeNumberPath(path)) continue;
                foreach (Match match in Regex.Matches(path, numberPattern))
                    formats.Add(path[..match.Index] + "{number}" + path[(match.Index + match.Length)..]);
            }

            return formats.OrderBy(path => path, StringComparer.Ordinal).ToList();
        }

        private static int? ResolveNumberDigits(int numberLimit, int? digits, bool inferDigits)
        {
            if (numberLimit < 0) throw new ArgumentOutOfRangeException(nameof(numberLimit));
            if (digits is < 1) throw new ArgumentOutOfRangeException(nameof(digits));
            return inferDigits
                ? Math.Max(1, Math.Max(0, numberLimit - 1).ToString(CultureInfo.InvariantCulture).Length)
                : digits;
        }

        private static IEnumerable<HashGuessCandidate> GenerateNumberCandidatesForFormat(
            string format,
            int numberLimit,
            int? effectiveDigits,
            bool includeCommonPadding)
        {
            for (int value = 0; value < numberLimit; value++)
            foreach (string candidate in FormatNumberVariants(format, value, effectiveDigits, includeCommonPadding))
                yield return new HashGuessCandidate(candidate, HashGuessStrategy.NumberVariant);
        }

        protected virtual bool IncludeNumberPath(string path) => true;

        protected virtual bool AnchorNumberMatchesToFileName => true;

        internal int SubstituteExtensions(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            int candidateBudget = int.MaxValue,
            string source = "Extension substitution",
            Action<int> progress = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            if (candidateBudget < 0) throw new ArgumentOutOfRangeException(nameof(candidateBudget));
            if (candidateBudget == 0) return 0;

            var prefixes = new HashSet<string>(StringComparer.Ordinal);
            var extensions = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in KnownPaths)
            {
                string extension = Path.GetExtension(path);
                string prefix = extension.Length == 0 ? path : path[..^extension.Length];
                prefixes.Add(prefix);
                if (!extension.EndsWith("00", StringComparison.Ordinal))
                    extensions.Add(extension);
            }

            int checkedCount = 0;
            IReadOnlyList<string> orderedExtensions = extensions.OrderBy(value => value, StringComparer.Ordinal).ToList();
            IEnumerable<string> orderedPrefixes = prefixes.OrderBy(value => value, StringComparer.Ordinal);
            foreach (string prefix in ProgressIterator(orderedPrefixes, value => value, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int remaining = candidateBudget == int.MaxValue ? int.MaxValue : candidateBudget - checkedCount;
                if (remaining <= 0) break;

                IEnumerable<HashGuessCandidate> candidates = orderedExtensions
                    .Select(extension => new HashGuessCandidate(prefix + extension, HashGuessStrategy.ExtensionVariant));
                if (remaining != int.MaxValue) candidates = candidates.Take(remaining);

                checkedCount += CheckIter(engine, candidates, source, cancellationToken);
                progress?.Invoke(checkedCount);
                if (engine.RemainingUnknownCount == 0) break;
            }

            return checkedCount;
        }

        internal bool Check(
            HashGuessEngine engine,
            string path,
            HashGuessStrategy strategy,
            string source = "Generated",
            ulong sourceChunkHash = 0) =>
            engine.Check(path, strategy, source, sourceChunkHash);

        internal bool IsKnown(HashGuessEngine engine, string path, HashGuessStrategy strategy, string source = "Generated")
        {
            ulong hash = XxHash64Ext.Hash(PathUtils.NormalizePath(path));
            return engine.Check(path, strategy, source) || HashFile.Load().ContainsKey(hash);
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
                engine.Check(path, strategy, source, sourceChunkHash);
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
            int progressInterval = 0,
            ulong sourceChunkHash = 0)
        {
            ArgumentNullException.ThrowIfNull(candidates);
            int checkedCount = 0;
            foreach (HashGuessCandidate candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckCandidate(engine, candidate, source, sourceChunkHash);
                checkedCount++;
                if (progressInterval > 0 && checkedCount % progressInterval == 0)
                    progress?.Invoke(checkedCount);
                if (engine.RemainingUnknownCount == 0) break;
            }
            return checkedCount;
        }

        internal static IEnumerable<T> ProgressIterator<T>(
            IEnumerable<T> sequence,
            Func<T, string> formatter = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(sequence);
            if (Console.IsErrorRedirected) return sequence;
            return ProgressIterate(sequence.ToList(), formatter, cancellationToken);
        }

        internal static IEnumerable<T> ProgressIterate<T>(
            IReadOnlyList<T> sequence,
            Func<T, string> formatter = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(sequence);
            formatter ??= value => value?.ToString() ?? string.Empty;
            for (int index = 0; index < sequence.Count; index++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    double percentage = sequence.Count == 0 ? 100 : 100.0 * index / sequence.Count;
                    Console.Error.WriteLine($"  {percentage,5:0.0}%  {formatter(sequence[index])}");
                    cancellationToken.ThrowIfCancellationRequested();
                }

                yield return sequence[index];
            }
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
            IReadOnlyList<string> names = Corpus.GetOrCreate("basenames", paths => paths.Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList());
            IReadOnlyList<string> directories = DirectoryList();
            if (maxNames.HasValue) names = names.Take(Math.Max(0, maxNames.Value)).ToList();
            if (maxDirectories.HasValue) directories = directories.Take(Math.Max(0, maxDirectories.Value)).ToList();

            int checkedCount = 0;
            int lastReportedCount = -1;
            var progressClock = Stopwatch.StartNew();
            foreach (string name in ProgressIterator(names, value => value, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int remaining = candidateBudget == int.MaxValue ? int.MaxValue : candidateBudget - checkedCount;
                if (remaining <= 0 || engine.RemainingUnknownCount == 0) break;

                IEnumerable<HashGuessCandidate> candidates = directories.Select(directory =>
                    new HashGuessCandidate(
                        string.IsNullOrEmpty(directory) ? name : $"{directory}/{name}",
                        HashGuessStrategy.PluginVariant));
                if (remaining != int.MaxValue) candidates = candidates.Take(remaining);

                checkedCount += CheckIter(
                    engine,
                    candidates,
                    "Basename substitution",
                    cancellationToken);
                if ((checkedCount & 0x3fff) == 0 && progressClock.ElapsedMilliseconds >= 100)
                {
                    progress?.Invoke(checkedCount);
                    lastReportedCount = checkedCount;
                    progressClock.Restart();
                }
                if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0)
                {
                    if (lastReportedCount != checkedCount) progress?.Invoke(checkedCount);
                    return checkedCount;
                }
            }
            if (lastReportedCount != checkedCount) progress?.Invoke(checkedCount);
            return checkedCount;
        }

        internal int RunFocusedWordlistSubstitution(HashGuessEngine engine, IEnumerable<string> paths, IEnumerable<string> words, CancellationToken cancellationToken, int candidateBudget = 500_000)
            => SubstituteBasenameWordsCore(engine, paths, words, 1, 1, cancellationToken, candidateBudget, "Focused Wordlist");

        internal int RunFocusedWordlistDoubleSubstitution(HashGuessEngine engine, IEnumerable<string> paths, IEnumerable<string> words, CancellationToken cancellationToken, int candidateBudget = 500_000)
            => SubstituteBasenameWordsCore(engine, paths, words.Take(150), 2, 2, cancellationToken, candidateBudget, "Double Wordlist");

        internal static IReadOnlyList<(string Prefix, string Suffix)> BuildBasenameWordFormats(IEnumerable<string> paths, int oldWordCount, int newWordCount)
        {
            if (oldWordCount < 1) throw new ArgumentOutOfRangeException(nameof(oldWordCount));
            if (newWordCount < 1) throw new ArgumentOutOfRangeException(nameof(newWordCount));
            var counts = new Dictionary<(string Prefix, string Suffix), int>();
            var regex = new Regex($@"([^/_.-]+)(?=((?:[-_][^/_.-]+){{{oldWordCount - 1}}})[^/]*\.[^/]+$)", RegexOptions.Compiled);
            foreach (string path in paths)
            {
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

        protected internal int SubstituteBasenameWordsCore(
            HashGuessEngine engine,
            IEnumerable<string> paths,
            IEnumerable<string> words,
            int oldWordCount,
            int newWordCount,
            CancellationToken cancellationToken,
            int candidateBudget = int.MaxValue,
            string source = "Wordlist substitution",
            Action<int> progress = null,
            HashGuessStrategy strategy = HashGuessStrategy.WordlistVariant)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(paths);
            ArgumentNullException.ThrowIfNull(words);
            if (candidateBudget < 0) throw new ArgumentOutOfRangeException(nameof(candidateBudget));
            if (candidateBudget == 0) return 0;

            IReadOnlyList<string> formats = BuildBasenameWordFormats(paths, oldWordCount, newWordCount)
                .SelectMany(format => (newWordCount == 1 ? new[] { string.Empty } : new[] { "-", "_" })
                    .Select(separator => BuildBasenameWordFormat(format.Prefix, format.Suffix, separator, newWordCount)))
                .OrderBy(format => format, StringComparer.Ordinal)
                .ToList();
            IReadOnlyList<string> wordsList = words.ToList();
            if (formats.Count == 0 || wordsList.Count == 0) return 0;

            int checkedCount = 0;
            foreach (string format in ProgressIterator(formats, format => format, cancellationToken))
            {
                int remaining = candidateBudget == int.MaxValue ? int.MaxValue : candidateBudget - checkedCount;
                if (remaining <= 0) return checkedCount;

                IEnumerable<string> candidates = EnumerateBasenameWordCandidates(format, wordsList, newWordCount, cancellationToken);
                if (remaining != int.MaxValue) candidates = candidates.Take(remaining);

                checkedCount += CheckIter(engine, candidates, strategy, source);
                progress?.Invoke(checkedCount);
                if (engine.RemainingUnknownCount == 0) return checkedCount;
            }
            return checkedCount;
        }

        internal static IReadOnlyList<string> BuildWordAdditionFormats(IEnumerable<string> paths)
        {
            ArgumentNullException.ThrowIfNull(paths);
            var formats = new HashSet<string>(StringComparer.Ordinal);
            var regex = new Regex(@"([^/_.-]+)(?=[^/]*\.[^/]+$)", RegexOptions.Compiled);
            foreach (string path in paths)
            foreach (Match match in regex.Matches(path))
            foreach (string separator in new[] { "-", "_" })
            {
                formats.Add(path[..match.Index] + "{0}" + separator + path[match.Index..]);
                formats.Add(path[..(match.Index + match.Length)] + separator + "{0}" + path[(match.Index + match.Length)..]);
            }
            return formats.OrderBy(format => format, StringComparer.Ordinal).ToList();
        }

        protected internal int AddBasenameWordCore(
            HashGuessEngine engine,
            IEnumerable<string> paths,
            IEnumerable<string> words,
            CancellationToken cancellationToken,
            int candidateBudget = int.MaxValue,
            string source = "Word insertion",
            Action<int> progress = null,
            HashGuessStrategy strategy = HashGuessStrategy.WordlistVariant)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(paths);
            ArgumentNullException.ThrowIfNull(words);
            if (candidateBudget < 0) throw new ArgumentOutOfRangeException(nameof(candidateBudget));
            if (candidateBudget == 0) return 0;

            var formats = BuildWordAdditionFormats(paths);
            var wordsList = words.ToList();
            if (formats.Count == 0 || wordsList.Count == 0) return 0;

            int checkedCount = 0;
            foreach (string format in ProgressIterator(formats, value => value, cancellationToken))
            {
                int remaining = candidateBudget == int.MaxValue ? int.MaxValue : candidateBudget - checkedCount;
                if (remaining <= 0) return checkedCount;

                IEnumerable<HashGuessCandidate> candidates = wordsList.Select(word =>
                    new HashGuessCandidate(
                        format.Replace("{0}", word, StringComparison.Ordinal),
                        strategy));
                if (remaining != int.MaxValue) candidates = candidates.Take(remaining);

                checkedCount += CheckIter(engine, candidates, source, cancellationToken);
                progress?.Invoke(checkedCount);
                if (engine.RemainingUnknownCount == 0) return checkedCount;
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

        private static IEnumerable<string> EnumerateBasenameWordCandidates(
            string format,
            IReadOnlyList<string> words,
            int newWordCount,
            CancellationToken cancellationToken)
        {
            foreach (IReadOnlyList<string> combination in EnumerateWordCombinations(words, newWordCount))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return FormatBasenameWordCandidate(format, combination);
            }
        }

        private static string BuildBasenameWordFormat(string prefix, string suffix, string separator, int newWordCount)
        {
            var builder = new StringBuilder(prefix);
            for (int index = 0; index < newWordCount; index++)
            {
                if (index > 0) builder.Append(separator);
                builder.Append("%s");
            }

            builder.Append(suffix);
            return builder.ToString();
        }

        private static string FormatBasenameWordCandidate(string format, IReadOnlyList<string> words)
        {
            var builder = new StringBuilder();
            int offset = 0;
            foreach (string word in words)
            {
                int marker = format.IndexOf("%s", offset, StringComparison.Ordinal);
                if (marker < 0) throw new FormatException("Basename word format has fewer placeholders than words.");
                builder.Append(format, offset, marker - offset);
                builder.Append(word);
                offset = marker + 2;
            }

            builder.Append(format, offset, format.Length - offset);
            return builder.ToString();
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
