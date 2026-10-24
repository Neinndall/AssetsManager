using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Views.Models.Hashes;

namespace AssetsManager.Services.Hashes.Guessers.Game;

internal sealed partial class GameHashGuesser
{
    private const int MaxCustomBuildListWords = 20_000;

    internal int SubstituteShaderVocabWords(
        HashGuessEngine engine,
        CancellationToken cancellationToken,
        Action<int> progress = null)
    {
        if (engine.RemainingUnknownCount == 0) return 0;

        IReadOnlyList<string> shaderPaths = Corpus.GetOrCreate(
            "custom-shader-paths",
            paths =>
            {
                var shaderPattern = new Regex(@"\.(?:[pv]s(?:_[23]_0)?|cs)(?=$|[.-])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
                return paths.Where(path => path.StartsWith("assets/shaders/", StringComparison.OrdinalIgnoreCase) ||
                                           path.StartsWith("data/shaders/", StringComparison.OrdinalIgnoreCase) ||
                                           shaderPattern.IsMatch(path)).ToList();
            });

        IReadOnlyList<string> shaderNames = Corpus.GetOrCreate(
            "custom-shader-names",
            _ => shaderPaths.Select(GetBasename).ToList());

        IReadOnlyList<string> shaderWordlist = Corpus.GetOrCreate(
            "custom-shader-wordlist",
            _ => HashGuessEngine.BuildWordlist(shaderNames));

        if (shaderPaths.Count == 0 || shaderWordlist.Count == 0) return 0;

        return _SubstituteBasenameWords(
            engine,
            shaderPaths,
            shaderWordlist.Take(MaxCustomBuildListWords),
            oldWordCount: 1,
            newWordCount: 1,
            cancellationToken,
            candidateBudget: int.MaxValue,
            source: "GAME Custom: shader vocabulary attack",
            progress: progress);
    }
}
