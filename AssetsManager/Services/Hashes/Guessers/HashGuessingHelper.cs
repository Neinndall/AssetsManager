using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Views.Models.Hashes;

namespace AssetsManager.Services.Hashes.Guessers
{
    internal readonly record struct HashGuessAttackLimits(
        int SingleCandidateBudget,
        int CompoundCandidateBudget,
        int DoubleCandidateBudget,
        int InsertionCandidateBudget,
        int DoubleWordLimit);

    internal static class HashGuessingHelper
    {
        internal static int RunScopedBasenameAttack(
            HashGuesser guesser,
            HashCorpusIndex corpus,
            HashGuessEngine engine,
            IReadOnlyList<string> paths,
            IReadOnlyList<string> singleWords,
            string corpusKey,
            string stagePrefix,
            HashGuessStrategy strategy,
            HashGuessAttackLimits limits,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(guesser);
            ArgumentNullException.ThrowIfNull(corpus);
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(paths);
            ArgumentNullException.ThrowIfNull(singleWords);
            if (paths.Count == 0 || engine.RemainingUnknownCount == 0) return 0;

            IReadOnlyList<string> compoundWords = corpus.GetOrCreate(
                $"{corpusKey}-compound-wordlist",
                _ => BuildCompoundWordlist(paths));

            int checkedCandidates = 0;
            checkedCandidates += RunWordPass(
                guesser,
                engine,
                paths,
                singleWords,
                oldWordCount: 1,
                newWordCount: 1,
                limits.SingleCandidateBudget,
                $"{stagePrefix}: vocabulary",
                strategy,
                progress,
                cancellationToken,
                checkedCandidates);
            if (engine.RemainingUnknownCount == 0) return checkedCandidates;

            // Collapse two attested basename tokens into one compound candidate.
            checkedCandidates += RunWordPass(
                guesser,
                engine,
                paths,
                compoundWords,
                oldWordCount: 2,
                newWordCount: 1,
                limits.CompoundCandidateBudget,
                $"{stagePrefix}: compound names",
                strategy,
                progress,
                cancellationToken,
                checkedCandidates);
            if (engine.RemainingUnknownCount == 0) return checkedCandidates;

            checkedCandidates += RunWordPass(
                guesser,
                engine,
                paths,
                compoundWords,
                oldWordCount: 1,
                newWordCount: 1,
                limits.CompoundCandidateBudget,
                $"{stagePrefix}: compound variants",
                strategy,
                progress,
                cancellationToken,
                checkedCandidates);
            if (engine.RemainingUnknownCount == 0) return checkedCandidates;

            IReadOnlyList<string> doubleWords = corpus.GetOrCreate(
                $"{corpusKey}-double-words",
                _ => compoundWords
                    .SelectMany(word => word.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
                    .Concat(singleWords)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(limits.DoubleWordLimit)
                    .ToList());
            checkedCandidates += RunWordPass(
                guesser,
                engine,
                paths,
                doubleWords,
                oldWordCount: 2,
                newWordCount: 2,
                limits.DoubleCandidateBudget,
                $"{stagePrefix}: double-word variants",
                strategy,
                progress,
                cancellationToken,
                checkedCandidates);
            if (engine.RemainingUnknownCount == 0) return checkedCandidates;

            int progressOffset = checkedCandidates;
            string insertionStage = $"{stagePrefix}: basename insertion";
            progress?.Report(engine.CreateProgress(insertionStage, progressOffset));
            checkedCandidates += guesser.AddBasenameWordCore(
                engine,
                paths,
                singleWords.Take(limits.DoubleWordLimit),
                cancellationToken,
                limits.InsertionCandidateBudget,
                insertionStage,
                count => progress?.Report(engine.CreateProgress(insertionStage, progressOffset + count)),
                strategy);
            return checkedCandidates;
        }

        internal static IReadOnlyList<string> BuildScopedWordlist(
            IEnumerable<string> paths,
            int minimumLength = 2,
            int maximumLength = 32) =>
            HashGuessEngine.BuildBasenameWordlist(paths, minimumLength, maximumLength);

        private static int RunWordPass(
            HashGuesser guesser,
            HashGuessEngine engine,
            IReadOnlyList<string> paths,
            IReadOnlyList<string> words,
            int oldWordCount,
            int newWordCount,
            int candidateBudget,
            string source,
            HashGuessStrategy strategy,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            int progressOffset)
        {
            if (words.Count == 0 || engine.RemainingUnknownCount == 0) return 0;
            progress?.Report(engine.CreateProgress(source, progressOffset));
            return guesser.SubstituteBasenameWordsCore(
                engine,
                paths,
                words,
                oldWordCount,
                newWordCount,
                cancellationToken,
                candidateBudget,
                source,
                count => progress?.Report(engine.CreateProgress(source, progressOffset + count)),
                strategy);
        }

        private static IReadOnlyList<string> BuildCompoundWordlist(IEnumerable<string> paths)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                string basename = GetBasename(path);
                int extension = basename.LastIndexOf('.');
                string stem = extension > 0 ? basename[..extension] : basename;
                string[] tokens = stem
                    .Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(IsToken)
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
        }

        private static bool IsCompound(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 48) return false;
            return value
                .Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .All(IsToken);
        }

        private static bool IsToken(string value) =>
            value.Length >= 2 &&
            value.Length <= 32 &&
            value.All(char.IsLetterOrDigit);

        private static string GetBasename(string path)
        {
            int separator = path.LastIndexOf('/');
            return separator >= 0 ? path[(separator + 1)..] : Path.GetFileName(path);
        }
    }
}
