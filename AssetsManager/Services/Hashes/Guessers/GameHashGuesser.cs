using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Parsers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes.Guessers
{
    internal sealed class GameHashGuesser : HashGuesser
    {
        private static readonly string[] ShaderExtensions = { ".ps_2_0", ".ps_3_0", ".vs_2_0", ".vs_3_0", ".ps", ".vs" };
        private static readonly string[] ShaderVariants = { ".dx11", ".dx9", ".dx9sm3", ".glsl", ".metal", "-dx11", "-metal" };
        private readonly ConditionalWeakTable<HashGuessEngine, ConcurrentDictionary<string, byte>> _scannedWadCharacters = new();
        private const int MaxCustomBuildListWords = 50_000;
        private const int MaxCustomBinWords = 20_000;
        private const int MaxCustomDataBinWords = 20_000;
        private const int MaxCustomSwordlistWords = 20_000;
        private const int MaxCustomDdsWords = 20_000;
        private const int MaxCustomTexWords = 20_000;
        private const int EsportsBannerSingleCandidateBudget = 2_000_000;
        private const int EsportsBannerCompoundCandidateBudget = 10_000_000;
        private const int EsportsBannerDoubleCandidateBudget = 2_000_000;
        private const int EsportsBannerInsertionCandidateBudget = 750_000;
        private const int EsportsBannerDoubleWordLimit = 96;
        private const int AnimationBinFallbackCandidateBudget = 100_000;
        private static readonly Regex AnimationBinPathRegex = new(
            @"^(?:assets|data)/characters/(?<character>[^/]+)/(?:animations/(?<skin>[^/]+)|skins/(?<skin>[^/]+)(?:/animations)?(?:/[^/]+)?|themes/(?<skin>[^/]+)(?:/animations)?(?:/[^/]+)?)\.(?:bin|inibin)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private readonly record struct AnimationFileLink(uint NameHash, ulong PathHash, string Path);
        private static readonly Regex SkinPathRegex = new(
            @"characters/(?<champ>[^/]+)/skins/(?<skin>base|skin0*(?<num>\d+))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MaterialPathRegex = new(
            @"characters/(?<champ>[^/]+)/skins/(?<skin>[^/]+)/materials/(?<mat>[^/]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly string[] SkinTextureSuffixes =
        {
            "_tx.tex", "_tx_cm.tex", "_cm_tx.tex", "_tx_cm2.tex", "_cm.tex", ".tex", "_d.tex",
            "_base_tx.tex", "_base_tx_cm.tex", "_base_cm_tx.tex", "_diffuse.tex",
            "_tx.dds", "_tx_cm.dds", "_cm_tx.dds", "_tx_cm2.dds", "_cm.dds", ".dds",
            "_tx_cm.project_jade.tex", "_tx_cm2.project_jade.tex", "_tx.project_jade.tex", ".project_jade.tex"
        };
        private static readonly string[] MaterialTextureSuffixes =
        {
            "_tx_cm.tex", "_tx.tex", ".tex",
            "_tx_cm.dds", "_tx.dds", ".dds"
        };

        private static readonly string[] CanonicalMaterialRoles =
        {
            "", "_mask", "_scroll", "_scrollmask", "_flowmap", "_matcap"
        };


        private readonly LogService _logService;
        private readonly Func<uint, string> _resolveBinHash;

        internal GameHashGuesser(HashFile hashFile, LogService logService = null, Func<uint, string> resolveBinHash = null)
            : base(hashFile, "*.wad.client")
        {
            if (hashFile.Domain != HashGuessDomain.Game) throw new ArgumentException("GAME guesser requires a GAME hash file.", nameof(hashFile));
            _logService = logService;
            _resolveBinHash = resolveBinHash;
        }

        internal GameHashGuesser() : this(new HashFile(HashGuessDomain.Game, Array.Empty<string>())) { }

        internal IReadOnlyList<string> GetCharacters() =>
            Corpus.GetOrCreate("characters", values => values
                .Select(path => Regex.Match(path, @"^(?:assets/|data/)?characters/([^/.]+)(?:/|$)", RegexOptions.IgnoreCase))
                .Where(match => match.Success)
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList());

        internal override IReadOnlyList<string> BuildWordlist() =>
            Corpus.GetOrCreate("wordlist", HashGuessEngine.BuildWordlist);

        internal IReadOnlyList<string> BuildSwordlist() =>
            Corpus.GetOrCreate(
                "swordlist",
                values => HashGuessEngine.BuildWordlist(
                    values
                        .Where(path => path.Contains(".bin", StringComparison.Ordinal))
                        .Select(GetBasename)));

        internal IEnumerable<HashGuessCandidate> SubstituteNumbers(int maximum = 100, int? digits = null, bool inferDigits = false) =>
            GenerateNumberCandidates(maximum, int.MaxValue, digits, inferDigits, includeCommonPadding: false);

        internal int SubstituteNumbers(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            int maximum = 100,
            int? digits = null,
            Action<int> progress = null) =>
            base._SubstituteNumbers(
                engine,
                KnownPaths,
                maximum,
                digits,
                inferDigits: false,
                cancellationToken: cancellationToken,
                source: "Generated numeric variant",
                progress: progress);

        protected override bool AnchorNumberMatchesToFileName => true;

        internal IEnumerable<HashGuessCandidate> SubstituteBasicNumbers(int maximum = 100)
        {
            foreach (HashGuessCandidate candidate in SubstituteNumbers(maximum))
                yield return candidate;

            // Two-digit values above 9 are identical to their unpadded form.
            foreach (HashGuessCandidate candidate in SubstituteNumbers(Math.Min(maximum, 10), digits: 2))
                yield return candidate;
        }

        internal int CheckBasenamePrefixes(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            IEnumerable<string> prefixes = null,
            int candidateBudget = int.MaxValue,
            Action<int> progress = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            if (candidateBudget < 0) throw new ArgumentOutOfRangeException(nameof(candidateBudget));
            if (candidateBudget == 0) return 0;

            string[] values = (prefixes ?? new[] { "2x_", "2x_sd_", "4x_", "4x_sd_", "sd_", "tft_", "common_", "base_", "sru_", "icon_" }).ToArray();
            var candidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in KnownPaths)
            {
                int separator = path.LastIndexOf('/');
                string directory = separator >= 0 ? path[..(separator + 1)] : string.Empty;
                string basename = separator >= 0 ? path[(separator + 1)..] : path;
                foreach (string prefix in values)
                    candidates.Add(directory + prefix + basename);
            }

            IEnumerable<HashGuessCandidate> orderedCandidates = candidates
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => new HashGuessCandidate(path, HashGuessStrategy.PrefixVariant));
            if (candidateBudget != int.MaxValue) orderedCandidates = orderedCandidates.Take(candidateBudget);
            int checkedCount = CheckIter(
                engine,
                orderedCandidates,
                "GAME basename prefixes",
                cancellationToken,
                progress);
            return checkedCount;
        }

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
            IReadOnlyList<string> binPaths = Corpus.GetOrCreate(
                "custom-bin-paths",
                paths => paths.Where(path => path.EndsWith(".bin", StringComparison.Ordinal)).ToList());
            IReadOnlyList<string> binNames = Corpus.GetOrCreate(
                "custom-bin-names",
                _ => binPaths.Select(GetBasename).ToList());
            IReadOnlyList<string> binWordlist = Corpus.GetOrCreate(
                "custom-bin-wordlist",
                _ => HashGuessEngine.BuildWordlist(binNames));

            return _SubstituteBasenameWords(
                engine,
                binPaths,
                binWordlist.Take(MaxCustomBinWords),
                oldWordCount: 1,
                newWordCount: 1,
                cancellationToken,
                candidateBudget: int.MaxValue,
                source: "GAME Custom: BIN basename wordlist",
                progress);
        }

        internal int SubstituteDataBinBasenameWords(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            Action<int> progress = null)
        {
            IReadOnlyList<string> dataPaths = Corpus.GetOrCreate(
                "custom-data-bin-paths",
                paths => paths
                    .Where(path => path.StartsWith("data/", StringComparison.Ordinal)
                        && path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                    .ToList());
            IReadOnlyList<string> dataNames = Corpus.GetOrCreate(
                "custom-data-bin-names",
                _ => dataPaths.Select(GetBasename).ToList());
            IReadOnlyList<string> dataWordlist = Corpus.GetOrCreate(
                "custom-data-bin-wordlist",
                _ => HashGuessEngine.BuildWordlist(dataNames));

            return _SubstituteBasenameWords(
                engine,
                dataPaths,
                dataWordlist.Take(MaxCustomDataBinWords),
                oldWordCount: 1,
                newWordCount: 1,
                cancellationToken,
                candidateBudget: int.MaxValue,
                source: "GAME Custom: data BIN basename wordlist",
                progress);
        }

        internal int SubstituteCharacterDdsBasenameWords(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            Action<int> progress = null)
        {
            IReadOnlyList<string> characterDdsPaths = Corpus.GetOrCreate(
                "custom-character-dds-paths",
                paths => paths
                    .Where(path => path.StartsWith("assets/characters/", StringComparison.Ordinal)
                        && path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    .ToList());
            IReadOnlyList<string> characterDdsNames = Corpus.GetOrCreate(
                "custom-character-dds-names",
                _ => characterDdsPaths.Select(GetBasename).ToList());
            IReadOnlyList<string> characterDdsWordlist = Corpus.GetOrCreate(
                "custom-character-dds-wordlist",
                _ => HashGuessEngine.BuildWordlist(characterDdsNames));

            return _SubstituteBasenameWords(
                engine,
                characterDdsPaths,
                characterDdsWordlist.Take(MaxCustomDdsWords),
                oldWordCount: 1,
                newWordCount: 1,
                cancellationToken,
                candidateBudget: int.MaxValue,
                source: "GAME Custom: character DDS basename wordlist",
                progress);
        }

        internal int SubstituteCharacterTexBasenameWords(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            Action<int> progress = null)
        {
            IReadOnlyList<string> characterTexPaths = Corpus.GetOrCreate(
                "custom-character-tex-paths",
                paths => paths
                    .Where(path => path.StartsWith("assets/characters/", StringComparison.Ordinal)
                        && path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                    .ToList());
            IReadOnlyList<string> characterTexNames = Corpus.GetOrCreate(
                "custom-character-tex-names",
                _ => characterTexPaths.Select(GetBasename).ToList());
            IReadOnlyList<string> characterTexWordlist = Corpus.GetOrCreate(
                "custom-character-tex-wordlist",
                _ => HashGuessEngine.BuildWordlist(characterTexNames));

            return _SubstituteBasenameWords(
                engine,
                characterTexPaths,
                characterTexWordlist.Take(MaxCustomTexWords),
                oldWordCount: 1,
                newWordCount: 1,
                cancellationToken,
                candidateBudget: int.MaxValue,
                source: "GAME Custom: character TEX basename wordlist",
                progress);
        }

        internal int SubstituteSwordlistBasenameWords(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            Action<int> progress = null)
        {
            IReadOnlyList<string> paths = Corpus.GetOrCreate("custom-focused-wordlist-paths", values => values.ToList());
            return _SubstituteBasenameWords(
                engine,
                paths,
                BuildSwordlist().Take(MaxCustomSwordlistWords),
                oldWordCount: 1,
                newWordCount: 1,
                cancellationToken,
                candidateBudget: int.MaxValue,
                source: "GAME Custom: SwordList basename substitution",
                progress);
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
                progress);
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

            if (ShouldRun("game-custom-databin"))
            {
                progress?.Report(engine.CreateProgress(
                    "GAME Custom: data BIN basename wordlist", checkedCandidates));
                int progressOffset = checkedCandidates;
                checkedCandidates += SubstituteDataBinBasenameWords(
                    engine,
                    cancellationToken,
                    count => progress?.Report(engine.CreateProgress(
                        "GAME Custom: data BIN basename wordlist", progressOffset + count)));
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
                        "GAME Custom: SwordList basename substitution", progressOffset + count)));
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

            var shaderPattern = new Regex(@".*\.[pv]s(?:_[23]_0|(?=$|[.-]))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            IReadOnlyList<string> shaderPaths = Corpus.GetOrCreate(
                "custom-shader-paths",
                paths => paths
                    .Where(path => path.StartsWith("assets/shaders/", StringComparison.OrdinalIgnoreCase) || shaderPattern.IsMatch(path))
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

        internal int AddBasenameWord(HashGuessEngine engine, CancellationToken cancellationToken, int candidateBudget = int.MaxValue)
        {
            var paths = Corpus.GetOrCreate("word-addition-paths", values => values.Where(path =>
                !path.Contains("assets/characters/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("vo/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("sfx/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("skins_skin", StringComparison.OrdinalIgnoreCase)).ToList());
            IReadOnlyList<string> words = BuildWordlist();
            return _AddBasenameWord(
                engine,
                paths,
                words,
                cancellationToken,
                candidateBudget,
                source: "GAME basename word addition");
        }

        internal IEnumerable<HashGuessCandidate> SubstituteCharacter(int candidateBudget = int.MaxValue) => GenerateCharacterSubstitutionCandidates(candidateBudget);
        internal IEnumerable<HashGuessCandidate> SubstituteSkinNumbers(int candidateBudget = int.MaxValue) => GenerateSkinNumberCandidates(candidateBudget);
        internal IEnumerable<HashGuessCandidate> SubstituteSuffixes(int candidateBudget = int.MaxValue) => GenerateSuffixCandidates(candidateBudget);
        internal int SubstituteLang(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            string source = "Generated locale variant",
            Action<int> progress = null)
        {
            ArgumentNullException.ThrowIfNull(engine);

            string[] langs =
            {
                "ar_ae", "ar_eg", "cs_cz", "de_de", "el_gr", "en_au", "en_gb", "en_ph", "en_pl", "en_sg",
                "en_us", "es_ar", "es_es", "es_mx", "fr_fr", "hu_hu", "id_id", "it_it", "ja_jp", "ko_kr",
                "ms_my", "pl_pl", "pt_br", "ro_ro", "ru_ru", "th_th", "tr_tr", "vi_vn", "vn_vn", "zh_cn",
                "zh_my", "zh_tw"
            };
            var langsRegex = new Regex($"({string.Join("|", langs)})", RegexOptions.Compiled);

            IReadOnlyList<string> formats = KnownPaths
                .Where(path => langsRegex.IsMatch(path))
                .Select(path => langsRegex.Replace(path, "{}"))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            int checkedCount = 0;
            foreach (string format in ProgressIterator(formats, value => value, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkedCount += CheckIter(
                    engine,
                    langs.Select(lang => new HashGuessCandidate(
                        format.Replace("{}", lang, StringComparison.Ordinal),
                        HashGuessStrategy.LanguageVariant)),
                    source,
                    cancellationToken);
                progress?.Invoke(checkedCount);
                if (engine.RemainingUnknownCount == 0) break;
            }

            return checkedCount;
        }

        internal int GuessFromLcuHashes(
            HashGuessEngine engine,
            HashGuesser lcuGuesser,
            CancellationToken cancellationToken,
            int candidateBudget = int.MaxValue,
            Action<int> progress = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(lcuGuesser);
            if (lcuGuesser.Domain != HashGuessDomain.Lcu)
                throw new ArgumentException("GAME cross-domain guessing requires an LCU guesser.", nameof(lcuGuesser));
            if (candidateBudget < 0) throw new ArgumentOutOfRangeException(nameof(candidateBudget));
            if (candidateBudget == 0) return 0;

            const string source = "GAME from LCU hashes";
            var regex = new Regex(
                @"^plugins/rcp-be-lol-game-data/global/default/((?:assets|data)/.*)\.(png|jpg|json)$",
                RegexOptions.Compiled);
            int checkedCount = 0;
            foreach (string lcuPath in lcuGuesser.KnownPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) break;

                Match match = regex.Match(lcuPath);
                if (!match.Success) continue;
                string path = match.Groups[1].Value;
                string extension = match.Groups[2].Value;
                string candidatePath = extension is "png" or "jpg"
                    ? $"{path}.dds"
                    : $"{path}.{extension}";

                Check(engine, candidatePath, HashGuessStrategy.CrossDomainGame, source);
                checkedCount++;
                if ((checkedCount & 0x1fff) == 0)
                {
                    progress?.Invoke(checkedCount);
                }
            }

            progress?.Invoke(checkedCount);
            return checkedCount;
        }

        internal IEnumerable<HashGuessCandidate> GuessFromBinEntryBasenames(IEnumerable<string> binEntryPaths)
        {
            var basenames = binEntryPaths.Select(Path.GetFileName)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            var extensions = KnownPaths.Select(Path.GetExtension)
                .Where(extension => extension.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(extension => !Regex.IsMatch(extension, @"(?:glsl|dx9|dx9sm3|dx11|metal)_", RegexOptions.IgnoreCase))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();

            foreach (string extension in extensions)
            foreach (string basename in basenames)
                yield return new HashGuessCandidate(basename + extension, HashGuessStrategy.CrossDomainGame);
        }

        internal int GuessCharactersFiles(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            IEnumerable<string> characters = null,
            int candidateBudget = int.MaxValue,
            Action<int> progress = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            if (candidateBudget < 0) throw new ArgumentOutOfRangeException(nameof(candidateBudget));
            if (candidateBudget == 0) return 0;

            IReadOnlyList<string> rawCharacters = (characters ?? GetCharacters())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();

            var characterList = new List<string>(rawCharacters);
            foreach (string ch in rawCharacters)
            {
                if (!ch.StartsWith("jade_", StringComparison.OrdinalIgnoreCase) &&
                    !ch.StartsWith("pet", StringComparison.OrdinalIgnoreCase))
                {
                    characterList.Add($"jade_{ch}");
                }
            }

            int checkedCount = 0;
            var emittedCharacterPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int CheckCharacterPaths(IEnumerable<string> paths)
            {
                int remaining = candidateBudget == int.MaxValue ? int.MaxValue : candidateBudget - checkedCount;
                if (remaining <= 0 || engine.RemainingUnknownCount == 0) return 0;
                IEnumerable<HashGuessCandidate> candidatesToCheck = paths.Select(
                    path => new HashGuessCandidate(path, HashGuessStrategy.CharacterTemplate))
                    .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Path) && emittedCharacterPaths.Add(candidate.Path));
                if (remaining != int.MaxValue) candidatesToCheck = candidatesToCheck.Take(remaining);
                int checkedPaths = CheckIter(engine, candidatesToCheck, "GAME character files", cancellationToken);
                progress?.Invoke(checkedCount + checkedPaths);
                return checkedPaths;
            }

            foreach (string character in ProgressIterator(characterList, value => value, cancellationToken))
            {
                checkedCount += CheckCharacterPaths(new[]
                {
                    $"data/characters/{character}/skins/root.bin",
                    $"data/characters/{character}/skins/base/{character}.skl",
                    $"data/characters/{character}/skins/base/{character}.skn",
                    $"data/characters/{character}/skins/base/{character}_tx_cm.dds",
                    $"data/characters/{character}/tiers/root.bin",
                    $"data/characters/{character}/animations/shared.bin",
                    $"data/characters/{character}/animations/root.bin",
                    $"data/characters/{character}/{character}.bin",
                    $"data/characters/{character}/{character}.ddf",
                    $"data/characters/{character}/hud/{character}_circle.dds",
                    $"data/characters/{character}/hud/{character}_square.dds",
                    $"assets/characters/{character}/animations/shared.bin",
                    $"assets/characters/{character}/animations/root.bin",
                    $"assets/characters/{character}/hud/{character}_circle.dds",
                    $"assets/characters/{character}/hud/{character}_circle.tex",
                    $"assets/characters/{character}/hud/{character}_circle_classic.tex",
                    $"assets/characters/{character}/hud/{character}_square.dds",
                    $"assets/characters/{character}/hud/{character}_square.tex",
                    $"assets/characters/{character}/hud/{character}_square_301.tex",
                    $"assets/characters/{character}/skins/base/{character}_base_tx_cm.tex",
                    $"assets/characters/{character}/skins/base/{character}loadscreen.tex",
                    $"assets/characters/{character}/skins/base/{character}_loadscreen.tex",
                    $"characters/{character}"
                });

                const int nskins = 400;
                checkedCount += CheckCharacterPaths(
                    Enumerable.Range(0, nskins).Select(skin =>
                        $"data/characters/{character}/skins/skin{skin}.bin"));
                checkedCount += CheckCharacterPaths(
                    Enumerable.Range(1, 9).Select(skin =>
                        $"data/characters/{character}/skins/skin{skin:D2}.bin"));
                checkedCount += CheckCharacterPaths(
                    Enumerable.Range(0, nskins).Select(skin =>
                        $"data/characters/{character}/animations/skin{skin}.bin"));
                checkedCount += CheckCharacterPaths(
                    Enumerable.Range(1, 9).Select(skin =>
                        $"data/characters/{character}/animations/skin{skin:D2}.bin"));
                checkedCount += CheckCharacterPaths(
                    Enumerable.Range(0, nskins).Select(skin =>
                        $"assets/characters/{character}/hud/{character}_circle_{skin}.tex"));
                checkedCount += CheckCharacterPaths(
                    Enumerable.Range(0, nskins).Select(skin =>
                        $"assets/characters/{character}/hud/{character}_square_{skin}.tex"));
                checkedCount += CheckCharacterPaths(
                    from ability in new[] { "", "p", "q", "w", "e", "r" }
                    from number in new[] { "", "1", "2", "3", "4" }
                    from suffix in new[] { "", "_passive" }
                    select $"assets/characters/{character}/hud/icons2d/{character}_{ability}{number}{suffix}.dds");
                checkedCount += CheckCharacterPaths(
                    Enumerable.Range(0, nskins).Select(skin =>
                        $"assets/characters/{character}/skins/skin{skin:D2}/{character}loadscreen_{skin}.tex"));
                checkedCount += CheckCharacterPaths(
                    Enumerable.Range(0, nskins).Select(skin =>
                        $"assets/characters/{character}/skins/skin{skin:D2}/{character}_loadscreen_{skin}.tex"));
                checkedCount += CheckCharacterPaths(
                    Enumerable.Range(0, nskins).Select(skin =>
                        $"assets/characters/{character}/skins/skin{skin:D2}/{character}loadscreen_{skin}_le.tex"));
                checkedCount += CheckCharacterPaths(
                    Enumerable.Range(0, nskins).Select(skin =>
                        $"assets/characters/{character}/skins/skin{skin:D2}/{character}_loadscreen_{skin}_le.tex"));
                checkedCount += CheckCharacterPaths(
                    Enumerable.Range(0, nskins).Select(skin =>
                        $"assets/characters/{character}/skins/skin{skin:D2}/{character}_skin{skin:D2}_tx_cm.tex"));
                checkedCount += CheckCharacterPaths(
                    from skin in Enumerable.Range(0, nskins)
                    from tier in new[] { "starter", "signature", "premium", "base" }
                    select $"assets/characters/{character}/skins/skin{skin:D2}/ui/{character}_skin{skin:D2}_loadscreen_augments_border_{tier}.tex");
                checkedCount += CheckCharacterPaths(
                    from skin in Enumerable.Range(1, 9)
                    from tier in new[] { "starter", "signature", "premium", "base" }
                    select $"assets/characters/{character}/skins/skin{skin}/ui/{character}_skin{skin}_loadscreen_augments_border_{tier}.tex");
                if (character.StartsWith("pet", StringComparison.OrdinalIgnoreCase))
                {
                    checkedCount += CheckCharacterPaths(
                        Enumerable.Range(0, 10).Select(tier =>
                            $"data/characters/{character}/tiers/tier{tier}.bin"));

                    IReadOnlyDictionary<string, IReadOnlyList<string>> themesByPet =
                        GetDynamicPetThemeNames(cancellationToken);
                    IReadOnlyList<string> themes = themesByPet.TryGetValue(
                        character,
                        out IReadOnlyList<string> observedThemes)
                        ? observedThemes
                        : new[] { "base" };
                    checkedCount += CheckCharacterPaths(
                        themes.SelectMany(theme =>
                            new[]
                            {
                                $"data/characters/{character}/themes/{theme}/root.bin"
                            }.Concat(Enumerable.Range(1, 3).Select(tier =>
                                $"data/characters/{character}/themes/{theme}/tier{tier}.bin"))));
                }

                if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) break;
            }

            return checkedCount;
        }

        private const int MaxGlobalAnimationWords = 100_000;

        private static readonly string[] BaseAnimationActions =
        {
            "idle", "idle1", "idle2", "run", "run_fast", "walk",
            "attack1", "attack2", "attack3", "crit",
            "spell1", "spell2", "spell3", "spell4",
            "death", "recall", "dance", "taunt", "laugh", "joke",
            "channel", "spawn", "signature_move"
        };

        private IReadOnlyList<string> GetGlobalAnimationActions(CancellationToken cancellationToken = default)
        {
            return Corpus.GetOrCreate("global-animation-actions", knownPaths =>
            {
                var actionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var animRegex = new Regex(
                    @"^(?:assets|data)/characters/(?<char>[^/]+)/(?:(?:skins/(?<skin>[^/]+)|themes/(?<theme>[^/]+))/animations/|animations/)(?<file>[^/]+)\.anm$",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled);

                for (int pathIndex = 0; pathIndex < knownPaths.Count; pathIndex++)
                {
                    if ((pathIndex & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
                    string path = knownPaths[pathIndex];
                    if (!path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) ||
                        !path.Contains("/characters/", StringComparison.OrdinalIgnoreCase) ||
                        !path.Contains("/animations/", StringComparison.OrdinalIgnoreCase)) continue;

                    string basename = GetBasename(path);
                    if (basename.Length <= 4) continue;
                    string stem = basename[..^4].ToLowerInvariant();

                    int dotIdx = stem.IndexOf('.');
                    if (dotIdx > 0) stem = stem[..dotIdx];

                    if (stem.Length < 2 || stem.Length > 100) continue;
                    if (stem.All(char.IsDigit) || (stem.Length == 16 && stem.All(c => char.IsAsciiHexDigitLower(c)))) continue;

                    bool isValid = true;
                    for (int i = 0; i < stem.Length; i++)
                    {
                        char c = stem[i];
                        if (!char.IsAsciiLetterOrDigit(c) && c != '_') { isValid = false; break; }
                    }
                    if (!isValid) continue;

                    AddCount(stem);

                    Match m = animRegex.Match(path);
                    if (m.Success)
                    {
                        string charName = m.Groups["char"].Value.ToLowerInvariant();
                        string container = (m.Groups["skin"].Success ? m.Groups["skin"].Value : (m.Groups["theme"].Success ? m.Groups["theme"].Value : null))?.ToLowerInvariant();

                        if (!string.IsNullOrEmpty(container) && stem.StartsWith($"{charName}_{container}_", StringComparison.OrdinalIgnoreCase))
                        {
                            string action = stem[(charName.Length + container.Length + 2)..];
                            if (action.Length >= 2) AddCount(action);
                        }
                        else if (stem.StartsWith($"{charName}_", StringComparison.OrdinalIgnoreCase))
                        {
                            string action = stem[(charName.Length + 1)..];
                            if (action.Length >= 2) AddCount(action);
                        }
                        else if (!string.IsNullOrEmpty(container) && stem.StartsWith($"{container}_", StringComparison.OrdinalIgnoreCase))
                        {
                            string action = stem[(container.Length + 1)..];
                            if (action.Length >= 2) AddCount(action);
                        }
                    }

                    int sep = stem.IndexOf('_');
                    while (sep >= 0 && sep < stem.Length - 1)
                    {
                        string sub = stem[(sep + 1)..];
                        if (sub.Length >= 2 && sub.Length <= 100 && !sub.All(char.IsDigit))
                            AddCount(sub);
                        sep = stem.IndexOf('_', sep + 1);
                    }
                }

                foreach (string baseAction in BaseAnimationActions)
                {
                    if (!actionCounts.ContainsKey(baseAction))
                        actionCounts[baseAction] = 1;
                }

                return actionCounts
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxGlobalAnimationWords)
                    .Select(kv => kv.Key)
                    .ToList();

                void AddCount(string action)
                {
                    if (actionCounts.TryGetValue(action, out int count))
                    {
                        if (count < int.MaxValue) actionCounts[action] = count + 1;
                    }
                    else
                    {
                        actionCounts[action] = 1;
                    }
                }
            });
        }

        private IReadOnlyList<string> GetExpandedGlobalAnimationActions(CancellationToken cancellationToken = default)
        {
            return Corpus.GetOrCreate("expanded-global-animation-actions", _ =>
            {
                IReadOnlyList<string> actions = GetGlobalAnimationActions(cancellationToken);
                var expandedActions = new HashSet<string>(actions, StringComparer.OrdinalIgnoreCase);
                foreach (string action in actions)
                {
                    foreach (Range range in action.AsSpan().Split('_'))
                    {
                        ReadOnlySpan<char> word = action.AsSpan(range);
                        if (word.Length >= 2 && word.ContainsAnyExceptInRange('0', '9'))
                            expandedActions.Add(word.ToString());
                    }
                }

                return expandedActions.OrderBy(action => action, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        private IReadOnlyList<byte[]> GetExpandedGlobalAnimationActionBytes(CancellationToken cancellationToken = default)
        {
            return Corpus.GetOrCreate(
                "expanded-global-animation-action-bytes",
                _ => GetExpandedGlobalAnimationActions(cancellationToken).Select(Encoding.UTF8.GetBytes).ToList());
        }

        private IReadOnlyList<string> GetCharacterAnimationActions(string character)
        {
            return Corpus.GetOrCreate($"champion-animation-actions/{character.ToLowerInvariant()}", knownPaths =>
            {
                var actions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                string baseChar = character.StartsWith("jade_", StringComparison.OrdinalIgnoreCase) ? character[5..]
                    : (character.StartsWith("tft_", StringComparison.OrdinalIgnoreCase) || (character.StartsWith("tft", StringComparison.OrdinalIgnoreCase) && character.Length > 5 && character.Contains('_'))) ? character[(character.IndexOf('_') + 1)..]
                    : (character.StartsWith("cherry_", StringComparison.OrdinalIgnoreCase) || character.StartsWith("strawberry_", StringComparison.OrdinalIgnoreCase) || character.StartsWith("crepe_", StringComparison.OrdinalIgnoreCase) || character.StartsWith("ruby_", StringComparison.OrdinalIgnoreCase)) ? character[(character.IndexOf('_') + 1)..]
                    : character.Equals("oriannaball", StringComparison.OrdinalIgnoreCase) ? "orianna"
                    : character.Equals("tibbers", StringComparison.OrdinalIgnoreCase) ? "annie"
                    : character.Equals("heimergarrison", StringComparison.OrdinalIgnoreCase) ? "heimerdinger"
                    : character.Equals("quinnvalor", StringComparison.OrdinalIgnoreCase) ? "quinn"
                    : character.Equals("yorickghoul", StringComparison.OrdinalIgnoreCase) ? "yorick"
                    : character.Equals("kalistaspawn", StringComparison.OrdinalIgnoreCase) ? "kalista"
                    : character.Equals("malzaharvoidling", StringComparison.OrdinalIgnoreCase) ? "malzahar"
                    : null;

                string charPrefix = $"assets/characters/{character}/";
                string dataCharPrefix = $"data/characters/{character}/";
                string basePrefix = baseChar != null ? $"assets/characters/{baseChar}/" : null;
                string dataBasePrefix = baseChar != null ? $"data/characters/{baseChar}/" : null;

                for (int i = 0; i < knownPaths.Count; i++)
                {
                    string path = knownPaths[i];
                    if (!path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase)) continue;

                    string rel = null;
                    if (path.StartsWith(charPrefix, StringComparison.OrdinalIgnoreCase))
                        rel = path[charPrefix.Length..];
                    else if (path.StartsWith(dataCharPrefix, StringComparison.OrdinalIgnoreCase))
                        rel = path[dataCharPrefix.Length..];
                    else if (basePrefix != null && path.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
                        rel = path[basePrefix.Length..];
                    else if (dataBasePrefix != null && path.StartsWith(dataBasePrefix, StringComparison.OrdinalIgnoreCase))
                        rel = path[dataBasePrefix.Length..];

                    if (string.IsNullOrEmpty(rel)) continue;

                    string basename = GetBasename(path);
                    string stem = basename.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) ? basename[..^4] : basename;
                    if (stem.Length == 0 || stem.Length > 50) continue;

                    actions.Add(stem);

                    if (stem.StartsWith(character + "_", StringComparison.OrdinalIgnoreCase))
                    {
                        string sub = stem[(character.Length + 1)..];
                        if (sub.Length > 0) actions.Add(sub);
                    }
                    if (baseChar != null && stem.StartsWith(baseChar + "_", StringComparison.OrdinalIgnoreCase))
                    {
                        string sub = stem[(baseChar.Length + 1)..];
                        if (sub.Length > 0) actions.Add(sub);
                    }

                    Match containerMatch = Regex.Match(
                        rel,
                        @"^(?:skins|themes)/(?<container>[^/]+)/animations/",
                        RegexOptions.IgnoreCase);
                    if (containerMatch.Success)
                    {
                        string container = containerMatch.Groups["container"].Value;
                        AddAnimationActionAfterPrefix($"{character}_{container}_");
                        if (baseChar != null) AddAnimationActionAfterPrefix($"{baseChar}_{container}_");
                    }

                    Match skinToken = Regex.Match(stem, @"(?:^|_)skin\d+_(?<action>.+)$", RegexOptions.IgnoreCase);
                    if (skinToken.Success) actions.Add(skinToken.Groups["action"].Value);

                    void AddAnimationActionAfterPrefix(string prefix)
                    {
                        if (!stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
                        string action = stem[prefix.Length..];
                        if (action.Length > 0) actions.Add(action);
                    }
                }

                if (actions.Count == 0)
                {
                    foreach (string globalAction in GetGlobalAnimationActions())
                        actions.Add(globalAction);
                }

                return actions.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        internal IEnumerable<HashGuessCandidate> GenerateCharacterSubstitutionCandidates(int candidateBudget)
        {
            var characterCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var formatCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var regex = new Regex(@"^(?:assets|data)/characters/([^/]+)/", RegexOptions.IgnoreCase);
            foreach (string path in KnownPaths)
            {
                Match match = regex.Match(path);
                if (!match.Success) continue;
                string character = match.Groups[1].Value;
                characterCounts.TryGetValue(character, out int characterSupport);
                characterCounts[character] = characterSupport + 1;
                string format = path.Replace(character, "{character}", StringComparison.Ordinal);
                formatCounts.TryGetValue(format, out int formatSupport);
                formatCounts[format] = formatSupport + 1;
            }
            var characters = characterCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key).ToList();
            int generated = 0;
            foreach (string format in formatCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key))
            foreach (string character in characters)
            {
                yield return new HashGuessCandidate(format.Replace("{character}", character, StringComparison.Ordinal), HashGuessStrategy.CharacterSubstitution);
                if (CountCandidate(ref generated, candidateBudget)) yield break;
            }
        }

        internal IEnumerable<HashGuessCandidate> GenerateSuffixCandidates(int candidateBudget)
        {
            var suffixCounts = new Dictionary<string, int>(StringComparer.Ordinal) { [string.Empty] = int.MaxValue };
            var formatCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var regex = new Regex(@"^(.*?)(\.[^.]+)?(\.[^.]+)$");
            foreach (string path in KnownPaths)
            {
                Match match = regex.Match(path);
                if (!match.Success) continue;
                string suffix = match.Groups[2].Value;
                if (suffix.Length > 0)
                {
                    suffixCounts.TryGetValue(suffix, out int support);
                    suffixCounts[suffix] = support + 1;
                }
                string format = match.Groups[1].Value + "{suffix}" + match.Groups[3].Value;
                formatCounts.TryGetValue(format, out int formatSupport);
                formatCounts[format] = formatSupport + 1;
            }
            int generated = 0;
            var suffixes = suffixCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key).ToList();
            foreach (string format in formatCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key))
            foreach (string suffix in suffixes)
            {
                yield return new HashGuessCandidate(format.Replace("{suffix}", suffix, StringComparison.Ordinal), HashGuessStrategy.SuffixVariant);
                if (CountCandidate(ref generated, candidateBudget)) yield break;
            }
        }

        internal IEnumerable<HashGuessCandidate> GenerateSkinNumberCandidates(int candidateBudget)
        {
            var directoryRegex = new Regex(@"/characters/([^/]+)/skins/(base|skin\d+)/", RegexOptions.IgnoreCase);
            var skinRegex = new Regex(@"(?:base|skin\d+)", RegexOptions.IgnoreCase);
            var characters = new Dictionary<string, (HashSet<string> Skins, HashSet<(string Format, int Count)> Formats)>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in KnownPaths)
            {
                Match directory = directoryRegex.Match(path);
                if (!directory.Success || directory.Groups[1].Value.Equals("sightward", StringComparison.OrdinalIgnoreCase)) continue;
                string character = directory.Groups[1].Value;
                if (!characters.TryGetValue(character, out var data))
                {
                    data = (new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<(string Format, int Count)>());
                    characters[character] = data;
                }
                data.Skins.Add(directory.Groups[2].Value.ToLowerInvariant());
                MatchCollection matches = skinRegex.Matches(path);
                data.Formats.Add((skinRegex.Replace(path, "{skin}"), matches.Count));
            }
            int generated = 0;
            foreach (var character in characters.OrderBy(value => value.Key, StringComparer.Ordinal))
            foreach ((string format, int count) in character.Value.Formats
                .OrderBy(value => value.Format, StringComparer.Ordinal)
                .ThenBy(value => value.Count))
            {
                List<string> skins = character.Value.Skins.OrderBy(value => value, StringComparer.Ordinal).ToList();
                if (count > skins.Count) continue;
                foreach (IEnumerable<string> combination in GetCombinations(skins, count))
                {
                    string candidate = format;
                    foreach (string skin in combination)
                    {
                        int marker = candidate.IndexOf("{skin}", StringComparison.Ordinal);
                        candidate = candidate[..marker] + skin + candidate[(marker + 6)..];
                    }
                    yield return new HashGuessCandidate(candidate, HashGuessStrategy.SkinNumberVariant);
                    if (CountCandidate(ref generated, candidateBudget)) yield break;
                }
            }
        }

        internal int GuessShaderVariants(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            int candidateBudget = int.MaxValue,
            Action<int> progress = null,
            string rootDirectory = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            if (candidateBudget < 0) throw new ArgumentOutOfRangeException(nameof(candidateBudget));
            if (candidateBudget == 0) return 0;

            var shaderPattern = new Regex(@".*\.[pv]s(?:_[23]_0|(?=$|[.-]))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var shaderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in KnownPaths)
            {
                Match match = shaderPattern.Match(path);
                if (match.Success) shaderPaths.Add(match.Value);
            }
            AddExecutableShaderReferences(shaderPaths, rootDirectory, cancellationToken);

            int checkedCount = 0;
            foreach (string path in ProgressIterator(
                         shaderPaths.OrderBy(value => value, StringComparer.Ordinal).ToList(),
                         value => value,
                         cancellationToken))
            {
                int remaining = candidateBudget == int.MaxValue ? int.MaxValue : candidateBudget - checkedCount;
                if (remaining <= 0 || engine.RemainingUnknownCount == 0) break;

                IEnumerable<HashGuessCandidate> variants = ShaderVariants.Select(variant =>
                    new HashGuessCandidate(path + variant, HashGuessStrategy.ShaderVariant));
                if (remaining != int.MaxValue) variants = variants.Take(remaining);
                int checkedVariants = CheckIter(engine, variants, "GAME shader variants", cancellationToken);
                checkedCount += checkedVariants;
                progress?.Invoke(checkedCount);

                remaining = candidateBudget == int.MaxValue ? int.MaxValue : candidateBudget - checkedCount;
                if (remaining <= 0 || engine.RemainingUnknownCount == 0) break;
                IEnumerable<int> shaderIndices = Enumerable.Range(0, 32)
                    .Concat(Enumerable.Range(1, 200).Select(index => index * 100));
                IEnumerable<HashGuessCandidate> numberedVariants =
                    ShaderVariants.SelectMany(variant => shaderIndices.Select(index =>
                        new HashGuessCandidate(
                            $"{path}{variant}_{index}",
                            HashGuessStrategy.ShaderVariant)));
                if (remaining != int.MaxValue) numberedVariants = numberedVariants.Take(remaining);
                checkedCount += CheckIter(engine, numberedVariants, "GAME shader variants", cancellationToken);
                progress?.Invoke(checkedCount);
            }

            return checkedCount;
        }

        private void AddExecutableShaderReferences(
            ISet<string> shaderPaths,
            string rootDirectory,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory)) return;
            string gameDirectory = Directory.Exists(Path.Combine(rootDirectory, "Game"))
                ? Path.Combine(rootDirectory, "Game")
                : rootDirectory;
            string executablePath = Path.Combine(gameDirectory, "League of Legends.exe");
            if (!File.Exists(executablePath)) return;

            try
            {
                using var stream = new FileStream(
                    executablePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 64 * 1024,
                    FileOptions.SequentialScan);
                var token = new StringBuilder();
                var buffer = new byte[64 * 1024];
                bool tokenOverflowed = false;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int index = 0; index < read; index++)
                    {
                        byte value = buffer[index];
                        if (IsShaderPathByte(value))
                        {
                            if (!tokenOverflowed)
                            {
                                token.Append((char)value);
                                if (token.Length > 512)
                                {
                                    token.Clear();
                                    tokenOverflowed = true;
                                }
                            }
                            continue;
                        }

                        if (!tokenOverflowed) AddReference(token);
                        token.Clear();
                        tokenOverflowed = false;
                    }
                }
                if (!tokenOverflowed) AddReference(token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logService?.LogDebug($"GAME shader executable scan skipped '{executablePath}': {exception.Message}");
            }

            void AddReference(StringBuilder value)
            {
                if (!IsShaderReference(value)) return;
                string path = NormalizePath(value.ToString());
                if (!path.Contains('/') || GetBasename(path).Length <= 4) return;
                shaderPaths.Add(path.StartsWith("assets/shaders/", StringComparison.OrdinalIgnoreCase)
                    ? path
                    : $"assets/shaders/hlsl/{path}");
            }
        }



        private static bool IsShaderPathByte(byte value) =>
            value is >= (byte)'0' and <= (byte)'9' or
                >= (byte)'A' and <= (byte)'Z' or
                >= (byte)'a' and <= (byte)'z' or
                (byte)'_' or (byte)'.' or (byte)'/' or (byte)'-';

        private static bool IsShaderReference(StringBuilder value)
        {
            foreach (string extension in ShaderExtensions)
                if (EndsWith(value, extension)) return true;
            return false;
        }

        private static bool EndsWith(StringBuilder value, string suffix)
        {
            if (value.Length < suffix.Length) return false;
            int offset = value.Length - suffix.Length;
            for (int index = 0; index < suffix.Length; index++)
                if (char.ToLowerInvariant(value[offset + index]) != suffix[index]) return false;
            return true;
        }

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

        internal async Task<int> RunExtendedAttacksAsync(
            HashGuessEngine engine,
            string rootDirectory,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            IReadOnlySet<string> selectedSubMethods = null)
        {
            int checkedCandidates = 0;
            bool ShouldRun(string subId) => selectedSubMethods == null || selectedSubMethods.Contains(subId);

            if (engine.RemainingUnknownCount > 0 && ShouldRun("game-ext-skingroups"))
                checkedCandidates += await GuessSkinGroupsBin(engine, cancellationToken, progress, checkedCandidates);
            if (engine.RemainingUnknownCount > 0 && ShouldRun("game-ext-chromas"))
                checkedCandidates += await GuessSkinGroupsBinUsingChromas(engine, rootDirectory, cancellationToken, progress, checkedCandidates);
            if (engine.RemainingUnknownCount > 0 && ShouldRun("game-ext-suffixes"))
                checkedCandidates += CheckCandidates(engine, SubstituteSuffixes(), "GAME suffix substitution", cancellationToken, progress, checkedCandidates);
            if (engine.RemainingUnknownCount > 0 && ShouldRun("game-ext-skinnumbers"))
                checkedCandidates += CheckCandidates(
                    engine,
                    SubstituteSkinNumbers(),
                    "GAME skin number combinations",
                    cancellationToken,
                    progress,
                    checkedCandidates);
            if (engine.RemainingUnknownCount > 0 && ShouldRun("game-ext-characters"))
                checkedCandidates += CheckCandidates(
                    engine,
                    SubstituteCharacter(),
                    "GAME character substitution",
                    cancellationToken,
                    progress,
                    checkedCandidates);
            if (engine.RemainingUnknownCount > 0 && ShouldRun("game-ext-wordaddition"))
            {
                int progressOffset = checkedCandidates;
                checkedCandidates += AddBasenameWord(
                    engine,
                    cancellationToken,
                    candidateBudget: int.MaxValue);
                progress?.Report(engine.CreateProgress("GAME basename word addition", checkedCandidates));
            }

            GetCharacters();
            return checkedCandidates;
        }

        private int CheckCandidates(
            HashGuessEngine engine,
            IEnumerable<HashGuessCandidate> candidates,
            string source,
            CancellationToken cancellationToken,
            IProgress<HashGuessProgress> progress = null,
            int progressOffset = 0)
        {
            progress?.Report(engine.CreateProgress(source, progressOffset));
            return CheckIter(
                engine,
                candidates,
                source,
                cancellationToken,
                count => progress?.Report(engine.CreateProgress(source, progressOffset + count)),
                5000);
        }

        internal async Task<int> GuessChromaGroupsAsync(
            HashGuessEngine engine,
            string rootDirectory,
            CancellationToken cancellationToken,
            IProgress<HashGuessProgress> progress = null,
            int progressOffset = 0)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory)) return 0;
            const string stageName = "GAME Extended: chroma group bins";
            progress?.Report(engine.CreateProgress(stageName, progressOffset));

            string json = await Task.Run(() => LoadLocalSkinsJson(rootDirectory, cancellationToken), cancellationToken);
            if (json == null) return 0;
            try
            {
                using var document = JsonDocument.Parse(json);
                var groups = new Dictionary<string, List<List<int>>>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty skin in document.RootElement.EnumerateObject())
                {
                    JsonElement value = skin.Value;
                    if (!value.TryGetProperty("loadScreenPath", out JsonElement loadScreen)) continue;
                    Match champion = Regex.Match(loadScreen.GetString() ?? string.Empty, @"/assets/characters/([^/]+)/skins/", RegexOptions.IgnoreCase);
                    if (!champion.Success) continue;
                    long skinId;
                    if (!long.TryParse(skin.Name, out skinId) &&
                        (!value.TryGetProperty("id", out JsonElement id) || !id.TryGetInt64(out skinId))) continue;
                    var ids = new HashSet<int> { (int)(skinId % 1000) };
                    if (value.TryGetProperty("chromas", out JsonElement chromas) && chromas.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement chroma in chromas.EnumerateArray())
                            if (chroma.TryGetProperty("id", out JsonElement chromaId) && chromaId.TryGetInt64(out long chromaValue))
                                ids.Add((int)(chromaValue % 1000));
                    string character = champion.Groups[1].Value.ToLowerInvariant();
                    groups.TryAdd(character, new List<List<int>>());
                    groups[character].Add(ids.OrderBy(id => id).ToList());
                }
                var knownCharacters = GetCharacters();
                if (knownCharacters.Count > 0)
                {
                    var filtered = groups.Where(pair => knownCharacters.Contains(pair.Key))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                    if (filtered.Count > 0) groups = filtered;
                }
                int generated = 0;
                foreach (var pair in groups)
                {
                    var tokens = pair.Value
                        .Select(group => group.Select(id => "_skins_skin" + id).ToList())
                        .GroupBy(group => string.Join('\0', group), StringComparer.Ordinal)
                        .Select(group => group.First())
                        .Append(new List<string> { "_skins_root" }).ToList();
                    for (int length = 1; length <= tokens.Count; length++)
                    {
                        foreach (var combination in GetCombinations(tokens, length))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string suffix = string.Concat(combination.SelectMany(value => value).OrderBy(value => value, StringComparer.Ordinal));
                            Check(engine, "data/" + pair.Key + suffix + ".bin", HashGuessStrategy.ChromaGroupVariant, "Local skins.json chroma groups");
                            if (generated < int.MaxValue) generated++;
                            if ((generated % 5000) == 0)
                            {
                                progress?.Report(engine.CreateProgress(stageName, progressOffset + generated));
                            }
                            if (engine.RemainingUnknownCount == 0) return generated;
                        }
                    }
                }
                progress?.Report(engine.CreateProgress(stageName, progressOffset + generated));
                return generated;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logService.LogWarning("Hash Lab skipped skins.json chroma groups: " + exception.Message);
                return 0;
            }
        }

        internal Task<int> GuessSkinGroupsBinUsingChromas(
            HashGuessEngine engine,
            string rootDirectory,
            CancellationToken cancellationToken,
            IProgress<HashGuessProgress> progress = null,
            int progressOffset = 0) =>
            GuessChromaGroupsAsync(engine, rootDirectory, cancellationToken, progress, progressOffset);

        private string LoadLocalSkinsJson(string rootDirectory, CancellationToken cancellationToken)
        {
            ulong skinsJsonHash = XxHash64Ext.Hash(RiotCatalogDefinitions.SkinsJsonPath);
            IEnumerable<string> wadPaths = Directory.EnumerateFiles(rootDirectory, "*.wad*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => path.Contains("game-data", StringComparison.OrdinalIgnoreCase))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase);

            foreach (string wadPath in wadPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var wad = new WadFile(wadPath);
                    if (!wad.Chunks.TryGetValue(skinsJsonHash, out WadChunk chunk)) continue;
                    using var dataOwner = wad.LoadChunkDecompressed(chunk);
                    ArraySegment<byte> data = dataOwner.DangerousGetArray();
                    if (!TryDecodeWadText(data, out string json)) continue;
                    using (JsonDocument document = JsonDocument.Parse(json))
                    {
                        if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                    }
                    return json;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logService?.LogDebug($"Hash Lab could not read local skins.json from '{wadPath}': {exception.Message}");
                }
            }

            return null;
        }

        internal Task<int> GuessSkinGroupsBinLocalAsync(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            IProgress<HashGuessProgress> progress = null,
            int progressOffset = 0)
        {
            return Task.Run(() =>
            {
                var characters = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
                var regex = new Regex(@"^assets/characters/([^/]+)/skins/skin(\d+)/", RegexOptions.IgnoreCase);
                foreach (string path in KnownPaths)
                {
                    Match match = regex.Match(path);
                    if (!match.Success || match.Groups[1].Value.Equals("sightward", StringComparison.OrdinalIgnoreCase)) continue;
                    string character = match.Groups[1].Value.ToLowerInvariant();
                    if (!characters.TryGetValue(character, out HashSet<int> skins))
                    {
                        skins = new HashSet<int> { 0 };
                        characters[character] = skins;
                    }
                    skins.Add(int.Parse(match.Groups[2].Value));
                }
                int generated = 0;
                const string stageName = "GAME Extended: local skin groups";
                progress?.Report(engine.CreateProgress(stageName, progressOffset));

                foreach (var pair in characters.OrderBy(pair => pair.Value.Count))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var skins = pair.Value.Select(value => $"_skins_skin{value}").OrderBy(value => value, StringComparer.Ordinal).ToList();
                    for (int length = 1; length <= skins.Count; length++)
                    {
                        foreach (var combination in GetCombinations(skins, length))
                        {
                            Check(engine, $"data/{pair.Key}{string.Concat(combination)}.bin", HashGuessStrategy.ChromaGroupVariant, "Local skin groups");
                            if (generated < int.MaxValue) generated++;
                            if ((generated % 5000) == 0)
                            {
                                progress?.Report(engine.CreateProgress(stageName, progressOffset + generated));
                            }
                            if (engine.RemainingUnknownCount == 0) return generated;
                        }
                    }
                }
                progress?.Report(engine.CreateProgress(stageName, progressOffset + generated));
                return generated;
            }, cancellationToken);
        }

        internal Task<int> GuessSkinGroupsBin(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            IProgress<HashGuessProgress> progress = null,
            int progressOffset = 0) =>
            GuessSkinGroupsBinLocalAsync(engine, cancellationToken, progress, progressOffset);

        internal override void GrepWad(
            HashGuessEngine engine,
            ArraySegment<byte> data,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Count == 0) return;

            void CheckGame(string path, HashGuessStrategy strategy = HashGuessStrategy.BinLengthPath)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(path))
                    Check(engine, path, strategy, sourceWadPath, sourceChunkHash);
            }

            void CheckGameIter(IEnumerable<string> paths, HashGuessStrategy strategy = HashGuessStrategy.BinLengthPath) =>
                CheckIter(engine, paths, strategy, sourceWadPath, cancellationToken, sourceChunkHash);

            void CheckGameCandidates(IEnumerable<HashGuessCandidate> candidates) =>
                CheckIter(engine, candidates, sourceWadPath, cancellationToken, sourceChunkHash: sourceChunkHash);

            if (sourcePath.Equals("data/all_lua_files.manifest", StringComparison.OrdinalIgnoreCase))
            {
                CheckGameCandidates(ExtractLuaManifestCandidates(data, cancellationToken));
                return;
            }

            if (ImageAutoAtlas.IsAtlas(data.AsSpan()))
            {
                GuessImageAutoAtlasPaths(engine, data, sourcePath, sourceWadPath, sourceChunkHash, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (engine.RemainingUnknownCount == 0) return;

            string extension = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
            if (extension is "dds" or "jpg" or "png" or "tga" or "ttf" or "otf" or "ogg" or "webm" or
                "anm" or "skl" or "skn" or "scb" or "sco" or "troybin" or "bnk" or "wpk" or "tex")
            {
                return; // don't grep filetypes known to not contain full paths
            }

            bool isBin = extension is "bin" or "inibin";
            if (isBin)
            {
                string text = Encoding.Latin1.GetString(data.Array, data.Offset, data.Count);
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match match in Regex.Matches(text, @"(?:ASSETS|DATA|Characters|Shaders|Maps|Gameplay|ClientStates|Patching|Loadouts)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int offset = match.Index;
                    if (offset < 2) continue;
                    int length = ByteAt(data, offset - 2) | (ByteAt(data, offset - 1) << 8);
                    if (length <= 0 || offset + length > data.Count) continue;
                    string path = text.Substring(offset, length);
                    if (!IsAscii(path)) continue;
                    path = NormalizePath(path);
                    if (!seenPaths.Add(path)) continue;

                    if (path.StartsWith("characters/", StringComparison.OrdinalIgnoreCase))
                    {
                        CheckGame(path);
                        CheckGame($"assets/{path}");
                        CheckGame($"data/{path}");
                    }
                    else if (path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                    {
                        string prefix = path[..^4];
                        CheckGame(path);
                        CheckGame(prefix + ".luabin", HashGuessStrategy.LuaVariant);
                        CheckGame(prefix + ".luabin64", HashGuessStrategy.LuaVariant);
                        CheckGame(prefix + ".preload", HashGuessStrategy.LuaVariant);
                    }
                    else if (path.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith("assets/shaders/", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith("data/shaders/", StringComparison.OrdinalIgnoreCase))
                    {
                        var candidateBases = path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ||
                                             path.StartsWith("data/", StringComparison.OrdinalIgnoreCase)
                            ? new[] { path }
                            : new[] { $"assets/{path}", $"data/{path}", $"assets/shaders/generated/{path}" };
                        foreach (string candidateBase in candidateBases)
                        {
                            CheckGameIter(
                                ShaderExtensions.Select(extensionName =>
                                    $"{candidateBase}{extensionName}"));
                            if (engine.RemainingUnknownCount == 0) break;

                            CheckGameIter(
                                ShaderExtensions.SelectMany(extensionName =>
                                    ShaderVariants.Select(variant =>
                                        $"{candidateBase}{extensionName}{variant}")));
                            if (engine.RemainingUnknownCount == 0) break;

                            CheckGameIter(
                                ShaderExtensions.SelectMany(extensionName =>
                                    ShaderVariants.SelectMany(variant =>
                                        Enumerable.Range(0, 32).Select(index =>
                                            $"{candidateBase}{extensionName}{variant}_{index}"))));
                            if (engine.RemainingUnknownCount == 0) break;
                        }
                    }
                    else if (path.StartsWith("maps/mapgeometry/", StringComparison.OrdinalIgnoreCase))
                    {
                        CheckGame($"data/{path}.mapgeo");
                        CheckGame($"data/{path}.materials.bin");
                    }
                    else if (path.StartsWith("clientstates/", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith("patching/", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith("loadouts/", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
                    {
                        CheckGame(path);
                        int separator = path.LastIndexOf('/');
                        if (separator > 0)
                        {
                            string parent = path[..separator];
                            CheckGame(parent);
                            int parentSeparator = parent.LastIndexOf('/');
                            if (parentSeparator > 0) CheckGame(parent[..parentSeparator]);
                        }
                    }
                    else
                    {
                        CheckGame(path);
                        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                            CheckGame(path[..^4] + ".dds", HashGuessStrategy.ImageExtensionVariant);
                    }
                }

                GuessDottedBinPaths(engine, data, sourceWadPath, sourceChunkHash, cancellationToken);
                if (data.Array is not null && data.Count >= sizeof(int) && FileTypeDetector.IsPropertyBin(data.AsSpan()))
                {
                    BinTree cachedTree = null;
                    BinTree GetCachedBinTree()
                    {
                        if (cachedTree == null)
                        {
                            using var stream = new MemoryStream(data.Array, data.Offset, data.Count, writable: false);
                            cachedTree = new BinTree(stream);
                        }
                        return cachedTree;
                    }

                    GuessAnimationBinPaths(engine, data, sourcePath, sourceWadPath, sourceChunkHash, cancellationToken, GetCachedBinTree);
                    GuessRegaliaBinChunkLinks(engine, data, sourcePath, sourceWadPath, sourceChunkHash, cancellationToken, GetCachedBinTree);
                    GuessSkinCharacterBinChunkLinks(engine, data, sourcePath, sourceWadPath, sourceChunkHash, cancellationToken, GetCachedBinTree);
                }

                GuessChampionSpecialBins(engine, sourceWadPath, cancellationToken);
                return;
            }

            if (extension == "preload")
            {
                string text = Encoding.Latin1.GetString(data.Array, data.Offset, data.Count);
                string directory = PathUtils.NormalizeSeparators(Path.GetDirectoryName(sourcePath));

                foreach (Match match in Regex.Matches(text, @"Name=""([^""]+)"""))
                {
                    if (!IsAscii(match.Groups[1].Value)) continue;
                    string path = NormalizePath(match.Groups[1].Value);
                    if (path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                    {
                        string prefix = path[..^4];
                        CheckGame(path, HashGuessStrategy.PreloadReference);
                        CheckGame(prefix + ".luabin", HashGuessStrategy.LuaVariant);
                        CheckGame(prefix + ".luabin64", HashGuessStrategy.LuaVariant);
                    }
                    else if (path.EndsWith(".troy", StringComparison.OrdinalIgnoreCase))
                    {
                        CheckGame($"data/shared/particles/{path[..^5]}.troybin", HashGuessStrategy.PreloadReference);
                    }
                    else if (path.StartsWith("shaders", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string ext in ShaderExtensions)
                        {
                            CheckGame($"assets/shaders/generated/{path}{ext}", HashGuessStrategy.PreloadReference);
                            foreach (string variant in ShaderVariants)
                            {
                                CheckGame($"assets/shaders/generated/{path}{ext}{variant}", HashGuessStrategy.PreloadReference);
                            }
                        }
                    }
                    else if (!string.IsNullOrEmpty(directory))
                    {
                        CheckGame(directory + "/" + path + ".preload", HashGuessStrategy.PreloadReference);
                    }
                }
                return;
            }

            if (extension is "hls" or "ps_2_0" or "ps_3_0" or "vs_2_0" or "vs_3_0")
            {
                string text = Encoding.Latin1.GetString(data.Array, data.Offset, data.Count);
                string directory = PathUtils.NormalizeSeparators(Path.GetDirectoryName(sourcePath));
                if (string.IsNullOrEmpty(directory)) return;
                foreach (Match match in Regex.Matches(text, @"#include ""([^""]+)"""))
                {
                    if (!IsAscii(match.Groups[1].Value)) continue;
                    CheckGame(
                        NormalizePath(PathUtils.NormalizeVirtualPath($"{directory}/{match.Groups[1].Value}")),
                        HashGuessStrategy.ShaderInclude);
                }
                return;
            }

            if (extension == "atlas")
            {
                string text = Encoding.Latin1.GetString(data.Array, data.Offset, data.Count);
                string directory = PathUtils.NormalizeSeparators(Path.GetDirectoryName(sourcePath));
                if (string.IsNullOrEmpty(directory)) return;
                foreach (string line in text.Split('\n'))
                {
                    if (!IsAscii(line)) continue;
                    CheckGame(
                        NormalizePath(Path.Combine(directory, line.Trim())),
                        HashGuessStrategy.AtlasReference);
                }
                return;
            }

            CheckGameCandidates(GrepFile(data, cancellationToken));
        }

        private void GuessDottedBinPaths(
            HashGuessEngine engine,
            ArraySegment<byte> data,
            string sourceWadPath,
            ulong sourceChunkHash,
            CancellationToken cancellationToken)
        {
            if (data.Array is null || data.Count < 16) return;

            try
            {
                using var stream = new MemoryStream(data.Array, data.Offset, data.Count, writable: false);
                using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
                string magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
                if (magic == "PTCH")
                {
                    if (reader.ReadUInt32() != 1) return;
                    reader.ReadUInt32();
                    magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
                }
                if (magic != "PROP") return;

                uint version = reader.ReadUInt32();
                if (version is not (1 or 2 or 3)) return;
                if (version >= 2)
                {
                    uint dependencyCount = reader.ReadUInt32();
                    if (dependencyCount > data.Count / sizeof(ushort)) return;
                    for (uint index = 0; index < dependencyCount; index++)
                    {
                        ushort length = reader.ReadUInt16();
                        if (length > stream.Length - stream.Position) return;
                        stream.Position += length;
                    }
                }

                uint objectCount = reader.ReadUInt32();
                if (objectCount > data.Count / 10 || (long)objectCount * sizeof(uint) > stream.Length - stream.Position)
                    return;
                stream.Position += (long)objectCount * sizeof(uint);

                string[] dottedBinTargetPrefixes =
                {
                    "loadouts/companions",
                    "loadouts/summoneremotesvfx",
                    "loadouts/summoneremotes",
                    "loadouts/tftdamageskins",
                    "loadouts/tftzoomskins"
                };

                for (uint index = 0; index < objectCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long objectOffset = stream.Position;
                    uint objectSize = reader.ReadUInt32();
                    uint objectPathHash = reader.ReadUInt32();
                    long nextObjectOffset = objectOffset + sizeof(uint) + objectSize;
                    if (objectSize < 6 || nextObjectOffset < stream.Position || nextObjectOffset > stream.Length)
                        return;
                    foreach (string prefix in dottedBinTargetPrefixes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Check(
                            engine,
                            $"{prefix}.{objectPathHash:x8}.bin",
                            HashGuessStrategy.BinEntry,
                            sourceWadPath,
                            sourceChunkHash);
                        if (engine.RemainingUnknownCount == 0) return;
                    }
                    stream.Position = nextObjectOffset;
                }
            }
            catch (Exception exception) when (exception is EndOfStreamException or IOException or ArgumentException)
            {
                _logService?.LogDebug($"GAME dotted BIN object scan skipped malformed data: {exception.Message}");
            }
        }

        private void GuessAnimationBinPaths(
            HashGuessEngine engine,
            ArraySegment<byte> data,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            CancellationToken cancellationToken,
            Func<BinTree> binTreeFactory = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Match context = AnimationBinPathRegex.Match(PathUtils.NormalizePath(sourcePath));
            if (!context.Success || data.Array is null || data.Count == 0) return;

            string character = context.Groups["character"].Value.ToLowerInvariant();
            string sourceSkin = context.Groups["skin"].Value.ToLowerInvariant();

            var links = new HashSet<AnimationFileLink>();
            try
            {
                BinTree tree = binTreeFactory != null
                    ? binTreeFactory()
                    : new BinTree(new MemoryStream(data.Array, data.Offset, data.Count, writable: false));
                if (tree == null) return;
                cancellationToken.ThrowIfCancellationRequested();
                foreach (AnimationFileLink link in EnumerateAnimationFileLinks(tree))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    links.Add(link);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logService?.LogDebug($"GAME animation BIN link scan skipped '{sourcePath}': {exception.Message}");
                return;
            }

            foreach (AnimationFileLink link in links)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(link.Path))
                    Check(engine, link.Path, HashGuessStrategy.AnimationBinLink, sourceWadPath, sourceChunkHash);
            }

            var unresolved = links
                .Where(link => link.PathHash != 0 && engine.UnknownHashes.Contains(link.PathHash))
                .ToList();
            cancellationToken.ThrowIfCancellationRequested();
            if (unresolved.Count == 0) return;

            CheckIter(
                engine,
                EnumerateAnimationPaths(
                    character,
                    sourceSkin,
                    IsThemeAnimationContext(character, sourceSkin),
                    unresolved,
                    engine.UnknownHashes,
                    cancellationToken),
                HashGuessStrategy.AnimationBinLink,
                sourceWadPath,
                cancellationToken,
                sourceChunkHash);
            var remainingHashes = unresolved
                .Where(link => engine.UnknownHashes.Contains(link.PathHash))
                .Select(link => link.PathHash)
                .ToHashSet();
            if (remainingHashes.Count == 0 || engine.RemainingUnknownCount == 0) return;

            var characters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddAnimationCharacter(character);
            if (character.StartsWith("jade_", StringComparison.OrdinalIgnoreCase))
                AddAnimationCharacter(character[5..]);
            else if (!character.StartsWith("pet", StringComparison.OrdinalIgnoreCase))
                AddAnimationCharacter($"jade_{character}");

            var emittedCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int checkedCandidates = 0;

            var namedActions = EnumerateAnimationNameCandidates(
                character,
                unresolved,
                remainingHashes,
                cancellationToken)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (namedActions.Count > 0)
            {
                foreach (string animationCharacter in characters)
                foreach (string skin in EnumerateAnimationSkins(animationCharacter))
                {
                    if (remainingHashes.Count == 0 || engine.RemainingUnknownCount == 0) return;
                    bool isTheme = IsThemeAnimationContext(animationCharacter, skin);
                    foreach (string action in namedActions)
                    foreach (string candidate in EnumerateAnimationCandidates(
                                 animationCharacter,
                                 skin,
                                 action,
                                 isTheme))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!emittedCandidates.Add(candidate)) continue;
                        ulong hash = XxHash64Ext.Hash(candidate);
                        if (remainingHashes.Remove(hash) && engine.UnknownHashes.Contains(hash))
                            Check(engine, candidate, HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                    }
                }
            }

            foreach (string animationCharacter in characters)
            foreach (string skin in EnumerateAnimationSkins(animationCharacter))
            {
                if (remainingHashes.Count == 0 || engine.RemainingUnknownCount == 0) return;
                bool isTheme = IsThemeAnimationContext(animationCharacter, skin);
                foreach (string action in EnumerateActionsForSkin(animationCharacter, skin))
                foreach (string candidate in EnumerateAnimationCandidates(
                             animationCharacter,
                             skin,
                             action,
                             isTheme))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!emittedCandidates.Add(candidate)) continue;
                    if (checkedCandidates++ >= AnimationBinFallbackCandidateBudget ||
                        remainingHashes.Count == 0 || engine.RemainingUnknownCount == 0) return;

                    ulong hash = XxHash64Ext.Hash(candidate);
                    if (remainingHashes.Remove(hash) && engine.UnknownHashes.Contains(hash))
                        Check(engine, candidate, HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                }
            }

            void AddAnimationCharacter(string value)
            {
                if (!string.IsNullOrWhiteSpace(value)) characters.Add(value.ToLowerInvariant());
            }

            IEnumerable<string> EnumerateAnimationSkins(string animationCharacter)
            {
                if (!string.IsNullOrEmpty(sourceSkin) &&
                    !sourceSkin.Equals("root", StringComparison.OrdinalIgnoreCase) &&
                    !sourceSkin.Equals("shared", StringComparison.OrdinalIgnoreCase))
                {
                    yield return sourceSkin;
                }

                IEnumerable<string> knownSkins = GetChampionSkinNames(animationCharacter, cancellationToken);
                foreach (string candidate in OrderAnimationContainers(sourceSkin, knownSkins))
                    if (!candidate.Equals(sourceSkin, StringComparison.OrdinalIgnoreCase)) yield return candidate;
            }

            IEnumerable<string> EnumerateActionsForSkin(string animChar, string sk)
            {
                bool isTargetSkin = !string.IsNullOrEmpty(sourceSkin) &&
                                    sk.Equals(sourceSkin, StringComparison.OrdinalIgnoreCase);
                if (isTargetSkin || string.IsNullOrEmpty(sourceSkin))
                {
                    return EnumerateFallbackActions(animChar);
                }
                return GetCharacterAnimationActions(animChar);
            }

            IReadOnlyList<string> EnumerateFallbackActions(string animationCharacter) =>
                Corpus.GetOrCreate($"fallback-animation-actions/{animationCharacter.ToLowerInvariant()}", _ =>
                    GetCharacterAnimationActions(animationCharacter)
                        .Concat(GetGlobalAnimationActions(cancellationToken))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList());

            static IEnumerable<string> EnumerateAnimationCandidates(
                string animationCharacter,
                string skin,
                string action,
                bool includeThemeLayout)
            {
                foreach (string candidate in EnumerateAnimationNameVariants(
                             animationCharacter,
                             skin,
                             action,
                             includeThemeLayout: includeThemeLayout))
                    yield return candidate;

                if (!action.Equals("recall", StringComparison.OrdinalIgnoreCase)) yield break;
                foreach (string root in AnimationRootPrefixes)
                {
                    yield return $"{root}/characters/{animationCharacter}/skins/{skin}/animations/recall.skins_{animationCharacter}_{skin}.anm";
                    if (animationCharacter.StartsWith("jade_", StringComparison.OrdinalIgnoreCase))
                        yield return $"{root}/characters/{animationCharacter}/skins/{skin}/animations/recall.skins_{animationCharacter[5..]}_{skin}.anm";
                }
            }
        }

        private IReadOnlyList<string> GetDynamicLoadoutRegaliaPaths(CancellationToken cancellationToken)
        {
            return Corpus.GetOrCreate("dynamic-loadout-regalia-paths", knownPaths =>
            {
                var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var queueTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "", "ranked_5s_", "ranked_solo_5s_", "ranked_flex_5s_", "ranked_3s_", "ranked_tft_", "arena_" };
                var tierTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var typeTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "", "_banner", "_crest", "_border", "_wings", "_flag", "_pedestal", "_badge" };
                var sizeTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "", "_512x512", "_256x256", "_1024x1024", "_128x128" };
                var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".tex", ".dds", ".png" };

                var regaliaRegex = new Regex(@"^(?<dir>(?:assets|data)/loadouts/regalia/[^/]+/)(?<file>[^.]+)(?<ext>\.[^.]+)$", RegexOptions.IgnoreCase);
                var trovesRegex = new Regex(@"^(?:plugins/rcp-be-lol-game-data/global/default/)?(?<dir>(?:assets|data)/ux/tft/troves_bannercontent/[^/]+/)(?<file>[^.]+)(?:\.[^./]+)?(?<ext>\.[^.]+)$", RegexOptions.IgnoreCase);

                for (int i = 0; i < knownPaths.Count; i++)
                {
                    string p = knownPaths[i];
                    if (p.Contains("loadouts/regalia", StringComparison.OrdinalIgnoreCase))
                    {
                        Match match = regaliaRegex.Match(p);
                        if (!match.Success) continue;

                        directories.Add(match.Groups["dir"].Value.ToLowerInvariant());
                        extensions.Add(match.Groups["ext"].Value.ToLowerInvariant());

                        string file = match.Groups["file"].Value.ToLowerInvariant();
                        string[] parts = file.Split('_', StringSplitOptions.RemoveEmptyEntries);
                        foreach (string part in parts)
                        {
                            if (part.Contains('x') && part.All(c => char.IsDigit(c) || c == 'x'))
                                sizeTokens.Add($"_{part}");
                            else if (part is "iron" or "bronze" or "silver" or "gold" or "platinum" or "emerald" or "diamond" or "master" or "grandmaster" or "challenger" or "unranked")
                                tierTokens.Add(part);
                        }
                    }
                    else if (p.Contains("troves_bannercontent", StringComparison.OrdinalIgnoreCase))
                    {
                        Match match = trovesRegex.Match(p);
                        if (!match.Success) continue;

                        string dir = match.Groups["dir"].Value.ToLowerInvariant();
                        string file = match.Groups["file"].Value.ToLowerInvariant();

                        candidates.Add($"{dir}{file}.tex");
                        candidates.Add($"{dir}{file}.dds");
                        candidates.Add($"{dir}{file}.png");
                    }
                    else if (p.Contains("loadouts/companions", StringComparison.OrdinalIgnoreCase) || p.Contains(".cutscene.bin", StringComparison.OrdinalIgnoreCase))
                    {
                        string clean = p;
                        if (clean.StartsWith("plugins/rcp-be-lol-game-data/global/default/", StringComparison.OrdinalIgnoreCase))
                            clean = clean[44..];
                        if (clean.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                            clean = clean[7..];
                        if (clean.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
                            clean = clean[5..];

                        candidates.Add(clean);
                        candidates.Add($"data/{clean}");
                        candidates.Add($"assets/{clean}");
                    }
                }

                var cutscenePets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var petThemeTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "base", "tier1" };
                for (int i = 0; i < knownPaths.Count; i++)
                {
                    string p = knownPaths[i];
                    if (p.StartsWith("data/characters/pet", StringComparison.OrdinalIgnoreCase) ||
                        p.StartsWith("assets/characters/pet", StringComparison.OrdinalIgnoreCase))
                    {
                        int slash1 = p.IndexOf('/', 16);
                        if (slash1 > 0)
                        {
                            string pet = p[16..slash1];
                            string cleanPet = pet.StartsWith("pet", StringComparison.OrdinalIgnoreCase) ? pet[3..] : pet;
                            cutscenePets.Add(cleanPet);
                            cutscenePets.Add(pet);
                        }
                    }
                    if (p.Contains("/themes/", StringComparison.OrdinalIgnoreCase))
                    {
                        int themeIdx = p.IndexOf("/themes/", StringComparison.OrdinalIgnoreCase);
                        int nextSlash = p.IndexOf('/', themeIdx + 8);
                        if (nextSlash > 0)
                        {
                            petThemeTokens.Add(p.Substring(themeIdx + 8, nextSlash - (themeIdx + 8)).ToLowerInvariant());
                        }
                    }
                }

                foreach (string pet in cutscenePets)
                foreach (string theme in petThemeTokens)
                {
                    for (int tier = 1; tier <= 3; tier++)
                    {
                        candidates.Add($"loadouts/companions/{pet}_{theme}_{theme}_tier{tier}.cutscene.bin");
                        candidates.Add($"loadouts/companions/{pet}_{theme}_tier{tier}.cutscene.bin");
                        candidates.Add($"data/loadouts/companions/{pet}_{theme}_{theme}_tier{tier}.cutscene.bin");
                        candidates.Add($"assets/loadouts/companions/{pet}_{theme}_{theme}_tier{tier}.cutscene.bin");
                    }
                }

                foreach (string dir in directories)
                foreach (string queue in queueTokens)
                foreach (string tier in tierTokens)
                foreach (string type in typeTokens)
                foreach (string size in sizeTokens)
                foreach (string ext in extensions)
                {
                    string candidate = $"{dir}{queue}{tier}{type}{size}{ext}".ToLowerInvariant().Replace("__", "_");
                    candidates.Add(candidate);
                }

                return candidates.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        private void GuessRegaliaBinChunkLinks(
            HashGuessEngine engine,
            ArraySegment<byte> data,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            CancellationToken cancellationToken,
            Func<BinTree> binTreeFactory = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Array is null || data.Count == 0) return;

            if (!sourcePath.Contains("regalia", StringComparison.OrdinalIgnoreCase) &&
                !sourcePath.Contains("loadouts", StringComparison.OrdinalIgnoreCase) &&
                !sourcePath.Contains("troves", StringComparison.OrdinalIgnoreCase) &&
                !sourcePath.Contains("companions", StringComparison.OrdinalIgnoreCase) &&
                !sourceWadPath.Contains("global", StringComparison.OrdinalIgnoreCase) &&
                !sourceWadPath.Contains("companion", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var unresolved = new HashSet<ulong>();
            try
            {
                BinTree tree = binTreeFactory != null
                    ? binTreeFactory()
                    : new BinTree(new MemoryStream(data.Array, data.Offset, data.Count, writable: false));
                if (tree == null) return;
                cancellationToken.ThrowIfCancellationRequested();
                foreach (ulong link in EnumerateChunkLinks(tree))
                {
                    if (engine.UnknownHashes.Contains(link))
                        unresolved.Add(link);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logService?.LogDebug($"GAME regalia BIN link scan skipped '{sourcePath}': {exception.Message}");
                return;
            }

            if (unresolved.Count == 0) return;

            var candidates = GetDynamicLoadoutRegaliaPaths(cancellationToken);
            foreach (string candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (unresolved.Count == 0 || engine.RemainingUnknownCount == 0) return;

                ulong hash = XxHash64Ext.Hash(candidate);
                if (unresolved.Remove(hash))
                {
                    Check(engine, candidate, HashGuessStrategy.BannerVariant, sourceWadPath, sourceChunkHash);
                }
            }
        }

        private static void HarvestSubmeshTokens(string text, HashSet<string> smSet)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (string sm in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string smLow = sm.ToLowerInvariant();
                if (smLow.Length >= 3)
                {
                    smSet.Add(smLow);
                    string[] parts = smLow.Split('_', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1)
                    {
                        for (int i = 1; i < parts.Length; i++)
                            smSet.Add(string.Join('_', parts.Take(i)));
                        foreach (string part in parts)
                        {
                            if (part.Length >= 3 && part != "top" && part != "low" && part != "bot")
                                smSet.Add(part);
                        }
                    }
                }
            }
        }

        private static void HarvestRoleTokens(string text, HashSet<string> descriptors)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Contains('/')) return;
            string low = text.ToLowerInvariant().Replace("_texture", "").Replace("_tex", "");
            if (low.Length >= 3)
            {
                descriptors.Add("_" + low);
                string[] parts = low.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    descriptors.Add("_" + string.Join("", parts));
                    foreach (string part in parts)
                    {
                        if (part.Length >= 3 && part != "texture")
                        {
                            descriptors.Add("_" + part);
                            if (part.EndsWith("scroll", StringComparison.Ordinal))
                                descriptors.Add("_scrollmask");
                        }
                    }
                }
            }
        }

        private void GuessSkinCharacterBinChunkLinks(
            HashGuessEngine engine,
            ArraySegment<byte> data,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            CancellationToken cancellationToken,
            Func<BinTree> binTreeFactory = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Array is null || data.Count == 0 || engine.RemainingUnknownCount == 0) return;

            BinTree tree;
            try
            {
                tree = binTreeFactory != null
                    ? binTreeFactory()
                    : new BinTree(new MemoryStream(data.Array, data.Offset, data.Count, writable: false));
                if (tree == null) return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logService?.LogDebug($"GAME skin character BIN link scan skipped '{sourcePath}': {exception.Message}");
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            void CollectChunkLinks(BinTreeProperty prop, HashSet<ulong> hashes)
            {
                switch (prop)
                {
                    case BinTreeWadChunkLink link:
                        if (link.Value != 0 && engine.UnknownHashes.Contains(link.Value))
                            hashes.Add(link.Value);
                        break;
                    case BinTreeStruct str:
                        foreach (BinTreeProperty p in str.Properties.Values)
                            CollectChunkLinks(p, hashes);
                        break;
                    case BinTreeContainer container:
                        foreach (BinTreeProperty p in container.Elements)
                            CollectChunkLinks(p, hashes);
                        break;
                    case BinTreeMap map:
                        foreach (var kv in map)
                        {
                            CollectChunkLinks(kv.Key, hashes);
                            CollectChunkLinks(kv.Value, hashes);
                        }
                        break;
                    case BinTreeOptional opt:
                        if (opt.Value != null)
                            CollectChunkLinks(opt.Value, hashes);
                        break;
                }
            }

            var submeshesBySkin = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var skinObjects = new List<(BinTreeObject Obj, string SimpleSkin, string Skeleton, HashSet<ulong> Links)>();
            var materialObjects = new List<(string Champ, string Skin, string RawMat, HashSet<ulong> Links, HashSet<string> Descriptors)>();
            int totalUnresolved = 0;

            // Phase 1: Fast single-pass metadata harvest across all BIN objects
            foreach (BinTreeObject obj in tree.Objects.Values)
            {
                if (obj.ClassHash == 0x9b67e9f6) // SkinCharacterDataProperties
                {
                    var targetTexHashes = new HashSet<ulong>();
                    string simpleSkin = null;
                    string skeleton = null;

                    if ((obj.Properties.TryGetValue(0x45ff5904, out BinTreeProperty meshProp) ||
                         obj.Properties.TryGetValue(0x5337242d, out meshProp)) &&
                        meshProp is BinTreeStruct meshStruct)
                    {
                        CollectChunkLinks(meshStruct, targetTexHashes);
                        if (meshStruct.Properties.TryGetValue(0xd6a00df6, out BinTreeProperty skinProp) &&
                            skinProp is BinTreeString skinStr)
                        {
                            simpleSkin = skinStr.Value;
                        }
                        if (meshStruct.Properties.TryGetValue(0xb14c976e, out BinTreeProperty skelProp) &&
                            skelProp is BinTreeString skelStr)
                        {
                            skeleton = skelStr.Value;
                        }

                        string refSkin = !string.IsNullOrEmpty(simpleSkin) ? simpleSkin : skeleton;
                        if (!string.IsNullOrEmpty(refSkin))
                        {
                            Match m = SkinPathRegex.Match(refSkin.Replace('\\', '/'));
                            if (m.Success)
                            {
                                string sKey = m.Groups["skin"].Value.ToLowerInvariant();
                                if (!submeshesBySkin.TryGetValue(sKey, out var smSet))
                                    submeshesBySkin[sKey] = smSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                                // Harvest submeshes from strings and override containers (submeshRenderOrder, materialOverride, etc.)
                                foreach (BinTreeProperty p in meshStruct.Properties.Values)
                                {
                                    if (p is BinTreeString sVal)
                                    {
                                        HarvestSubmeshTokens(sVal.Value, smSet);
                                    }
                                    else if (p is BinTreeContainer container)
                                    {
                                        foreach (BinTreeProperty elem in container.Elements)
                                        {
                                            if (elem is BinTreeStruct elemStruct)
                                            {
                                                foreach (BinTreeProperty ep in elemStruct.Properties.Values)
                                                {
                                                    if (ep is BinTreeString subStr)
                                                        HarvestSubmeshTokens(subStr.Value, smSet);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    foreach (BinTreeProperty p in obj.Properties.Values)
                        CollectChunkLinks(p, targetTexHashes);

                    if (targetTexHashes.Count > 0)
                    {
                        skinObjects.Add((obj, simpleSkin, skeleton, targetTexHashes));
                        totalUnresolved += targetTexHashes.Count;
                    }
                }
                else if (obj.ClassHash == 0xff9d3409) // StaticMaterialDef
                {
                    if (obj.Properties.TryGetValue(0x8d39bde6, out BinTreeProperty pMat) &&
                        pMat is BinTreeString sMat &&
                        !string.IsNullOrEmpty(sMat.Value))
                    {
                        Match matMatch = MaterialPathRegex.Match(sMat.Value.Replace('\\', '/'));
                        if (matMatch.Success)
                        {
                            var matLinks = new HashSet<ulong>();
                            foreach (BinTreeProperty p in obj.Properties.Values)
                                CollectChunkLinks(p, matLinks);

                            if (matLinks.Count > 0)
                            {
                                string mChamp = matMatch.Groups["champ"].Value.ToLowerInvariant();
                                string mSkin = matMatch.Groups["skin"].Value.ToLowerInvariant();
                                string rawMat = matMatch.Groups["mat"].Value.ToLowerInvariant();
                                if (rawMat.EndsWith("_inst", StringComparison.OrdinalIgnoreCase)) rawMat = rawMat[..^5];
                                if (rawMat.EndsWith("_mat", StringComparison.OrdinalIgnoreCase)) rawMat = rawMat[..^4];
                                if (rawMat.StartsWith(mChamp + "_" + mSkin + "_", StringComparison.OrdinalIgnoreCase))
                                    rawMat = rawMat[(mChamp.Length + mSkin.Length + 2)..];
                                else if (rawMat.StartsWith(mChamp + "_", StringComparison.OrdinalIgnoreCase))
                                    rawMat = rawMat[(mChamp.Length + 1)..];

                                var descriptors = new HashSet<string>(CanonicalMaterialRoles, StringComparer.OrdinalIgnoreCase);
                                foreach (BinTreeProperty p in obj.Properties.Values)
                                {
                                    if (p is BinTreeContainer container)
                                    {
                                        foreach (BinTreeProperty elem in container.Elements)
                                        {
                                            if (elem is BinTreeStruct samplerStruct)
                                            {
                                                foreach (BinTreeProperty sp in samplerStruct.Properties.Values)
                                                {
                                                    if (sp is BinTreeString sVal)
                                                        HarvestRoleTokens(sVal.Value, descriptors);
                                                }
                                            }
                                        }
                                    }
                                }

                                materialObjects.Add((mChamp, mSkin, rawMat, matLinks, descriptors));
                                totalUnresolved += matLinks.Count;
                            }
                        }
                    }
                }
            }

            if (totalUnresolved == 0 || engine.RemainingUnknownCount == 0) return;

            // Phase 2: Resolve Modern Skin Materials (StaticMaterialDef)
            foreach (var (mChamp, mSkin, rawMat, matLinks, matDescriptors) in materialObjects)
            {
                if (engine.RemainingUnknownCount == 0) break;

                var baseStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rawMat };
                if (rawMat.Contains('_'))
                {
                    foreach (string tok in rawMat.Split('_', StringSplitOptions.RemoveEmptyEntries))
                        if (tok.Length >= 3 && tok != "matcap" && tok != "inst" && tok != "mat")
                            baseStems.Add(tok);
                }

                if (submeshesBySkin.TryGetValue(mSkin, out var smList))
                    baseStems.UnionWith(smList);

                var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string baseSt in baseStems)
                {
                    string root = baseSt.EndsWith("_f1", StringComparison.OrdinalIgnoreCase) ? baseSt[..^3] : baseSt;
                    stems.Add(root);
                    stems.Add(root + "_f1");
                    stems.Add(root + "_f2");
                    stems.Add(root + "_f3");
                    stems.Add(root + "_f4");
                    stems.Add(root + "_f1_f2");
                    stems.Add(root + "_f2_f3");
                    stems.Add(root + "_empowered");
                    stems.Add(root + "_reset");
                    stems.Add("empowered_" + root);
                }

                string baseDir = $"assets/characters/{mChamp}/skins/{mSkin}/";
                foreach (ulong unk in matLinks)
                {
                    if (engine.RemainingUnknownCount == 0) break;
                    if (!engine.UnknownHashes.Contains(unk)) continue;

                    Check(engine, $"{baseDir}{mChamp}_{mSkin}_tx_cm.tex", HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                    Check(engine, $"{baseDir}{mChamp}_{mSkin}_mask_tx_cm.tex", HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                    Check(engine, $"{baseDir}{mChamp}_{mSkin}_matcap.tex", HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);

                    foreach (string st in stems)
                    {
                        if (!engine.UnknownHashes.Contains(unk)) break;
                        foreach (string desc in matDescriptors)
                        {
                            if (!engine.UnknownHashes.Contains(unk)) break;
                            foreach (string suf in MaterialTextureSuffixes)
                            {
                                string c1 = $"{baseDir}{mChamp}_{mSkin}_{st}{desc}{suf}";
                                if (XxHash64Ext.Hash(c1) == unk)
                                {
                                    Check(engine, c1, HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                                    break;
                                }
                                string c2 = $"{baseDir}{mChamp}_{st}{desc}{suf}";
                                if (XxHash64Ext.Hash(c2) == unk)
                                {
                                    Check(engine, c2, HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                                    break;
                                }
                                string c3 = $"{baseDir}{st}{desc}{suf}";
                                if (XxHash64Ext.Hash(c3) == unk)
                                {
                                    Check(engine, c3, HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // Phase 3: Resolve Skin Mesh Links (SkinCharacterDataProperties)
            foreach (var (obj, simpleSkin, skeleton, targetTexHashes) in skinObjects)
            {
                if (engine.RemainingUnknownCount == 0 || targetTexHashes.Count == 0) break;

                string refPath = !string.IsNullOrEmpty(simpleSkin) ? simpleSkin : skeleton;
                if (string.IsNullOrEmpty(refPath)) continue;

                refPath = refPath.Replace('\\', '/').ToLowerInvariant();
                int lastSlash = refPath.LastIndexOf('/');
                string dir = lastSlash >= 0 ? refPath[..(lastSlash + 1)] : "";
                if (dir.EndsWith("/rig/", StringComparison.OrdinalIgnoreCase)) dir = dir[..^4];

                string fileStem = lastSlash >= 0 ? refPath[(lastSlash + 1)..] : refPath;
                int dot = fileStem.IndexOf('.');
                if (dot >= 0) fileStem = fileStem[..dot];

                Match champMatch = SkinPathRegex.Match(dir);
                string champ = champMatch.Success ? champMatch.Groups["champ"].Value.ToLowerInvariant() : "";
                string cleanChamp = champ.StartsWith("jade_", StringComparison.OrdinalIgnoreCase) ? champ[5..] : champ;

                var candidateDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { dir };
                if (dir.Contains("/jade_", StringComparison.OrdinalIgnoreCase))
                    candidateDirs.Add(dir.Replace("/jade_", "/", StringComparison.OrdinalIgnoreCase));
                if (dir.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                    candidateDirs.Add("data/" + dir[7..]);
                else if (dir.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
                    candidateDirs.Add("assets/" + dir[5..]);
                else
                {
                    candidateDirs.Add("assets/" + dir);
                    candidateDirs.Add("data/" + dir);
                }

                Match skinFolderMatch = Regex.Match(dir, @"/skins/skin30*(?<num>\d+)/", RegexOptions.IgnoreCase);
                if (skinFolderMatch.Success)
                {
                    string num = skinFolderMatch.Groups["num"].Value;
                    string skinPad = num.Length == 1 ? $"skin0{num}" : $"skin{num}";
                    string skinRaw = $"skin{num}";
                    var baseDirs = candidateDirs.ToList();
                    foreach (string bd in baseDirs)
                    {
                        candidateDirs.Add(Regex.Replace(bd, @"/skins/skin30*\d+/", $"/skins/{skinPad}/", RegexOptions.IgnoreCase));
                        candidateDirs.Add(Regex.Replace(bd, @"/skins/skin30*\d+/", $"/skins/{skinRaw}/", RegexOptions.IgnoreCase));
                    }
                }

                var candidateStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fileStem };
                if (fileStem.StartsWith("jade_", StringComparison.OrdinalIgnoreCase))
                    candidateStems.Add(fileStem[5..]);
                else
                    candidateStems.Add("jade_" + fileStem);

                var champAliases = new List<string>();
                if (!string.IsNullOrEmpty(cleanChamp))
                {
                    champAliases.Add(cleanChamp);
                    if (cleanChamp == "xinzhao")
                    {
                        champAliases.Add("xenzhao");
                        champAliases.Add("xinzhaorework");
                    }
                    else if (cleanChamp == "orianna")
                    {
                        champAliases.Add("oriana");
                    }

                    foreach (string ca in champAliases)
                    {
                        candidateStems.Add(ca);
                        candidateStems.Add($"{ca}_tx");
                    }
                }

                string skinName = null;
                if (obj.Properties.TryGetValue(0x2d78c328, out BinTreeProperty nameProp) &&
                    nameProp is BinTreeString nameStr)
                {
                    skinName = nameStr.Value;
                }

                if (!string.IsNullOrEmpty(skinName))
                {
                    string cleanName = skinName.ToLowerInvariant();
                    candidateStems.Add(cleanName);
                    candidateStems.Add(cleanName.Replace(' ', '_'));
                    candidateStems.Add(cleanName.Replace(" ", ""));
                    string[] parts = cleanName.Split(new[] { '_', ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
                    var meaningfulTokens = parts.Where(p => p != "jade" && p != "classic" && p.Length >= 3).ToList();
                    foreach (string t in meaningfulTokens)
                    {
                        candidateStems.Add(t);
                        foreach (string ca in champAliases)
                        {
                            candidateStems.Add($"{ca}_{t}");
                            candidateStems.Add($"jade_{ca}_{t}");
                            candidateStems.Add($"{t}_{ca}");
                            candidateStems.Add($"{t}_jade_{ca}");
                        }
                    }
                    if (parts.Length >= 2)
                    {
                        candidateStems.Add($"{parts[0]}_{parts[^1]}");
                        candidateStems.Add($"{parts[^1]}_{parts[0]}");
                    }
                }

                string[] stemParts = fileStem.Split('_', StringSplitOptions.RemoveEmptyEntries);
                var stemTokens = stemParts.Where(p => p != "jade" && p != "classic" && p != "rg" && p != cleanChamp && p.Length >= 3).ToList();
                foreach (string st in stemTokens)
                {
                    candidateStems.Add(st);
                    foreach (string ca in champAliases)
                    {
                        candidateStems.Add($"{ca}_{st}");
                        candidateStems.Add($"jade_{ca}_{st}");
                        candidateStems.Add($"{st}_{ca}");
                        candidateStems.Add($"{st}_jade_{ca}");
                    }
                }

                string sKey = champMatch.Success ? champMatch.Groups["skin"].Value.ToLowerInvariant() : "";
                if (!string.IsNullOrEmpty(sKey) && submeshesBySkin.TryGetValue(sKey, out var smList))
                {
                    foreach (string sm in smList) candidateStems.Add(sm);
                }

                var allStems = candidateStems.ToList();
                foreach (string s in allStems)
                {
                    candidateStems.Add(s + "_base");
                    candidateStems.Add(s + "_tx");
                    candidateStems.Add(s + "_base_tx");
                    candidateStems.Add("2x_" + s);
                    candidateStems.Add("4x_" + s);
                    if (s.Contains("royalguard", StringComparison.OrdinalIgnoreCase) || s.Contains("royal_guard", StringComparison.OrdinalIgnoreCase))
                    {
                        candidateStems.Add("fiora_musketeer");
                        candidateStems.Add("fiora_musketeer_tx");
                    }
                    if (s.Contains("nightraven", StringComparison.OrdinalIgnoreCase) || s.Contains("night_raven", StringComparison.OrdinalIgnoreCase))
                    {
                        candidateStems.Add("fiora_zorro");
                        candidateStems.Add("fiora_zorro_tx");
                    }
                    if (s.Contains("hextech", StringComparison.OrdinalIgnoreCase))
                    {
                        candidateStems.Add("galio_hextech");
                        candidateStems.Add("galio_hextech_tx");
                    }
                    if (s.Contains("lumberjack", StringComparison.OrdinalIgnoreCase))
                    {
                        candidateStems.Add("sion_lumberjack");
                        candidateStems.Add("sion_lumberjack_tx");
                    }
                }

                foreach (string cDir in candidateDirs)
                {
                    if (targetTexHashes.Count == 0 || engine.RemainingUnknownCount == 0) break;
                    foreach (string cStem in candidateStems)
                    {
                        if (targetTexHashes.Count == 0 || engine.RemainingUnknownCount == 0) break;
                        foreach (string suf in SkinTextureSuffixes)
                        {
                            string candidatePath = cDir + cStem + suf;
                            ulong hash = XxHash64Ext.Hash(candidatePath);
                            if (targetTexHashes.Remove(hash) && engine.UnknownHashes.Contains(hash))
                            {
                                Check(engine, candidatePath, HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                                if (targetTexHashes.Count == 0) break;
                            }
                        }
                    }
                }
            }
        }


        internal int GuessRegaliaAssets(

            HashGuessEngine engine,
            CancellationToken cancellationToken,
            Action<int> progress = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            if (engine.RemainingUnknownCount == 0) return 0;

            var candidates = GetDynamicLoadoutRegaliaPaths(cancellationToken);
            int checkedCount = 0;
            foreach (string path in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Check(engine, path, HashGuessStrategy.BannerVariant, "Regalia matrix");
                checkedCount++;
                if ((checkedCount & 0xFFF) == 0)
                {
                    progress?.Invoke(checkedCount);
                }
                if (engine.RemainingUnknownCount == 0) break;
            }

            progress?.Invoke(checkedCount);
            return checkedCount;
        }

        private IReadOnlyDictionary<string, IReadOnlyList<string>> GetDynamicPetThemeNames(
            CancellationToken cancellationToken)
        {
            return Corpus.GetOrCreate("dynamic-pet-themes-by-character", knownPaths =>
            {
                var themesByPet = new Dictionary<string, HashSet<string>>(
                    StringComparer.OrdinalIgnoreCase);
                var petThemeRegex = new Regex(
                    @"^(?:assets|data)/characters/(?<pet>pet[^/]+)/themes/(?<theme>[^/]+)/",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled);

                for (int i = 0; i < knownPaths.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string path = knownPaths[i];
                    Match match = petThemeRegex.Match(path);
                    if (!match.Success) continue;

                    string pet = match.Groups["pet"].Value.ToLowerInvariant();
                    string theme = match.Groups["theme"].Value.ToLowerInvariant();
                    if (theme.Length > 40 || theme.Contains('.')) continue;

                    if (!themesByPet.TryGetValue(pet, out HashSet<string> themes))
                        themesByPet[pet] = themes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    themes.Add(theme);
                }

                var result = new Dictionary<string, IReadOnlyList<string>>(
                    StringComparer.OrdinalIgnoreCase);
                foreach ((string pet, HashSet<string> themes) in themesByPet)
                {
                    themes.Add("base");
                    result[pet] = themes
                        .OrderBy(theme => theme, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                return result;
            });
        }

        private void GuessChampionSpecialBins(
            HashGuessEngine engine,
            string sourceWadPath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(sourceWadPath) ||
                !sourceWadPath.Contains("champions", StringComparison.OrdinalIgnoreCase))
                return;

            string fileName = Path.GetFileName(sourceWadPath);
            string[] parts = fileName.Split('.');
            if (parts.Length != 3 || !parts[1].Equals("wad", StringComparison.OrdinalIgnoreCase))
                return;

            string champName = parts[0];
            if (string.IsNullOrEmpty(champName))
                return;

            ConcurrentDictionary<string, byte> scannedCharacters = _scannedWadCharacters.GetValue(
                engine,
                _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            if (!scannedCharacters.TryAdd(champName, 0)) return;

            string champ = champName.ToLowerInvariant();
            string[] aliases = champ.StartsWith("jade_", StringComparison.OrdinalIgnoreCase) ||
                               champ.StartsWith("pet", StringComparison.OrdinalIgnoreCase)
                ? new[] { champ }
                : new[] { champ, $"jade_{champ}" };

            foreach (string alias in aliases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (engine.RemainingUnknownCount == 0) break;

                CheckSpecialBin($"data/characters/{alias}/{alias}.bin");
                CheckSpecialBin($"data/characters/{alias}/skins/root.bin");
                CheckSpecialBin($"gameplay.hol{alias}ncvc.bin");
                CheckSpecialBin($"gameplay.{alias}comps.bin");

                string consonantStem = new string(alias.Where(c => !"aeiou_".Contains(c)).ToArray());
                if (!string.IsNullOrEmpty(consonantStem) && !consonantStem.Equals(alias, StringComparison.OrdinalIgnoreCase))
                {
                    CheckSpecialBin($"gameplay.{consonantStem}comps.bin");
                }

                for (int s = 0; s <= 350; s++)
                {
                    if (engine.RemainingUnknownCount == 0) break;
                    CheckSpecialBin($"gameplay.{alias}skin{s}viewcontroller.bin");
                }

                var dynamicSkins = GetChampionSkinNames(alias, cancellationToken);
                foreach (string skin in dynamicSkins)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (engine.RemainingUnknownCount == 0) break;

                    CheckSpecialBin($"data/characters/{alias}/skins/{skin}.bin");
                    CheckSpecialBin($"data/characters/{alias}/animations/{skin}.bin");
                }
            }

            void CheckSpecialBin(string path)
            {
                ulong hash = XxHash64Ext.Hash(path);
                if (engine.UnknownHashes.Contains(hash))
                {
                    Check(engine, path, HashGuessStrategy.BinEntry, sourceWadPath);
                }
            }
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
                    IEnumerable<string> candidates = pool
                        .Skip(wordOffset)
                        .Take(batchCount)
                        .Select(word => format.Prefix + word + format.Suffix);
                    checkedCandidates += CheckIter(
                        engine,
                        candidates,
                        HashGuessStrategy.WordlistVariant,
                        "GAME Custom: animation actions build-list",
                        cancellationToken);
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
        /// Executes the learned skin texture build-list attack across all champion skin families
        /// to discover unresolved material masks, flowmaps, and texture links.
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

        private IReadOnlyDictionary<string, IReadOnlyList<string>> GetChampionSkinMap(CancellationToken cancellationToken)
        {
            return Corpus.GetOrCreate("all-champion-skin-maps", knownPaths =>
            {
                var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                for (int pathIndex = 0; pathIndex < knownPaths.Count; pathIndex++)
                {
                    if ((pathIndex & 0x3fff) == 0) cancellationToken.ThrowIfCancellationRequested();
                    string path = knownPaths[pathIndex];
                    if (!path.StartsWith("assets/characters/", StringComparison.OrdinalIgnoreCase) &&
                        !path.StartsWith("data/characters/", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int skinsIdx = path.IndexOf("/skins/", StringComparison.OrdinalIgnoreCase);
                    if (skinsIdx <= 0) continue;

                    string champ = path.Substring(path.StartsWith("assets/characters/", StringComparison.OrdinalIgnoreCase) ? 18 : 16, skinsIdx - (path.StartsWith("assets/characters/", StringComparison.OrdinalIgnoreCase) ? 18 : 16)).ToLowerInvariant();
                    string rel = path.Substring(skinsIdx + 7);
                    int slash = rel.IndexOf('/');
                    string skin = slash > 0 ? rel[..slash] : rel;
                    if (skin.Length is >= 3 and <= 35 && !skin.Contains('.'))
                    {
                        if (!map.TryGetValue(champ, out var set))
                        {
                            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "base" };
                            map[champ] = set;
                        }
                        set.Add(skin);
                    }
                }

                return (IReadOnlyDictionary<string, IReadOnlyList<string>>)map.ToDictionary(
                    kvp => kvp.Key,
                    kvp => (IReadOnlyList<string>)kvp.Value.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
                    StringComparer.OrdinalIgnoreCase);
            });
        }

        private IReadOnlyList<string> GetChampionSkinNames(string character, CancellationToken cancellationToken)
        {
            var map = GetChampionSkinMap(cancellationToken);
            string baseChar = character.StartsWith("jade_", StringComparison.OrdinalIgnoreCase) ? character[5..] : character;
            
            var skins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "base" };
            if (map.TryGetValue(character, out var directSkins))
            {
                for (int i = 0; i < directSkins.Count; i++) skins.Add(directSkins[i]);
            }
            if (baseChar != character && map.TryGetValue(baseChar, out var baseSkins))
            {
                for (int i = 0; i < baseSkins.Count; i++) skins.Add(baseSkins[i]);
            }

            // Add attested skin numbers + padding
            int maxAttested = 0;
            foreach (var s in skins)
            {
                if (s.StartsWith("skin", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(s[4..], out int num))
                {
                    if (num > maxAttested) maxAttested = num;
                }
            }

            int limit = Math.Max(maxAttested + 15, 85);
            if (character.Equals("sightward", StringComparison.OrdinalIgnoreCase)) limit = 500;
            for (int i = 0; i <= limit; i++)
            {
                skins.Add($"skin{i}");
                if (i <= 9) skins.Add($"skin{i:D2}");
            }

            for (int i = 300; i <= 350; i++) skins.Add($"skin{i}");

            return skins.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IEnumerable<ulong> EnumerateChunkLinks(BinTree tree)
        {
            var roots = tree.Objects.Values.SelectMany(obj => obj.Properties.Values)
                .Concat(tree.DataOverrides.Select(ovr => ovr.Property));
            foreach (BinTreeProperty root in roots)
            {
                foreach (BinTreeProperty prop in EnumerateAllProperties(root))
                {
                    if (prop is BinTreeWadChunkLink link && link.Value != 0)
                        yield return link.Value;
                }
            }
        }

        private static IEnumerable<BinTreeProperty> EnumerateAllProperties(BinTreeProperty property)
        {
            if (property == null) yield break;
            yield return property;

            IEnumerable<BinTreeProperty> children = property switch
            {
                BinTreeStruct structure => structure.Properties.Values,
                BinTreeOptional optional when optional.Value != null => new[] { optional.Value },
                BinTreeContainer container => container.Elements,
                BinTreeMap map => map.SelectMany(pair => new[] { pair.Key, pair.Value }),
                _ => Array.Empty<BinTreeProperty>()
            };
            foreach (BinTreeProperty child in children)
            foreach (BinTreeProperty descendant in EnumerateAllProperties(child))
                yield return descendant;
        }

        private IReadOnlyDictionary<uint, List<string>> GetAnimationNameIndex(CancellationToken cancellationToken)
        {
            return Corpus.GetOrCreate("animation-name-index", knownPaths =>
            {
                var index = new Dictionary<uint, List<string>>();
                for (int pathIndex = 0; pathIndex < knownPaths.Count; pathIndex++)
                {
                    if ((pathIndex & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
                    string path = knownPaths[pathIndex];
                    if (!path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase)) continue;
                    string name = GetBasename(path);
                    if (name.Length <= 4) continue;
                    string stem = name[..^4];
                    Add(Fnv1a.HashLower(stem), stem);
                    string compact = new(stem.Where(char.IsLetterOrDigit).ToArray());
                    if (compact.Length > 0) Add(Fnv1a.HashLower(compact), stem);

                    int sep = stem.IndexOf('_');
                    while (sep >= 0 && sep < stem.Length - 1)
                    {
                        string sub = stem[(sep + 1)..];
                        if (sub.Length >= 2)
                        {
                            Add(Fnv1a.HashLower(sub), sub);
                            string compactSub = new(sub.Where(char.IsLetterOrDigit).ToArray());
                            if (compactSub.Length > 0) Add(Fnv1a.HashLower(compactSub), sub);
                        }
                        sep = stem.IndexOf('_', sep + 1);
                    }
                }
                if (index.Count == 0)
                {
                    foreach (string baseAction in BaseAnimationActions)
                    {
                        Add(Fnv1a.HashLower(baseAction), baseAction);
                        string compact = new(baseAction.Where(char.IsLetterOrDigit).ToArray());
                        if (compact.Length > 0) Add(Fnv1a.HashLower(compact), baseAction);
                    }
                }

                return index;

                void Add(uint hash, string stem)
                {
                    if (!index.TryGetValue(hash, out List<string> values))
                        index.Add(hash, values = new List<string>());
                    if (!values.Contains(stem, StringComparer.OrdinalIgnoreCase)) values.Add(stem);
                }
            });
        }

        private void GuessImageAutoAtlasPaths(
            HashGuessEngine engine,
            ArraySegment<byte> data,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Array is null || data.Count == 0) return;
            if (!ImageAutoAtlas.IsAtlas(data.AsSpan()) || !ImageAutoAtlas.TryRead(data.Array[data.Offset..(data.Offset + data.Count)], out ImageAutoAtlas atlas))
                return;

            IReadOnlyDictionary<ulong, string> knownDict = Corpus.GetOrCreate("known-hashes-dict", _ => HashFile.Load());

            // Ensure any sprite hash not in HashFile is marked unknown in engine
            bool hasUnresolvedSprites = false;
            foreach (var sprite in atlas.Sprites)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (engine.UnknownHashes.Contains(sprite.SpriteHash) || !knownDict.ContainsKey(sprite.SpriteHash))
                {
                    engine.EnsureUnknown(sprite.SpriteHash);
                    hasUnresolvedSprites = true;
                }
            }
            if (!hasUnresolvedSprites) return;

            var candidateDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(sourcePath) && !sourcePath.Equals(".bin", StringComparison.OrdinalIgnoreCase))
            {
                string dir = Path.GetDirectoryName(PathUtils.NormalizePath(sourcePath));
                if (!string.IsNullOrEmpty(dir))
                    candidateDirs.Add(PathUtils.NormalizeSeparators(dir));
            }

            if (candidateDirs.Count == 0 && atlas.TextureHashes.Count > 0)
            {
                var texDirIndex = GetTextureHashToDirectoryIndex();
                foreach (ulong texHash in atlas.TextureHashes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (texDirIndex.TryGetValue(texHash, out string dir))
                        candidateDirs.Add(dir);
                }
            }

            if (candidateDirs.Count == 0)
            {
                foreach (string dir in GetAllKnownAtlasDirectories())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    candidateDirs.Add(dir);
                }
            }

            IReadOnlyList<string> candidatePatterns = GetAutoAtlasCandidatePatterns();
            foreach (string baseDir in candidateDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (string pattern in candidatePatterns)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Check(engine, $"{baseDir}/{pattern}", HashGuessStrategy.AtlasReference, sourceWadPath, sourceChunkHash);

                    // If all sprites in this atlas are resolved, stop immediately
                    bool stillHasUnresolved = false;
                    foreach (var sprite in atlas.Sprites)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (engine.UnknownHashes.Contains(sprite.SpriteHash))
                        {
                            stillHasUnresolved = true;
                            break;
                        }
                    }
                    if (!stillHasUnresolved || engine.RemainingUnknownCount == 0) break;
                }

                bool anyRemaining = false;
                foreach (var sprite in atlas.Sprites)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (engine.UnknownHashes.Contains(sprite.SpriteHash))
                    {
                        anyRemaining = true;
                        break;
                    }
                }
                if (!anyRemaining || engine.RemainingUnknownCount == 0) break;
            }
        }

        private IReadOnlyDictionary<ulong, string> GetTextureHashToDirectoryIndex()
        {
            return Corpus.GetOrCreate("texture-hash-to-dir-index", knownPaths =>
            {
                var dict = new Dictionary<ulong, string>();
                foreach (string path in knownPaths)
                {
                    if (path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    {
                        string norm = PathUtils.NormalizePath(path);
                        string dir = PathUtils.NormalizeSeparators(Path.GetDirectoryName(norm));
                        if (!string.IsNullOrEmpty(dir))
                            dict[XxHash64Ext.Hash(norm)] = dir;
                    }
                }
                return dict;
            });
        }

        private IReadOnlyList<string> GetAllKnownAtlasDirectories()
        {
            return Corpus.GetOrCreate("all-known-atlas-directories", knownPaths =>
            {
                var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in knownPaths)
                {
                    if (path.EndsWith("atlas_info.bin", StringComparison.OrdinalIgnoreCase))
                    {
                        string dir = Path.GetDirectoryName(PathUtils.NormalizePath(path));
                        if (!string.IsNullOrEmpty(dir))
                            dirs.Add(PathUtils.NormalizeSeparators(dir));
                    }
                }
                return dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        private IReadOnlyList<string> GetAutoAtlasCandidatePatterns()
        {
            return Corpus.GetOrCreate("autoatlas-candidate-patterns-v4", knownPaths =>
            {
                var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string path in knownPaths)
                {
                    string norm = PathUtils.NormalizePath(path);

                    // Extract relative filenames from any known autoatlas folders
                    int autoAtlasIdx = norm.IndexOf("/autoatlas/", StringComparison.OrdinalIgnoreCase);
                    if (autoAtlasIdx >= 0)
                    {
                        string sub = norm[(autoAtlasIdx + "/autoatlas/".Length)..];
                        int firstSlash = sub.IndexOf('/');
                        if (firstSlash >= 0 && firstSlash < sub.Length - 1)
                        {
                            string relFile = sub[(firstSlash + 1)..];
                            if (!string.IsNullOrWhiteSpace(relFile) && !relFile.StartsWith("atlas_", StringComparison.OrdinalIgnoreCase))
                                patterns.Add(relFile);
                        }
                        else
                        {
                            string fileName = Path.GetFileName(norm);
                            if (!string.IsNullOrWhiteSpace(fileName) && !fileName.StartsWith("atlas_", StringComparison.OrdinalIgnoreCase))
                                patterns.Add(fileName);
                        }
                    }
                    else if (norm.Contains("/icons2d/", StringComparison.OrdinalIgnoreCase) ||
                             norm.StartsWith("ux/", StringComparison.OrdinalIgnoreCase) ||
                             norm.StartsWith("clientstates/", StringComparison.OrdinalIgnoreCase))
                    {
                        string fileName = Path.GetFileName(norm);
                        if (!string.IsNullOrWhiteSpace(fileName) && !fileName.StartsWith("atlas_", StringComparison.OrdinalIgnoreCase))
                        {
                            patterns.Add(fileName);
                            string stem = Path.GetFileNameWithoutExtension(fileName);
                            if (!string.IsNullOrWhiteSpace(stem) && stem.Length <= 100)
                            {
                                patterns.Add($"{stem}.png");
                                patterns.Add($"{stem}.dds");
                                patterns.Add($"{stem}.tex");
                            }
                        }
                    }
                }

                return patterns.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        private IEnumerable<string> EnumerateAnimationPaths(
            string character,
            string skin,
            bool includeThemeLayout,
            IReadOnlyList<AnimationFileLink> links,
            IReadOnlyCollection<ulong> unknownHashes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = links
                .Where(link => link.PathHash != 0 && unknownHashes.Contains(link.PathHash))
                .Select(link => link.PathHash)
                .ToHashSet();
            if (remaining.Count == 0) yield break;

            var attemptedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var (prefixes, suffixes) = GetDynamicAnimationAffixes(cancellationToken);

            foreach (string name in EnumerateAnimationNameCandidates(character, links, remaining, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!attemptedNames.Add(name)) continue;
                foreach (string path in MatchAnimationVariants(name, character, skin, remaining, prefixes, suffixes, includeThemeLayout))
                    yield return path;
                if (remaining.Count == 0) yield break;
            }

            if (remaining.Count == 0) yield break;

            IReadOnlyList<string> sourceNames = GetAnimationNames(character, cancellationToken);

            foreach (HashGuessCandidate candidate in GenerateNumberCandidates(
                         sourceNames.Where(name => name.Any(char.IsDigit)).Select(name => $"animations/{name}"),
                         numberLimit: 360,
                         candidateBudget: int.MaxValue,
                         digits: null,
                         inferDigits: false,
                         includeCommonPadding: false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = GetBasename(candidate.Path);
                if (!attemptedNames.Add(name)) continue;
                foreach (string path in MatchAnimationVariants(name, character, skin, remaining, DefaultPrefixModifiers, DefaultSuffixModifiers, includeThemeLayout))
                    yield return path;
                if (remaining.Count == 0) yield break;
            }
        }

        private IEnumerable<string> EnumerateAnimationNameCandidates(
            string character,
            IReadOnlyList<AnimationFileLink> links,
            IReadOnlySet<ulong> targetHashes,
            CancellationToken cancellationToken)
        {
            var namedLinks = links
                .Where(link => targetHashes.Contains(link.PathHash) && link.NameHash != 0)
                .ToList();
            if (namedLinks.Count == 0) yield break;

            foreach (AnimationFileLink link in namedLinks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string resolved = _resolveBinHash?.Invoke(link.NameHash);
                if (IsAnimationStem(resolved)) yield return resolved;
            }
            if (targetHashes.Count == 0) yield break;

            var nameHashes = namedLinks
                .Where(link => targetHashes.Contains(link.PathHash))
                .Select(link => link.NameHash)
                .ToHashSet();
            if (nameHashes.Count == 0) yield break;

            IReadOnlyDictionary<uint, List<string>> namesByHash = GetAnimationNameIndex(cancellationToken);
            foreach (AnimationFileLink link in namedLinks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!targetHashes.Contains(link.PathHash) || !namesByHash.TryGetValue(link.NameHash, out List<string> names)) continue;
                foreach (string name in names)
                    yield return name;
            }
            if (targetHashes.Count == 0) yield break;

            IReadOnlyList<string> sourceNames = GetAnimationNames(character, cancellationToken);
            HashSet<string> prefixes = GetReusableAnimationPrefixes(sourceNames);

            foreach (string name in sourceNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string stem = name.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
                if (nameHashes.Contains(Fnv1a.HashLower(stem))) yield return stem;
                foreach (string prefix in prefixes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string prefixed = prefix + "_" + stem;
                    if (nameHashes.Contains(Fnv1a.HashLower(prefixed))) yield return prefixed;
                }
            }

        }

        private static HashSet<string> GetReusableAnimationPrefixes(IEnumerable<string> names)
        {
            var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names)
            for (int separator = name.IndexOf('_'); separator > 0; separator = name.IndexOf('_', separator + 1))
            {
                string prefix = name[..separator];
                if (prefix.Length <= 24 && prefix.All(character => char.IsLetterOrDigit(character) || character == '_'))
                    prefixes.Add(prefix);
            }
            return prefixes;
        }

        private static bool IsAnimationStem(string value) =>
            !string.IsNullOrWhiteSpace(value) &&
            !value.Contains('/') &&
            !value.Contains('\\') &&
            !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

        private IReadOnlyList<string> GetAnimationNames(string character, CancellationToken cancellationToken)
        {
            return Corpus.GetOrCreate(
                $"animation-names/{character.ToLowerInvariant()}",
                paths => BuildAnimationNames(paths, character, cancellationToken));
        }

        private static IReadOnlyList<string> BuildAnimationNames(
            IReadOnlyList<string> knownPaths,
            string character,
            CancellationToken cancellationToken)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var knownAnimationPathRegex = new Regex(
                @"^(?:assets|data)/characters/(?<character>[^/]+)/skins/(?<skin>[^/]+)/animations/[^/]+\.anm$",
                RegexOptions.IgnoreCase);
            for (int pathIndex = 0; pathIndex < knownPaths.Count; pathIndex++)
            {
                if ((pathIndex & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
                string path = knownPaths[pathIndex];
                if (!path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase)) continue;
                Match context = knownAnimationPathRegex.Match(PathUtils.NormalizePath(path));
                if (!context.Success || !context.Groups["character"].Value.Equals(character, StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = GetBasename(path);
                if (name.Length > 0) names.Add(name);
            }

            return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private (IReadOnlyList<string> Prefixes, IReadOnlyList<string> Suffixes) GetDynamicAnimationAffixes(CancellationToken cancellationToken)
        {
            return Corpus.GetOrCreate("dynamic-animation-affixes", knownPaths =>
            {
                var prefixes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var suffixes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                for (int pathIndex = 0; pathIndex < knownPaths.Count; pathIndex++)
                {
                    if ((pathIndex & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
                    string path = knownPaths[pathIndex];
                    if (!path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase)) continue;
                    string stem = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                    int first = stem.IndexOf('_');
                    if (first > 0 && first < stem.Length - 1 && path.Contains($"/{stem[..first]}/", StringComparison.OrdinalIgnoreCase))
                        stem = stem[(first + 1)..];

                    first = stem.IndexOf('_');
                    if (first > 0 && first <= 16)
                        prefixes[stem[..(first + 1)]] = prefixes.GetValueOrDefault(stem[..(first + 1)]) + 1;

                    int last = stem.LastIndexOf('_');
                    if (last >= 0 && last < stem.Length - 1 && (stem.Length - last) <= 16)
                        suffixes[stem[last..]] = suffixes.GetValueOrDefault(stem[last..]) + 1;
                }

                static IReadOnlyList<string> Top(Dictionary<string, int> dict) =>
                    new[] { "" }.Concat(dict.OrderByDescending(kv => kv.Value).Take(10).Select(kv => kv.Key)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                return (Top(prefixes), Top(suffixes));
            });
        }

        private IEnumerable<string> MatchAnimationVariants(
            string name,
            string character,
            string skin,
            ISet<ulong> remaining,
            IReadOnlyList<string> prefixes = null,
            IReadOnlyList<string> suffixes = null,
            bool includeThemeLayout = false)
        {
            foreach (string path in EnumerateAnimationNameVariants(character, skin, name, prefixes, suffixes, includeThemeLayout))
            {
                if (remaining.Remove(XxHash64Ext.Hash(path)))
                    yield return path;
            }

            string converted = Regex.Replace(name, @"skin\d+", skin, RegexOptions.IgnoreCase);
            if (converted.Equals(name, StringComparison.OrdinalIgnoreCase)) yield break;
            foreach (string path in EnumerateAnimationNameVariants(character, skin, converted, prefixes, suffixes, includeThemeLayout))
                if (remaining.Remove(XxHash64Ext.Hash(path)))
                    yield return path;
        }

        private static IEnumerable<string> EnumerateAnimationNameVariants(
            string character,
            string skin,
            string name,
            IReadOnlyList<string> prefixModifiers = null,
            IReadOnlyList<string> suffixModifiers = null,
            bool includeThemeLayout = false)
        {
            string stem = name.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            if (string.IsNullOrWhiteSpace(stem) || stem.Contains('/') || stem.Contains('\\')) yield break;
            stem = stem.ToLowerInvariant();

            string paddedSkin = skin.Length == 5 && skin.StartsWith("skin", StringComparison.OrdinalIgnoreCase) && char.IsDigit(skin[4])
                ? "skin0" + skin[4..]
                : skin.Length == 6 && skin.StartsWith("skin0", StringComparison.OrdinalIgnoreCase) && char.IsDigit(skin[5])
                    ? "skin" + skin[5..]
                    : skin;
            string[] skinsToTry = string.Equals(skin, paddedSkin, StringComparison.OrdinalIgnoreCase) ? new[] { skin } : new[] { skin, paddedSkin };

            foreach (string sk in skinsToTry)
            foreach (string pre in prefixModifiers ?? DefaultPrefixModifiers)
            foreach (string suf in suffixModifiers ?? DefaultSuffixModifiers)
            foreach (string s in ExpandAnimationStemVariants(pre + stem + suf))
            {
                foreach (string root in AnimationRootPrefixes)
                {
                    if (includeThemeLayout)
                    {
                        yield return $"{root}/characters/{character}/themes/{sk}/animations/{s}.anm";
                        yield return $"{root}/characters/{character}/themes/{sk}/animations/{character}_{s}.anm";
                        yield return $"{root}/characters/{character}/themes/{sk}/animations/{character}_{sk}_{s}.anm";
                        yield return $"{root}/characters/{character}/themes/{sk}/animations/{sk}_{s}.anm";
                    }
                    else
                    {
                        yield return $"{root}/characters/{character}/skins/{sk}/animations/{s}.anm";
                        yield return $"{root}/characters/{character}/skins/{sk}/animations/{character}_{s}.anm";
                        yield return $"{root}/characters/{character}/skins/{sk}/animations/{character}_{sk}_{s}.anm";
                        yield return $"{root}/characters/{character}/skins/{sk}/animations/{sk}_{s}.anm";
                        if (character.StartsWith("jade_", StringComparison.OrdinalIgnoreCase))
                        {
                            string baseCharacter = character[5..];
                            yield return $"{root}/characters/{character}/skins/{sk}/animations/{baseCharacter}_{s}.anm";
                            yield return $"{root}/characters/{character}/skins/{sk}/animations/{baseCharacter}_{sk}_{s}.anm";
                        }
                    }
                }
            }
        }

        private static IEnumerable<string> ExpandAnimationStemVariants(string stem)
        {
            yield return stem;
            if (stem.Contains("variant", StringComparison.OrdinalIgnoreCase))
                yield return stem.Replace("variant", "varient", StringComparison.OrdinalIgnoreCase);
            if (stem.Contains("spawn", StringComparison.OrdinalIgnoreCase))
                yield return stem.Replace("spawn", "spwan", StringComparison.OrdinalIgnoreCase);
            if (stem.Contains("_in", StringComparison.OrdinalIgnoreCase))
                yield return stem.Replace("_in", "in", StringComparison.OrdinalIgnoreCase);
            if (stem.Contains("_out", StringComparison.OrdinalIgnoreCase))
                yield return stem.Replace("_out", "out", StringComparison.OrdinalIgnoreCase);
            if (stem.Contains("_cycle", StringComparison.OrdinalIgnoreCase))
                yield return stem.Replace("_cycle", "cycle", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsThemeAnimationContext(string character, string container)
        {
            return character.StartsWith("pet", StringComparison.OrdinalIgnoreCase) &&
                   !container.Equals("root", StringComparison.OrdinalIgnoreCase) &&
                   !container.Equals("shared", StringComparison.OrdinalIgnoreCase) &&
                   !(container.StartsWith("skin", StringComparison.OrdinalIgnoreCase) &&
                     container.Length > 4 && container.Skip(4).All(char.IsDigit));
        }

        private static IEnumerable<string> OrderAnimationContainers(string sourceContainer, IEnumerable<string> containers)
        {
            int sourceNumber = -1;
            Match sourceMatch = Regex.Match(sourceContainer ?? string.Empty, @"^skin0*(\d+)$", RegexOptions.IgnoreCase);
            if (sourceMatch.Success) int.TryParse(sourceMatch.Groups[1].Value, out sourceNumber);

            return containers
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(container => AnimationContainerDistance(container))
                .ThenBy(container => container, StringComparer.OrdinalIgnoreCase);

            int AnimationContainerDistance(string container)
            {
                if (sourceNumber < 0) return int.MaxValue;
                Match match = Regex.Match(container, @"^skin0*(\d+)$", RegexOptions.IgnoreCase);
                return match.Success && int.TryParse(match.Groups[1].Value, out int number)
                    ? Math.Abs(number - sourceNumber)
                    : int.MaxValue;
            }
        }

        private static readonly string[] DefaultPrefixModifiers = { "" };
        private static readonly string[] DefaultSuffixModifiers = { "" };

        private static readonly string[] AnimationRootPrefixes = { "assets", "data" };

        private static string GetBasename(string path)
        {
            int separator = path.LastIndexOf('/');
            return separator >= 0 ? path[(separator + 1)..] : path;
        }

        private static IEnumerable<AnimationFileLink> EnumerateAnimationFileLinks(BinTree tree)
        {
            uint clipDataMapNameHash = Fnv1a.HashLower("mClipDataMap");
            uint animationFilePathNameHash = Fnv1a.HashLower("mAnimationFilePath");

            var roots = tree.Objects.Values.SelectMany(obj => obj.Properties.Values)
                .Concat(tree.DataOverrides.Select(ovr => ovr.Property));
            var namedPathHashes = new HashSet<ulong>();
            var namedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (BinTreeMap map in roots
                         .SelectMany(root => FindProperties(root, clipDataMapNameHash))
                         .OfType<BinTreeMap>())
            foreach (var pair in map)
            {
                uint nameHash = pair.Key is BinTreeHash hash ? hash.Value : 0;
                foreach (BinTreeProperty path in FindProperties(pair.Value, animationFilePathNameHash).Take(1))
                {
                    if (path is BinTreeWadChunkLink link && link.Value != 0)
                    {
                        namedPathHashes.Add(link.Value);
                        yield return new AnimationFileLink(nameHash, link.Value, null);
                    }
                    else if (path is BinTreeString text && !string.IsNullOrWhiteSpace(text.Value))
                    {
                        string normalized = PathUtils.NormalizePath(text.Value);
                        namedPaths.Add(normalized);
                        yield return new AnimationFileLink(nameHash, 0, normalized);
                    }
                }
            }

            // Some current hash-only BINs store AnimationClip records outside
            // mClipDataMap. Keep the same property evidence, but do not turn
            // every arbitrary WadChunkLink into an animation candidate.
            foreach (BinTreeProperty root in roots)
            foreach (BinTreeProperty path in FindProperties(root, animationFilePathNameHash))
            {
                if (path is BinTreeWadChunkLink link && link.Value != 0 &&
                    !namedPathHashes.Contains(link.Value))
                    yield return new AnimationFileLink(0, link.Value, null);
                else if (path is BinTreeString text && !string.IsNullOrWhiteSpace(text.Value) &&
                         !namedPaths.Contains(PathUtils.NormalizePath(text.Value)))
                    yield return new AnimationFileLink(0, 0, PathUtils.NormalizePath(text.Value));
            }
        }

        private static IEnumerable<BinTreeProperty> FindProperties(BinTreeProperty property, uint nameHash)
        {
            if (property == null) yield break;
            if (property.NameHash == nameHash)
            {
                yield return property;
                yield break;
            }

            IEnumerable<BinTreeProperty> children = property switch
            {
                BinTreeStruct structure => structure.Properties.Values,
                BinTreeOptional optional when optional.Value != null => new[] { optional.Value },
                BinTreeContainer container => container.Elements,
                BinTreeMap map => map.SelectMany(pair => new[] { pair.Key, pair.Value }),
                _ => Array.Empty<BinTreeProperty>()
            };
            foreach (BinTreeProperty child in children)
            foreach (BinTreeProperty match in FindProperties(child, nameHash))
                yield return match;
        }

        internal int GrepFile(
            HashGuessEngine engine,
            string path = null,
            byte[] data = null,
            string source = "GAME grep file")
        {
            if (!string.IsNullOrWhiteSpace(path)) data = File.ReadAllBytes(path);
            else if (data == null) throw new ArgumentException("Either path or data must be provided.");
            int checkedCandidates = 0;
            foreach (HashGuessCandidate candidate in GrepFile(data, CancellationToken.None))
            {
                Check(engine, candidate.Path, candidate.Strategy, string.IsNullOrWhiteSpace(path) ? source : path);
                checkedCandidates++;
                if (engine.RemainingUnknownCount == 0) break;
            }
            return checkedCandidates;
        }

        private static IEnumerable<HashGuessCandidate> ExtractLuaManifestCandidates(
            ArraySegment<byte> data,
            CancellationToken cancellationToken)
        {
            string[] luaExtensions = { "luabin64", "preload" };
            string[] luaCharacterPrefixes = { "", "spells/", "scripts/", "npcscripts", "npcscripts/" };
            string[] sharedScriptDirectories =
            {
                "data/spells", "data/spells/modules", "data/scripts", "data/shared/scripts",
                "data/shared/scripts/aicomponents", "data/shared/spells", "data/shared/npcscripts",
                "data/shared/tft/common", "data/shared/tft/items", "data/shared/tft/traits",
                "data/shared/spells/practicetool", "data/items", "data/items/spells",
                "data/items/spells/modules", "data/buildingblocks", "data/shared/gamemodes"
            };
            string[] luaCommonPaths =
            {
                "data/spells", "data/spells/modules", "data/scripts", "data/shared/scripts",
                "data/shared/scripts/aicomponents", "data/shared/spells", "data/shared/npcscripts",
                "data/shared/tft/common", "data/shared/tft/items", "data/shared/tft/traits",
                "data/shared/spells/practicetool", "data/items", "data/items/spells",
                "data/items/spells/modules", "data/buildingblocks", "data/shared/gamemodes",
                "data/shared/spells/cheat"
            };

            using var stream = new MemoryStream(data.Array, data.Offset, data.Count, false);
            using var reader = new BinaryReader(stream, new UTF8Encoding(false, true), true);
            if (stream.Length < 8) yield break;

            reader.ReadBytes(4);
            uint characterCount = ReadManifestCount(reader);
            for (uint characterIndex = 0; characterIndex < characterCount; characterIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string character = ReadManifestString(reader).ToLowerInvariant();
                uint childCount = ReadManifestCount(reader);
                for (uint childIndex = 0; childIndex < childCount; childIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string name = ReadManifestString(reader).ToLowerInvariant();
                    foreach (string prefix in luaCharacterPrefixes)
                    foreach (string extension in luaExtensions)
                        yield return new HashGuessCandidate(
                            $"data/characters/{character}/{prefix}{name}.{extension}",
                            HashGuessStrategy.LuaManifest);
                }
            }

            uint sharedCount = ReadManifestCount(reader);
            var sharedNames = new List<string>((int)Math.Min(sharedCount, 100_000));
            for (uint sharedIndex = 0; sharedIndex < sharedCount; sharedIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sharedNames.Add(ReadManifestString(reader).ToLowerInvariant());
            }

            uint hashCount = ReadManifestCount(reader);
            long hashBytes = checked((long)hashCount * sizeof(ulong));
            if (hashBytes > stream.Length - stream.Position)
                throw new InvalidDataException("Lua manifest hash table exceeds the available data.");

            var hashMap = new Dictionary<ulong, uint>((int)Math.Min(hashCount, 100_000));
            for (uint i = 0; i < hashCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong entry = reader.ReadUInt64();
                uint dirIndex = (uint)(entry & 0x1F);
                ulong xxh3Truncated = entry >> 5;
                hashMap[xxh3Truncated] = dirIndex;
            }

            foreach (string name in sharedNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] nameBytes = Encoding.UTF8.GetBytes(name);
                ulong nameHashTruncated = XxHash3.HashToUInt64(nameBytes) >> 5;
                if (hashMap.TryGetValue(nameHashTruncated, out uint dirIndex) && dirIndex < (uint)sharedScriptDirectories.Length)
                {
                    string dir = sharedScriptDirectories[dirIndex];
                    foreach (string extension in luaExtensions)
                    {
                        yield return new HashGuessCandidate($"{dir}/{name}.{extension}", HashGuessStrategy.LuaManifest);
                    }
                }
                else
                {
                    // Fallback for stripped scripts (Cheat* or Map scripts)
                    if (name.StartsWith("cheat", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string extension in luaExtensions)
                            yield return new HashGuessCandidate($"data/shared/spells/cheat/{name}.{extension}", HashGuessStrategy.LuaManifest);
                    }
                    else
                    {
                        foreach (string prefix in luaCommonPaths)
                        foreach (string extension in luaExtensions)
                            yield return new HashGuessCandidate($"{prefix}/{name}.{extension}", HashGuessStrategy.LuaManifest);

                        for (int map = 0; map < 1500; map++)
                        foreach (string prefix in new[] { string.Empty, "mutators/" })
                            yield return new HashGuessCandidate(
                                $"levels/map{map}/scripts/{prefix}{name}.luabin64",
                                HashGuessStrategy.LuaManifest);
                    }
                }
            }
        }

        private static uint ReadManifestCount(BinaryReader reader)
        {
            const uint maximumCount = 1_000_000;
            if (reader.BaseStream.Length - reader.BaseStream.Position < sizeof(uint))
                throw new EndOfStreamException("Unexpected end of Lua manifest count.");
            uint count = reader.ReadUInt32();
            if (count > maximumCount)
                throw new InvalidDataException($"Lua manifest count {count} exceeds the safety limit.");
            return count;
        }

        private static string ReadManifestString(BinaryReader reader)
        {
            const uint maximumLength = 16_384;
            uint length = ReadManifestCount(reader);
            if (length > maximumLength || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException($"Lua manifest string length {length} is invalid.");
            byte[] bytes = reader.ReadBytes((int)length);
            return new UTF8Encoding(false, true).GetString(bytes);
        }

        private static IEnumerable<HashGuessCandidate> GrepFile(
            ArraySegment<byte> data,
            CancellationToken cancellationToken)
        {
            if (data.Array is null || data.Count == 0) yield break;

            string text = Encoding.Latin1.GetString(data.Array, data.Offset, data.Count);
            var paths = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match match in Regex.Matches(text, @"(?:ASSETS|Common|DATA|DATA_SOON|DATA_Soon|Gameplay|Global|LEVELS|Loadouts|UX|UIAutoAtlas)/[0-9a-zA-Z_. /-]+"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rawPath = match.Value;
                string path = rawPath.ToLowerInvariant().Replace("data_soon/", "data/", StringComparison.Ordinal);
                paths.Add(path);

                int pos = match.Index;
                if (pos >= 2)
                {
                    int n = ByteAt(data, pos - 2) | (ByteAt(data, pos - 1) << 8);
                    if (n == 0 && pos >= 4)
                    {
                        n = ByteAt(data, pos - 4) | (ByteAt(data, pos - 3) << 8) |
                            (ByteAt(data, pos - 2) << 16) | (ByteAt(data, pos - 1) << 24);
                    }

                    if (n > 0 && n < rawPath.Length)
                    {
                        string shortened = rawPath[..n].ToLowerInvariant().Replace("data_soon/", "data/", StringComparison.Ordinal);
                        paths.Add(shortened);
                    }
                }
            }

            var emitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (HashGuessCandidate candidate in ExpandGrepFilePath(path, HashGuessStrategy.EmbeddedPathGrep))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (emitted.Add(candidate.Path)) yield return candidate;
                }
            }
        }

        private static IEnumerable<HashGuessCandidate> ExpandGrepFilePath(string path, HashGuessStrategy strategy)
        {
            if (path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            {
                string prefix = path[..^4];
                yield return new HashGuessCandidate(path, strategy);
                yield return new HashGuessCandidate(prefix + ".luabin", HashGuessStrategy.LuaVariant);
                yield return new HashGuessCandidate(prefix + ".luabin64", HashGuessStrategy.LuaVariant);
                yield return new HashGuessCandidate(prefix + ".preload", HashGuessStrategy.LuaVariant);
                yield break;
            }

            yield return new HashGuessCandidate(path, strategy);
        }

        private static bool IsAscii(string value)
        {
            foreach (char character in value)
                if (character > 0x7F) return false;
            return true;
        }

        private static byte ByteAt(ArraySegment<byte> data, int index) => data.Array[data.Offset + index];

        private static IEnumerable<IReadOnlyList<T>> GetCombinations<T>(IReadOnlyList<T> values, int length)
        {
            if (length <= 0 || values.Count < length) yield break;
            if (length == 1)
            {
                for (int i = 0; i < values.Count; i++)
                    yield return new[] { values[i] };
                yield break;
            }

            int[] indices = new int[length];
            for (int i = 0; i < length; i++)
                indices[i] = i;

            while (true)
            {
                T[] result = new T[length];
                for (int i = 0; i < length; i++)
                    result[i] = values[indices[i]];
                yield return result;

                int pos = length - 1;
                while (pos >= 0 && indices[pos] == values.Count - length + pos)
                    pos--;

                if (pos < 0) break;

                indices[pos]++;
                for (int i = pos + 1; i < length; i++)
                    indices[i] = indices[i - 1] + 1;
            }
        }

        private static IEnumerable<IEnumerable<T>> GetPermutations<T>(IReadOnlyList<T> values, int length)
        {
            if (length == 1) return values.Select(value => new[] { value }.AsEnumerable());
            return values.SelectMany(
                (value, index) => GetPermutations(values.Where((_, candidateIndex) => candidateIndex != index).ToList(), length - 1),
                (value, tail) => new[] { value }.Concat(tail));
        }
    }
}
