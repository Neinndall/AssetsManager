using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;

namespace AssetsManager.Services.Hashes.Guessers.Game
{
    internal sealed partial class GameHashGuesser
    {
        private const int EsportsBannerSingleCandidateBudget = 2_000_000;
        private const int EsportsBannerCompoundCandidateBudget = 10_000_000;
        private const int EsportsBannerDoubleCandidateBudget = 2_000_000;
        private const int EsportsBannerInsertionCandidateBudget = 750_000;
        private const int EsportsBannerDoubleWordLimit = 96;
        private const string EsportsBannerPathPrefix = "assets/esports/sponsoredbanners/";
        private static readonly string[] EsportsBannerFallbackWords =
            {
                "portuguesa", "msi", "2025", "2026", "25", "26", "hepsiburada", "kitkat", "tsl",
                "fst", "first", "stand", "lol", "esports", "ewc", "lta", "lcp", "v1", "v2", "v3"
            };
        private static readonly string[] EsportsBannerEventPrefixes =
        {
            "fst", "msi", "worlds", "ewc", "lck", "lec", "lcs", "lta", "lcp", "cblol", "pcs", "vcs", "ljl", "lla"
        };
        private static readonly string[] EsportsBannerVersionSuffixes =
        {
            "2025", "2026", "25", "26", "v1", "v2", "v3", "01", "02"
        };

        internal static IReadOnlyList<string> BuildEsportsBannerWordlist(IEnumerable<string> paths)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string value in paths)
            {
                string path = PathUtils.NormalizePath(value);
                if (!path.StartsWith(EsportsBannerPathPrefix, StringComparison.Ordinal)) continue;
                string relative = path[EsportsBannerPathPrefix.Length..];
                int extension = relative.LastIndexOf('.');
                if (extension > relative.LastIndexOf('/')) relative = relative[..extension];
                foreach (string word in relative.Split(new[] { '/', '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (word.Length is < 1 or > 32 || !word.All(char.IsLetterOrDigit)) continue;
                    counts[word] = counts.GetValueOrDefault(word) + 1;
                }
            }
            foreach (string word in EsportsBannerFallbackWords) counts.TryAdd(word, 0);
            return counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key).ToArray();
        }

