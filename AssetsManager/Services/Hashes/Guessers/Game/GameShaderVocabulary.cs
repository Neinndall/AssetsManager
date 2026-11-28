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

        int checkedCount = _SubstituteBasenameWords(
            engine,
            shaderPaths,
            shaderWordlist.Take(MaxCustomBuildListWords),
            oldWordCount: 1,
            newWordCount: 1,
            cancellationToken,
            candidateBudget: int.MaxValue,
            source: "GAME Custom: shader vocabulary attack",
            progress: progress);

        if (engine.RemainingUnknownCount == 0) return checkedCount;

        // Many HLSL shader families (e.g. font/, ui/) concatenate words without underscores (font + fill -> fontfill).
        checkedCount += GuessCompoundShaderNames(engine, shaderPaths, shaderWordlist, cancellationToken, progress);
        return checkedCount;
    }

    private static int GuessCompoundShaderNames(
        HashGuessEngine engine,
        IReadOnlyList<string> shaderPaths,
        IReadOnlyList<string> shaderWordlist,
        CancellationToken cancellationToken,
        Action<int> progress)
    {
        var byDirectory = new Dictionary<string, (HashSet<string> Stems, HashSet<string> Extensions)>(StringComparer.Ordinal);
        foreach (string path in shaderPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!path.StartsWith("assets/shaders/", StringComparison.Ordinal) &&
                !path.StartsWith("data/shaders/", StringComparison.Ordinal)) continue;
            int slash = path.LastIndexOf('/');
            if (slash <= 0 || slash + 1 >= path.Length) continue;
            string directory = path[..slash];
            string fileName = path[(slash + 1)..];
            int dot = fileName.IndexOf('.');
            if (dot <= 0) continue;
            string stem = fileName[..dot];
            string extension = fileName[dot..];

            if (!byDirectory.TryGetValue(directory, out var entry))
            {
                entry = (new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
                int parentSlash = directory.LastIndexOf('/');
                if (parentSlash >= 0 && parentSlash + 1 < directory.Length)
                    entry.Stems.Add(directory[(parentSlash + 1)..]);
                byDirectory[directory] = entry;
            }
            if (stem.Length is >= 2 and <= 16 && !stem.Contains('_') && stem.All(char.IsAsciiLetterOrDigit))
                entry.Stems.Add(stem);
            entry.Extensions.Add(extension);
        }

        string[] words = shaderWordlist
            .Where(word => word.Length is >= 2 and <= 20 && word.All(char.IsAsciiLetterOrDigit))
            .Take(MaxCustomBuildListWords)
            .ToArray();
        if (words.Length == 0 || byDirectory.Count == 0) return 0;

        int checkedCount = 0;
        foreach (var (directory, entry) in byDirectory.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string prefix = directory + "/";
            string[] stems = entry.Stems.OrderBy(s => s, StringComparer.Ordinal).Take(16).ToArray();
            string[] extensions = entry.Extensions.OrderBy(e => e, StringComparer.Ordinal).ToArray();
            foreach (string stem in stems)
            foreach (string word in words)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (engine.RemainingUnknownCount == 0) return checkedCount;
                if (string.Equals(stem, word, StringComparison.Ordinal)) continue;
                string joined = stem + word;
                foreach (string extension in extensions)
                {
                    engine.CheckNormalizedParts(prefix, joined, extension, HashGuessStrategy.WordlistVariant,
                        "GAME Custom: shader compound vocabulary");
                    if ((++checkedCount & 0x3fff) == 0) progress?.Invoke(checkedCount);
                    if (engine.RemainingUnknownCount == 0) return checkedCount;
                }
            }
        }

        progress?.Invoke(checkedCount);
        return checkedCount;
    }
}
