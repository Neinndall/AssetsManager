using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;

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

        internal int SubstituteBinBasenameWords(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            Action<int> progress = null)
        {
            IReadOnlyList<string> binPaths = GetCustomBinPaths(dataOnly: false);
            IReadOnlyList<string> binWordlist = GetCustomUnifiedBinWords();

            return _SubstituteBasenameWords(
                engine,
                binPaths,
                binWordlist,
                oldWordCount: 1,
                newWordCount: 1,
                cancellationToken,
                candidateBudget: int.MaxValue,
                source: "GAME Custom: BIN basename wordlist",
                progress: progress);
        }

        internal int SubstituteDataBinBasenameWords(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            Action<int> progress = null,
            bool excludeCompletedBinVocabulary = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<string> dataPaths = GetCustomBinPaths(dataOnly: true);
            IEnumerable<string> dataWordlist = GetCustomBinWords(dataOnly: true).Take(MaxCustomDataBinWords);
            if (excludeCompletedBinVocabulary)
            {
                // The full BIN pass includes every data template, but its word limit can exclude data-specific vocabulary.
                var completedWords = GetCustomBinWords(dataOnly: false).Take(MaxCustomBinWords).ToHashSet(StringComparer.Ordinal);
                dataWordlist = dataWordlist.Where(word => !completedWords.Contains(word));
            }
            string[] remainingWords = dataWordlist.ToArray();
            if (remainingWords.Length == 0) return 0;

            return _SubstituteBasenameWords(
                engine,
                dataPaths,
                remainingWords,
                oldWordCount: 1,
                newWordCount: 1,
                cancellationToken,
                candidateBudget: int.MaxValue,
                source: "GAME Custom: data BIN basename wordlist",
                progress: progress);
        }

        private IReadOnlyList<string> GetCustomBinPaths(bool dataOnly) =>
            Corpus.GetOrCreate(dataOnly ? "custom-data-bin-paths" : "custom-bin-paths",
                paths => paths.Where(path => path.EndsWith(".bin", StringComparison.Ordinal) &&
                    (!dataOnly || path.StartsWith("data/", StringComparison.Ordinal))).ToList());

        private IReadOnlyList<string> GetCustomBinWords(bool dataOnly) =>
            Corpus.GetOrCreate(dataOnly ? "custom-data-bin-wordlist" : "custom-bin-wordlist",
                _ => HashGuessEngine.BuildWordlist(GetCustomBinPaths(dataOnly).Select(GetBasename)));

        private IReadOnlyList<string> GetCustomUnifiedBinWords() =>
            Corpus.GetOrCreate("custom-unified-bin-wordlist", _ =>
            {
                IReadOnlyList<string> globalWords = GetCustomBinWords(dataOnly: false);
                IReadOnlyList<string> dataWords = GetCustomBinWords(dataOnly: true);
                return globalWords.Take(MaxCustomBinWords)
                    .Concat(dataWords.Take(MaxCustomDataBinWords))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            });

        internal int SubstituteCharacterDdsBasenameWords(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            Action<int> progress = null) =>
            SubstituteCharacterTextureBasenameWords(engine, "dds", MaxCustomDdsWords, cancellationToken, progress);

        internal int SubstituteCharacterTexBasenameWords(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            Action<int> progress = null) =>
            SubstituteCharacterTextureBasenameWords(engine, "tex", MaxCustomTexWords, cancellationToken, progress);

        private int SubstituteCharacterTextureBasenameWords(
            HashGuessEngine engine,
            string extension,
            int maximumWords,
            CancellationToken cancellationToken,
            Action<int> progress)
        {
            string dottedExtension = "." + extension;
            IReadOnlyList<string> texturePaths = Corpus.GetOrCreate(
                $"custom-character-{extension}-paths",
                paths => paths
                    .Where(path => path.StartsWith("assets/characters/", StringComparison.Ordinal)
                        && path.EndsWith(dottedExtension, StringComparison.OrdinalIgnoreCase))
                    .ToList());
            IReadOnlyList<string> textureNames = Corpus.GetOrCreate(
                $"custom-character-{extension}-names",
                _ => texturePaths.Select(GetBasename).ToList());
            IReadOnlyList<string> textureWordlist = Corpus.GetOrCreate(
                $"custom-character-{extension}-wordlist",
                _ => HashGuessEngine.BuildWordlist(textureNames));

            return _SubstituteBasenameWords(
                engine,
                texturePaths,
                textureWordlist.Take(maximumWords),
                oldWordCount: 1,
                newWordCount: 1,
                cancellationToken,
                candidateBudget: int.MaxValue,
                source: $"GAME Custom: character {extension.ToUpperInvariant()} basename wordlist",
                progress: progress);
        }

        internal int SubstituteSwordlistBasenameWords(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            Action<int> progress = null,
            bool excludeCompletedBinPaths = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] words = BuildSwordlist().Take(MaxCustomSwordlistWords).ToArray();
            // BIN paths can only be skipped when the completed pass also covered every selected word.
            if (excludeCompletedBinPaths)
                excludeCompletedBinPaths = GetCustomUnifiedBinWords().ToHashSet(StringComparer.Ordinal).IsSupersetOf(words);
            IReadOnlyList<string> paths = Corpus.GetOrCreate(
                excludeCompletedBinPaths ? "custom-focused-swordlist-paths-nobin" : "custom-focused-wordlist-paths",
                values => excludeCompletedBinPaths
                    ? values.Where(path => !path.EndsWith(".bin", StringComparison.Ordinal)).ToList()
                    : values.ToList());
            return _SubstituteBasenameWords(
                engine,
                paths,
                words,
                oldWordCount: 1,
                newWordCount: 1,
                cancellationToken,
                candidateBudget: int.MaxValue,
                source: "GAME Custom: SwordList basename substitution",
                progress: progress);
        }

        internal int SubstituteWordlistBasenameWords(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            Action<int> progress = null)
        {
            IReadOnlyList<string> paths = Corpus.GetOrCreate("custom-focused-wordlist-paths", values => values.ToList());
            return _SubstituteBasenameWords(
                engine,
                paths,
                BuildWordlist().Take(MaxCustomBuildListWords),
                oldWordCount: 1,
                newWordCount: 1,
                cancellationToken,
                candidateBudget: int.MaxValue,
                source: "GAME Custom: WordList basename substitution",
                progress: progress);
        }

        internal int RunCustomAttacks(
            HashGuessEngine engine,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            IReadOnlySet<string> selectedSubMethods = null)
        {
            int checkedCandidates = 0;
            if (engine.RemainingUnknownCount == 0) return checkedCandidates;

            bool ShouldRun(string subId) => selectedSubMethods == null || selectedSubMethods.Contains(subId);

            if (ShouldRun("game-custom-bin"))
            {
                progress?.Report(engine.CreateProgress("GAME Custom: BIN basename wordlist", checkedCandidates));
                int progressOffset = checkedCandidates;
                checkedCandidates += SubstituteBinBasenameWords(
                    engine,
                    cancellationToken,
                    count => progress?.Report(engine.CreateProgress(
                        "GAME Custom: BIN basename wordlist", progressOffset + count)));
                if (engine.RemainingUnknownCount == 0) return checkedCandidates;
            }

            else if (ShouldRun("game-custom-databin"))
            {
                progress?.Report(engine.CreateProgress(
                    "GAME Custom: data BIN basename wordlist", checkedCandidates));
                int progressOffset = checkedCandidates;
                checkedCandidates += SubstituteDataBinBasenameWords(
                    engine,
                    cancellationToken,
                    count => progress?.Report(engine.CreateProgress(
                        "GAME Custom: data BIN basename wordlist", progressOffset + count)),
                    excludeCompletedBinVocabulary: ShouldRun("game-custom-bin"));
                if (engine.RemainingUnknownCount == 0) return checkedCandidates;
            }

            if (ShouldRun("game-custom-dds"))
            {
                progress?.Report(engine.CreateProgress(
                    "GAME Custom: character DDS basename wordlist", checkedCandidates));
                int progressOffset = checkedCandidates;
                checkedCandidates += SubstituteCharacterDdsBasenameWords(
                    engine,
                    cancellationToken,
                    count => progress?.Report(engine.CreateProgress(
                        "GAME Custom: character DDS basename wordlist", progressOffset + count)));
                if (engine.RemainingUnknownCount == 0) return checkedCandidates;
            }

            if (ShouldRun("game-custom-tex"))
            {
                progress?.Report(engine.CreateProgress(
                    "GAME Custom: character TEX basename wordlist", checkedCandidates));
                int progressOffset = checkedCandidates;
                checkedCandidates += SubstituteCharacterTexBasenameWords(
                    engine,
                    cancellationToken,
                    count => progress?.Report(engine.CreateProgress(
                        "GAME Custom: character TEX basename wordlist", progressOffset + count)));
                if (engine.RemainingUnknownCount == 0) return checkedCandidates;
            }

            if (ShouldRun("game-custom-swordlist"))
            {
                progress?.Report(engine.CreateProgress(
                    "GAME Custom: SwordList basename substitution", checkedCandidates));
                int progressOffset = checkedCandidates;
                checkedCandidates += SubstituteSwordlistBasenameWords(
                    engine,
                    cancellationToken,
                    count => progress?.Report(engine.CreateProgress(
                        "GAME Custom: SwordList basename substitution", progressOffset + count)),
                    excludeCompletedBinPaths: ShouldRun("game-custom-bin"));
                if (engine.RemainingUnknownCount == 0) return checkedCandidates;
            }

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
                        (int)Math.Min(int.MaxValue, progressOffset + count))));
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
                        (int)Math.Min(int.MaxValue, progressOffset + count))));
                checkedCandidates = (int)Math.Min(int.MaxValue, checkedCandidates + textureCheckedCandidates);
            }

            return checkedCandidates;
        }

        internal int SubstituteShaderVocabWords(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            Action<int> progress = null)
        {
            if (engine.RemainingUnknownCount == 0) return 0;

            var shaderPattern = new Regex(@"\.(?:[pv]s(?:_[23]_0)?|cs)(?=$|[.-])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            IReadOnlyList<string> shaderPaths = Corpus.GetOrCreate(
                "custom-shader-paths",
                paths => paths
                    .Where(path => path.StartsWith("assets/shaders/", StringComparison.OrdinalIgnoreCase) ||
                                   path.StartsWith("data/shaders/", StringComparison.OrdinalIgnoreCase) ||
                                   shaderPattern.IsMatch(path))
                    .ToList());

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

        internal long SubstituteAnimationBuildListWords(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            long candidateBudget = long.MaxValue,
            Action<long> progress = null)
        {
            if (engine.RemainingUnknownCount == 0 || candidateBudget <= 0) return 0;

            IReadOnlyList<string> animPaths = Corpus.GetOrCreate(
                "custom-character-anm-paths",
                paths => paths
                    .Where(path => (path.StartsWith("assets/characters/", StringComparison.OrdinalIgnoreCase)
                                 || path.StartsWith("data/characters/", StringComparison.OrdinalIgnoreCase))
                                 && path.Contains("/animations/", StringComparison.OrdinalIgnoreCase)
                                 && path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase))
                    .ToList());

            IReadOnlyList<string> words = GetExpandedGlobalAnimationActions(cancellationToken);
            if (animPaths.Count == 0 || words.Count == 0) return 0;

            IReadOnlyList<string> prioritizedWords = GetGlobalAnimationActions(cancellationToken)
                .Concat(words)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            IReadOnlyDictionary<string, IReadOnlyList<string>> wordsByFamily = Corpus.GetOrCreate(
                "expanded-animation-actions-by-family-v2",
                _ => prioritizedWords
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

            long checkedCandidates = 0;
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
                long roundCandidateCount = 0;
                foreach (var format in orderedFormats)
                {
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

        /// <summary>
        /// Combines learned texture suffixes across skins, companion themes and map kitpieces.
        /// </summary>
        internal long SubstituteTextureBuildListWords(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            long candidateBudget = long.MaxValue,
            Action<long> progress = null)
        {
            if (engine.RemainingUnknownCount == 0 || candidateBudget <= 0) return 0;

            var familyIndex = Corpus.GetOrCreate("skin-texture-families", paths => new GameTextureFamilyIndex(paths, cancellationToken));
            return familyIndex.RunBuildList(engine, cancellationToken, candidateBudget, progress);
        }
    }
}
