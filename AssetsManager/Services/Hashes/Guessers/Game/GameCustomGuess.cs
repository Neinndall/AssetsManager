using System;
using System.Collections.Generic;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models.Hashes;

namespace AssetsManager.Services.Hashes.Guessers.Game
{
    internal sealed partial class GameHashGuesser
    {
        internal int SubstituteBasenameWords(HashGuessEngine engine, CancellationToken cancellationToken, int candidateBudget = int.MaxValue)
        {
            IReadOnlyList<string> words = BuildWordlist();
            return _SubstituteBasenameWords(
                engine,
                KnownPaths,
                words,
                oldWordCount: 1,
                newWordCount: 1,
                cancellationToken,
                candidateBudget,
                "GAME basename word substitution");
        }

        internal int RunCustomAttacks(
            HashGuessEngine engine,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            IReadOnlySet<string> selectedSubMethods = null,
            string rootDirectory = null)
        {
            int checkedCandidates = 0;
            if (engine.RemainingUnknownCount == 0) return checkedCandidates;

            bool ShouldRun(string subId) => selectedSubMethods == null || selectedSubMethods.Contains(subId);
            var wadPathCache = !string.IsNullOrWhiteSpace(rootDirectory) &&
                ShouldRun("game-custom-animations") && ShouldRun("game-custom-textures") ? new GameWadPathCache() : null;

            if (ShouldRun("game-custom-shaders"))
            {
                progress?.Report(engine.CreateProgress(
                    "GAME Custom: shader vocabulary attack", checkedCandidates));
                int progressOffset = checkedCandidates;
                checkedCandidates += SubstituteShaderVocabWords(
                    engine,
                    cancellationToken,
                    progress: count => progress?.Report(engine.CreateProgress(
                        "GAME Custom: shader vocabulary attack", progressOffset + count)));
                if (engine.RemainingUnknownCount == 0) return checkedCandidates;
            }

            if (ShouldRun("game-custom-animation-bin"))
            {
                progress?.Report(engine.CreateProgress("GAME Custom: BIN-referenced animations", checkedCandidates));
                int progressOffset = checkedCandidates;
                long binCheckedCandidates = GuessBinReferencedAnimations(engine, rootDirectory, cancellationToken,
                    progress: count => progress?.Report(engine.CreateProgress(
                        "GAME Custom: BIN-referenced animations",
                        (int)Math.Min(int.MaxValue, progressOffset + count))));
                checkedCandidates = (int)Math.Min(int.MaxValue, checkedCandidates + binCheckedCandidates);
                if (engine.RemainingUnknownCount == 0) return checkedCandidates;
            }

            if (ShouldRun("game-custom-animations"))
            {
                progress?.Report(engine.CreateProgress(
                    "GAME Custom: animation actions build-list", checkedCandidates));
                int progressOffset = checkedCandidates;
                long animationCheckedCandidates = SubstituteAnimationBuildListWords(
                    engine,
                    cancellationToken,
                    progress: count => progress?.Report(engine.CreateProgress(
                        "GAME Custom: animation actions build-list",
                        (int)Math.Min(int.MaxValue, progressOffset + count))),
                    rootDirectory: rootDirectory, wadPathCache: wadPathCache);
                checkedCandidates = (int)Math.Min(int.MaxValue, checkedCandidates + animationCheckedCandidates);
                if (engine.RemainingUnknownCount == 0) return checkedCandidates;
            }

            if (ShouldRun("game-custom-textures"))
            {
                progress?.Report(engine.CreateProgress(
                    "GAME Custom: texture build-list", checkedCandidates));
                int progressOffset = checkedCandidates;
                long textureCheckedCandidates = SubstituteTextureBuildListWords(
                    engine,
                    cancellationToken,
                    progress: count => progress?.Report(engine.CreateProgress(
                        "GAME Custom: texture build-list",
                        (int)Math.Min(int.MaxValue, progressOffset + count))),
                    rootDirectory: rootDirectory, wadPathCache: wadPathCache);
                checkedCandidates = (int)Math.Min(int.MaxValue, checkedCandidates + textureCheckedCandidates);
            }

            return checkedCandidates;
        }

    }
}
