using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
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

        internal int GuessEsportsBanners(
            HashGuessEngine engine,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken)
        {
            const string bannerPathPrefix = "assets/esports/sponsoredbanners/";
            IReadOnlyList<string> paths = Corpus.GetOrCreate(
                "esports-banner-paths",
                values => values
                    .Where(path => path.StartsWith(bannerPathPrefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList());
            IReadOnlyList<string> wordlist = Corpus.GetOrCreate(
                "esports-banner-wordlist",
                _ => HashGuessEngine.BuildBasenameWordlist(paths, minimumLength: 2, maximumLength: 32));
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
                            .Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
                            .Where(IsBannerToken)
                            .ToArray();

                        for (int index = 0; index + 1 < tokens.Length; index++)
                        {
                            AddWord($"{tokens[index]}_{tokens[index + 1]}");
                            AddWord($"{tokens[index]}-{tokens[index + 1]}");
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
                        value.Length >= 2 &&
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
            var passes = new[]
            {
                (Words: wordlist, OldCount: 1, NewCount: 1, Budget: EsportsBannerSingleCandidateBudget, Source: "GAME Banner: vocabulary"),
                (Words: compoundWords, OldCount: 2, NewCount: 1, Budget: EsportsBannerCompoundCandidateBudget, Source: "GAME Banner: compound names"),
                (Words: compoundWords, OldCount: 1, NewCount: 1, Budget: EsportsBannerCompoundCandidateBudget, Source: "GAME Banner: compound variants"),
                (Words: doubleWords, OldCount: 2, NewCount: 2, Budget: EsportsBannerDoubleCandidateBudget, Source: "GAME Banner: double-word variants")
            };
            foreach (var pass in passes)
            {
                if (engine.RemainingUnknownCount == 0) return checkedCandidates;
                int progressOffset = checkedCandidates;
                progress?.Report(engine.CreateProgress(pass.Source, progressOffset));
                checkedCandidates += _SubstituteBasenameWords(
                    engine,
                    paths,
                    pass.Words,
                    pass.OldCount,
                    pass.NewCount,
                    cancellationToken,
                    pass.Budget,
                    pass.Source,
                    count => progress?.Report(engine.CreateProgress(pass.Source, progressOffset + count)),
                    HashGuessStrategy.BannerVariant);
            }
            if (engine.RemainingUnknownCount == 0) return checkedCandidates;

            int insertionProgressOffset = checkedCandidates;
            const string insertionStage = "GAME Banner: basename insertion";
            progress?.Report(engine.CreateProgress(insertionStage, insertionProgressOffset));
            checkedCandidates += _AddBasenameWord(
                engine,
                paths,
                wordlist.Take(EsportsBannerDoubleWordLimit),
                cancellationToken,
                EsportsBannerInsertionCandidateBudget,
                insertionStage,
                count => progress?.Report(engine.CreateProgress(insertionStage, insertionProgressOffset + count)),
                HashGuessStrategy.BannerVariant);
            return checkedCandidates;
        }


    }
}
