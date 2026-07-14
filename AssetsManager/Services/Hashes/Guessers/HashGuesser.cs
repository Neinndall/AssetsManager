using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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

        protected HashGuesser(HashFile hashFile, string wadPattern)
        {
            HashFile = hashFile ?? throw new ArgumentNullException(nameof(hashFile));
            Domain = hashFile.Domain;
            WadPattern = wadPattern;
        }

        protected HashFile HashFile { get; }
        internal HashGuessDomain Domain { get; }
        internal string WadPattern { get; }
        internal IReadOnlyList<string> KnownPaths => HashFile.LoadPaths();

        internal static HashSet<ulong> UnknownFromExport(string directory) =>
            HashFile.LoadUnknownFromExport(directory);

        internal IReadOnlyList<string> DirectoryList() =>
            HashGuessEngine.BuildDirectoryList(KnownPaths);

        internal string[] FindWads(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
                throw new DirectoryNotFoundException("The selected game directory does not exist.");
            return Directory.EnumerateFiles(rootDirectory, WadPattern, SearchOption.AllDirectories)
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

        internal static bool TryDecodeWadText(byte[] data, out string text)
        {
            text = string.Empty;
            if (data == null || data.Length == 0) return false;
            try
            {
                text = StrictUtf8.GetString(data);
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
        internal abstract void GrepWad(HashGuessEngine engine, byte[] data, string sourcePath, string sourceWadPath, ulong sourceChunkHash);

        internal void CheckChunk(
            HashGuessEngine engine,
            byte[] data,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash)
        {
            if (engine.Domain != Domain)
                throw new InvalidOperationException($"Cannot use the {Domain} guesser with a {engine.Domain} engine.");

            foreach (HashGuessCandidate candidate in ExtractCandidates(data, sourcePath))
            {
                foreach (string path in ExpandCandidate(candidate))
                    engine.Check(path, candidate.Strategy, sourceWadPath, sourceChunkHash);
            }
        }

        protected abstract IEnumerable<HashGuessCandidate> ExtractCandidates(byte[] data, string sourcePath);

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
            string numberPattern = effectiveDigits.HasValue ? $"[0-9]{{{effectiveDigits.Value}}}" : "[0-9]+";
            var formats = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in knownPaths)
            {
                if (!IncludeNumberPath(path)) continue;
                foreach (Match match in Regex.Matches(path, numberPattern + @"(?=[^/]*\.[^/]+$)"))
                {
                    formats.Add(path[..match.Index] + "{number}" + path[(match.Index + match.Length)..]);
                }
            }

            int generated = 0;
            var orderedFormats = formats.OrderBy(path => path, StringComparer.Ordinal).ToList();
            for (int value = 0; value < numberLimit; value++)
            {
                foreach (string format in orderedFormats)
                {
                    foreach (string candidate in FormatNumberVariants(format, value, effectiveDigits, includeCommonPadding))
                    {
                        yield return new HashGuessCandidate(candidate, HashGuessStrategy.NumberVariant);
                        if (CountCandidate(ref generated, candidateBudget)) yield break;
                    }
                }
            }
        }

        protected virtual bool IncludeNumberPath(string path) => true;

        internal IEnumerable<HashGuessCandidate> GenerateExtensionCandidates(int candidateBudget = int.MaxValue) =>
            GenerateExtensionCandidates(KnownPaths, candidateBudget);

        protected static IEnumerable<HashGuessCandidate> GenerateExtensionCandidates(IEnumerable<string> knownPaths, int candidateBudget)
        {
            var paths = knownPaths.ToList();
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
                .OrderBy(prefix => prefix, StringComparer.Ordinal);
            int generated = 0;
            foreach (string prefix in prefixes)
            foreach (string extension in extensions)
            {
                yield return new HashGuessCandidate(prefix + extension, HashGuessStrategy.ExtensionVariant);
                if (CountCandidate(ref generated, candidateBudget)) yield break;
            }
        }

        internal bool Check(HashGuessEngine engine, string path, HashGuessStrategy strategy, string source = "Generated") =>
            engine.Check(path, strategy, source);

        internal bool IsKnown(HashGuessEngine engine, string path, HashGuessStrategy strategy, string source = "Generated")
        {
            string normalized = NormalizePath(path);
            ulong hash = XxHash64Ext.Hash(normalized);
            return engine.Check(normalized, strategy, source) || HashFile.Load().ContainsKey(hash);
        }

        internal int CheckMany(HashGuessEngine engine, IEnumerable<string> paths, HashGuessStrategy strategy, string source = "Generated") =>
            engine.CheckMany(paths, strategy, source);

        internal int CheckTextList(HashGuessEngine engine, string text, HashGuessStrategy strategy, string source = "Text list") =>
            CheckMany(engine, text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries), strategy, source);

        internal int CheckXdbgHashes(HashGuessEngine engine, string path, HashGuessStrategy strategy = HashGuessStrategy.EmbeddedPathGrep)
        {
            if (!File.Exists(path)) return 0;
            var candidates = File.ReadLines(path)
                .Where(line => line.StartsWith("hash: ", StringComparison.Ordinal))
                .Select(line => line.Split('"'))
                .Where(parts => parts.Length > 1)
                .Select(parts => parts[1]);
            return CheckMany(engine, candidates, strategy, "XDBG hashes");
        }

        internal int CheckBasenames(HashGuessEngine engine, IEnumerable<string> names, HashGuessStrategy strategy, string source, int candidateBudget = int.MaxValue)
        {
            int checkedCount = 0;
            IReadOnlyList<string> directories = DirectoryList();
            foreach (string name in names.Select(NormalizePath).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
            foreach (string directory in directories)
            {
                engine.Check(directory + "/" + name, strategy, source);
                if (CountCandidate(ref checkedCount, candidateBudget) || engine.RemainingUnknownCount == 0) return checkedCount;
            }
            return checkedCount;
        }

        internal int SubstituteBasenames(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            int? maxNames = null,
            int? maxDirectories = null,
            int candidateBudget = int.MaxValue)
        {
            IEnumerable<string> names = KnownPaths.Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal);
            IEnumerable<string> directories = DirectoryList();
            if (maxNames.HasValue) names = names.Take(Math.Max(0, maxNames.Value));
            if (maxDirectories.HasValue) directories = directories.Take(Math.Max(0, maxDirectories.Value));
            var directoryList = directories.ToList();

            int checkedCount = 0;
            foreach (string name in names)
            foreach (string directory in directoryList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                engine.Check(directory + "/" + name, HashGuessStrategy.PluginVariant, "Basename substitution");
                if (CountCandidate(ref checkedCount, candidateBudget) || engine.RemainingUnknownCount == 0) return checkedCount;
            }
            return checkedCount;
        }

        internal static int RunFocusedWordlistSubstitution(HashGuessEngine engine, IEnumerable<string> paths, IEnumerable<string> words, CancellationToken cancellationToken, int candidateBudget = 500_000)
            => RunBasenameWordSubstitution(engine, paths, words, 1, 1, cancellationToken, candidateBudget, "Focused Wordlist");

        internal static int RunFocusedWordlistDoubleSubstitution(HashGuessEngine engine, IEnumerable<string> paths, IEnumerable<string> words, CancellationToken cancellationToken, int candidateBudget = 500_000)
            => RunBasenameWordSubstitution(engine, paths, words.Take(150), 2, 2, cancellationToken, candidateBudget, "Double Wordlist");

        internal static int RunBasenameWordSubstitution(
            HashGuessEngine engine,
            IEnumerable<string> paths,
            IEnumerable<string> words,
            int oldWordCount,
            int newWordCount,
            CancellationToken cancellationToken,
            int candidateBudget = 500_000,
            string source = "Wordlist substitution")
        {
            if (oldWordCount < 1) throw new ArgumentOutOfRangeException(nameof(oldWordCount));
            if (newWordCount < 1) throw new ArgumentOutOfRangeException(nameof(newWordCount));
            var pathsList = paths.ToList();
            var wordsList = words.Where(word => !string.IsNullOrEmpty(word)).Distinct(StringComparer.Ordinal).ToList();
            if (pathsList.Count == 0 || wordsList.Count == 0) return 0;
            var formats = new HashSet<(string Prefix, string Suffix)>();
            var regex = new Regex($@"([^/_.-]+)(?=((?:[-_][^/_.-]+){{{oldWordCount - 1}}})[^/]*\.[^/]+$)", RegexOptions.Compiled);
            foreach (string path in pathsList)
            {
                if (path.Contains('%')) continue;
                foreach (Match match in regex.Matches(path))
                {
                    int matchedLength = match.Groups[1].Length + match.Groups[2].Length;
                    formats.Add((path[..match.Index], path[(match.Index + matchedLength)..]));
                }
            }

            int checkedCount = 0;
            foreach ((string prefix, string suffix) in formats.OrderBy(value => value.Prefix, StringComparer.Ordinal).ThenBy(value => value.Suffix, StringComparer.Ordinal))
            foreach (string separator in newWordCount == 1 ? new[] { string.Empty } : new[] { "-", "_" })
            foreach (IReadOnlyList<string> combination in EnumerateWordCombinations(wordsList, newWordCount))
            {
                cancellationToken.ThrowIfCancellationRequested();
                engine.Check(prefix + string.Join(separator, combination) + suffix, HashGuessStrategy.WordlistVariant, source);
                if (CountCandidate(ref checkedCount, candidateBudget) || engine.RemainingUnknownCount == 0) return checkedCount;
            }
            return checkedCount;
        }

        internal static int RunWordAdditionAttack(HashGuessEngine engine, IEnumerable<string> paths, IEnumerable<string> words, CancellationToken cancellationToken, int candidateBudget = 500_000)
        {
            var pathsList = paths.ToList();
            var wordsList = words.ToList();
            if (pathsList.Count == 0 || wordsList.Count == 0) return 0;
            var formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var regex = new Regex(@"([^/_.-]+)(?=[^/]*\.[^/]+$)", RegexOptions.Compiled);
            foreach (string path in pathsList)
            foreach (Match match in regex.Matches(path))
            foreach (string separator in new[] { "-", "_" })
            {
                formats.Add(path[..match.Index] + "{0}" + separator + path[match.Index..]);
                formats.Add(path[..(match.Index + match.Length)] + separator + "{0}" + path[(match.Index + match.Length)..]);
            }
            int checkedCount = 0;
            foreach (string format in formats)
            foreach (string word in wordsList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                engine.Check(string.Format(format, word), HashGuessStrategy.WordlistVariant, "Word Insertion");
                if (CountCandidate(ref checkedCount, candidateBudget) || engine.RemainingUnknownCount == 0) return checkedCount;
            }
            return checkedCount;
        }

        internal static int RunPrefixAttack(HashGuessEngine engine, IEnumerable<string> paths, IEnumerable<string> prefixes, CancellationToken cancellationToken, int candidateBudget = 2_000_000)
        {
            int checkedCount = 0;
            var prefixList = prefixes.ToList();
            foreach (string path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int separator = path.LastIndexOf('/');
                string directory = separator >= 0 ? path[..(separator + 1)] : string.Empty;
                string file = separator >= 0 ? path[(separator + 1)..] : path;
                foreach (string prefix in prefixList)
                {
                    engine.Check(directory + prefix + file, HashGuessStrategy.PrefixVariant, "Prefix variant");
                    if (CountCandidate(ref checkedCount, candidateBudget) || engine.RemainingUnknownCount == 0) return checkedCount;
                }
            }
            return checkedCount;
        }

        protected virtual IEnumerable<string> ExpandCandidate(HashGuessCandidate candidate)
        {
            yield return candidate.Path;
        }

        protected static string NormalizePath(string value) => HashGuessEngine.NormalizePath(value);

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