        internal int GuessEsportsBanners(
            HashGuessEngine engine,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (engine.RemainingUnknownCount == 0) return 0;
            IReadOnlyList<string> paths = Corpus.GetOrCreate(
                "esports-banner-paths",
                values => values
                    .Where(path => path.StartsWith(EsportsBannerPathPrefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList());
            IReadOnlyList<string> wordlist = Corpus.GetOrCreate(
                "esports-banner-wordlist",
                _ => BuildEsportsBannerWordlist(paths));
            IReadOnlyList<string> compoundWords = Corpus.GetOrCreate(
                "esports-banner-compound-wordlist",
                _ =>
                {
                    var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (string path in paths)
                    {
                        string basename = GetBasename(path);
                        int extension = basename.LastIndexOf('.');
                        string stem = extension > 0 ? basename[..extension] : basename;
                        string[] tokens = stem
                            .Split(new[] { '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries)
                            .Where(IsBannerToken)
                            .ToArray();

                        for (int length = 2; length <= 4; length++)
                        for (int index = 0; index + length <= tokens.Length; index++)
                        {
                            var words = tokens.Skip(index).Take(length);
                            AddWord(string.Join('_', words));
                            AddWord(string.Join('-', words));
                            AddWord(string.Concat(words));
                        }
                    }

                    return counts
                        .OrderByDescending(pair => pair.Value)
                        .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(pair => pair.Key.ToLowerInvariant())
                        .ToList();

                    void AddWord(string value)
                    {
                        if (!IsCompound(value)) return;
                        counts.TryGetValue(value, out int current);
                        counts[value] = current + 1;
                    }

                    bool IsCompound(string value) =>
                        !string.IsNullOrWhiteSpace(value) &&
                        value.Length <= 48 &&
                        value.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries).All(IsBannerToken);

                    bool IsBannerToken(string value) =>
                        value.Length >= 1 &&
                        value.Length <= 32 &&
                        value.All(char.IsLetterOrDigit);
                });
            IReadOnlyList<string> doubleWords = Corpus.GetOrCreate(
                "esports-banner-double-words",
                _ => compoundWords
                    .SelectMany(word => word.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
                    .Concat(wordlist)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(EsportsBannerDoubleWordLimit)
                    .ToList());

            return RunBannerBasenameAttack(
                engine,
                paths,
                wordlist,
                compoundWords,
                doubleWords,
                progress,
                cancellationToken);
        }

        private int RunBannerBasenameAttack(
            HashGuessEngine engine,
            IReadOnlyList<string> paths,
            IReadOnlyList<string> wordlist,
            IReadOnlyList<string> compoundWords,
            IReadOnlyList<string> doubleWords,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken)
        {
            if (paths.Count == 0 || engine.RemainingUnknownCount == 0) return 0;

            int checkedCandidates = 0;
            const string buildListStage = "GAME Banner: whole filenames build-list";
            progress?.Report(engine.CreateProgress(buildListStage, checkedCandidates));
            string[] names = paths.Select(GetBasename).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            foreach (string directory in paths.Select(path => path[..(path.LastIndexOf('/') + 1)])
                         .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (checkedCandidates >= EsportsBannerSingleCandidateBudget || engine.RemainingUnknownCount == 0) break;
                foreach (string name in names)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (checkedCandidates >= EsportsBannerSingleCandidateBudget || engine.RemainingUnknownCount == 0) break;
                    engine.CheckPrefixSuffix(directory, name, HashGuessStrategy.BannerVariant, buildListStage);
                    checkedCandidates++;
                }
            }
            var middleStems = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in names)
            {
                int dot = name.LastIndexOf('.');
                string stem = dot > 0 ? name[..dot] : name;
                string[] parts = stem.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    middleStems.Add(string.Join('_', parts[1..]));
                    if (parts.Length >= 3)
                        middleStems.Add(string.Join('_', parts[1..^1]));
                }
            }
            foreach (string directory in paths.Select(path => path[..(path.LastIndexOf('/') + 1)])
                         .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (checkedCandidates >= EsportsBannerSingleCandidateBudget || engine.RemainingUnknownCount == 0) break;
                foreach (string eventPrefix in EsportsBannerEventPrefixes)
                foreach (string middle in middleStems)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (checkedCandidates >= EsportsBannerSingleCandidateBudget || engine.RemainingUnknownCount == 0) break;
                    engine.CheckNormalizedParts(directory, $"{eventPrefix}_{middle}", ".tex", HashGuessStrategy.BannerVariant, buildListStage);
                    checkedCandidates++;
                    foreach (string version in EsportsBannerVersionSuffixes)
                    {
                        if (checkedCandidates >= EsportsBannerSingleCandidateBudget || engine.RemainingUnknownCount == 0) break;
                        engine.CheckNormalizedParts(directory, $"{eventPrefix}_{middle}_{version}", ".tex", HashGuessStrategy.BannerVariant, buildListStage);
                        checkedCandidates++;
                    }
                }
            }
            int buildListCount = checkedCandidates;
            int compoundBudget = EsportsBannerCompoundCandidateBudget;
            var passes = new[]
            {
                (Words: wordlist, OldCount: 1, NewCount: 1, Budget: EsportsBannerSingleCandidateBudget - buildListCount, Source: "GAME Banner: vocabulary"),
                (Words: compoundWords, OldCount: 2, NewCount: 1, Budget: EsportsBannerCompoundCandidateBudget, Source: "GAME Banner: compound names"),
                (Words: compoundWords, OldCount: 3, NewCount: 1, Budget: EsportsBannerCompoundCandidateBudget, Source: "GAME Banner: three-word names"),
                (Words: compoundWords, OldCount: 4, NewCount: 1, Budget: EsportsBannerCompoundCandidateBudget, Source: "GAME Banner: four-word names"),
                (Words: compoundWords, OldCount: 1, NewCount: 1, Budget: EsportsBannerCompoundCandidateBudget, Source: "GAME Banner: compound variants"),
                (Words: doubleWords, OldCount: 2, NewCount: 2, Budget: EsportsBannerDoubleCandidateBudget, Source: "GAME Banner: double-word variants")
            };
            foreach (var pass in passes)
            {
                if (engine.RemainingUnknownCount == 0) return checkedCandidates;
                int progressOffset = checkedCandidates;
                progress?.Report(engine.CreateProgress(pass.Source, progressOffset));
                bool compoundPass = pass.OldCount >= 2 && pass.NewCount == 1;
                int count = _SubstituteBasenameWords(
                    engine,
                    paths,
                    pass.Words,
                    pass.OldCount,
                    pass.NewCount,
                    cancellationToken,
                    compoundPass ? compoundBudget : pass.Budget,
                    pass.Source,
                    count => progress?.Report(engine.CreateProgress(pass.Source, progressOffset + count)),
                    HashGuessStrategy.BannerVariant);
                checkedCandidates += count;
                if (compoundPass) compoundBudget -= count;
            }
            if (engine.RemainingUnknownCount == 0) return checkedCandidates;

            int insertionProgressOffset = checkedCandidates;
            const string insertionStage = "GAME Banner: basename insertion";
            progress?.Report(engine.CreateProgress(insertionStage, insertionProgressOffset));
            checkedCandidates += _AddBasenameWord(
                engine,
                paths,
                wordlist,
                cancellationToken,
                EsportsBannerInsertionCandidateBudget,
                insertionStage,
                count => progress?.Report(engine.CreateProgress(insertionStage, insertionProgressOffset + count)),
                HashGuessStrategy.BannerVariant);
            return checkedCandidates;
        }


    }
}
