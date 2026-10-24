using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Views.Models.Hashes;

namespace AssetsManager.Services.Hashes.Guessers.Game;

internal sealed partial class GameHashGuesser
{
    internal long SubstituteAnimationBuildListWords(
        HashGuessEngine engine,
        CancellationToken cancellationToken,
        long candidateBudget = long.MaxValue,
        Action<long> progress = null,
        string rootDirectory = null, GameWadPathCache wadPathCache = null)
    {
        if (engine.RemainingUnknownCount == 0 || candidateBudget <= 0) return 0;

        long checkedCandidates = 0;
        if (!string.IsNullOrWhiteSpace(rootDirectory))
        {
            checkedCandidates = GuessLocalNamePatterns(engine, rootDirectory, animations: true,
                cancellationToken, candidateBudget, progress, wadPathCache);
            if (engine.RemainingUnknownCount == 0 || checkedCandidates >= candidateBudget) return checkedCandidates;
        }

        IReadOnlyList<string> animPaths = Corpus.GetOrCreate(
            "custom-character-anm-paths",
            paths => paths
                .Where(path => (path.StartsWith("assets/characters/", StringComparison.OrdinalIgnoreCase)
                             || path.StartsWith("data/characters/", StringComparison.OrdinalIgnoreCase))
                             && path.Contains("/animations/", StringComparison.OrdinalIgnoreCase)
                             && path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase))
                .ToList());

        IReadOnlyList<string> words = GetExpandedGlobalAnimationActions(cancellationToken);
        if (animPaths.Count == 0 || words.Count == 0) return checkedCandidates;

        IReadOnlyDictionary<string, IReadOnlyList<string>> wordsByFamily = Corpus.GetOrCreate(
            "expanded-animation-actions-by-family-v2",
            _ => GetGlobalAnimationActions(cancellationToken)
                .Concat(words)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .GroupBy(ActionFamily, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Key.Length > 0)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<string>)group.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    StringComparer.OrdinalIgnoreCase));
        IReadOnlyList<string> crossFamilyWords = Corpus.GetOrCreate(
            "prioritized-cross-family-animation-actions",
            _ => BaseAnimationActions
                .Concat(GetGlobalAnimationActions(cancellationToken).Take(2_048))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList());

        int processedFormats = 0;
        var formats = new HashSet<(string Prefix, string Suffix, string Family, bool WholeBasename)>();
        var tokenRegex = new Regex(@"[^/_.-]+", RegexOptions.Compiled);
        foreach (string path in animPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int basenameStart = path.LastIndexOf('/') + 1;
            int extensionStart = path.LastIndexOf('.');
            if (extensionStart <= basenameStart) continue;

            foreach (Match match in tokenRegex.Matches(path, basenameStart))
            {
                if (match.Index >= extensionStart) break;
                string family = ActionFamily(match.Value);
                if (family.Length == 0 || !wordsByFamily.ContainsKey(family)) continue;
                bool wholeBasename = match.Index == basenameStart && match.Index + match.Length == extensionStart;
                formats.Add((path[..match.Index], path[(match.Index + match.Length)..], family, wholeBasename));
            }
        }

        var orderedFormats = formats
            .OrderBy(value => value.Family, StringComparer.Ordinal)
            .ThenBy(value => value.Suffix, StringComparer.Ordinal)
            .ThenBy(value => value.Prefix, StringComparer.Ordinal)
            .ToList();
        var wordPools = new Dictionary<(string Family, bool WholeBasename), IReadOnlyList<string>>();
        const int wordBatchSize = 64;
        for (int wordOffset = 0; ; wordOffset += wordBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long roundCandidateCount = 0;
            foreach (var format in orderedFormats)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var poolKey = (format.Family, format.WholeBasename);
                if (!wordPools.TryGetValue(poolKey, out IReadOnlyList<string> pool))
                {
                    IEnumerable<string> poolWords = wordsByFamily[format.Family];
                    if (format.WholeBasename) poolWords = poolWords.Concat(crossFamilyWords);
                    pool = poolWords.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    wordPools[poolKey] = pool;
                }

                roundCandidateCount += Math.Max(0, Math.Min(wordBatchSize, pool.Count - wordOffset));
            }

            if (roundCandidateCount == 0
                || roundCandidateCount > candidateBudget - checkedCandidates
                || engine.RemainingUnknownCount == 0) break;

            foreach (var format in orderedFormats)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<string> pool = wordPools[(format.Family, format.WholeBasename)];
                int batchCount = Math.Min(wordBatchSize, pool.Count - wordOffset);
                if (batchCount <= 0) continue;
                for (int i = 0; i < batchCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    engine.CheckNormalizedParts(format.Prefix, pool[wordOffset + i], format.Suffix,
                        HashGuessStrategy.WordlistVariant, "GAME Custom: animation actions build-list");
                    checkedCandidates++;
                    if (engine.RemainingUnknownCount == 0) break;
                }
                if ((++processedFormats & 0xff) == 0) progress?.Invoke(checkedCandidates);
            }
        }

        progress?.Invoke(checkedCandidates);
        return checkedCandidates;

        static string ActionFamily(string action)
        {
            int separator = action.IndexOfAny('_', '-', '.');
            ReadOnlySpan<char> head = separator >= 0 ? action.AsSpan(0, separator) : action.AsSpan();
            int length = head.Length;
            while (length > 0 && char.IsDigit(head[length - 1])) length--;
            return length == 0 ? string.Empty : head[..length].ToString().ToLowerInvariant();
        }
    }
}
