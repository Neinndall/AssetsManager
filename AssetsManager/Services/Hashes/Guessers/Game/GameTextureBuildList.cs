using System;
using System.Threading;

namespace AssetsManager.Services.Hashes.Guessers.Game;

internal sealed partial class GameHashGuesser
{
    /// <summary>
    /// Combines learned texture suffixes across skins, companion themes and map kitpieces.
    /// </summary>
    internal long SubstituteTextureBuildListWords(
        HashGuessEngine engine,
        CancellationToken cancellationToken,
        long candidateBudget = long.MaxValue,
        Action<long> progress = null,
        string rootDirectory = null, GameWadPathCache wadPathCache = null)
    {
        if (engine.RemainingUnknownCount == 0 || candidateBudget <= 0) return 0;

        long roleCheckedCandidates = GuessMaterialTextureRoles(engine, rootDirectory, cancellationToken, candidateBudget, progress);
        if (engine.RemainingUnknownCount == 0 || roleCheckedCandidates >= candidateBudget) return roleCheckedCandidates;
        long localCheckedCandidates = string.IsNullOrWhiteSpace(rootDirectory) ? 0 :
            GuessLocalNamePatterns(engine, rootDirectory, animations: false, cancellationToken, candidateBudget - roleCheckedCandidates,
                count => progress?.Invoke(roleCheckedCandidates + count), wadPathCache);
        localCheckedCandidates += roleCheckedCandidates;
        if (engine.RemainingUnknownCount == 0 || localCheckedCandidates >= candidateBudget) return localCheckedCandidates;
        var familyIndex = Corpus.GetOrCreate("skin-texture-families", paths => new GameTextureFamilyIndex(paths, cancellationToken));
        return localCheckedCandidates + familyIndex.RunBuildList(engine, cancellationToken,
            candidateBudget - localCheckedCandidates, count => progress?.Invoke(localCheckedCandidates + count));
    }
}
