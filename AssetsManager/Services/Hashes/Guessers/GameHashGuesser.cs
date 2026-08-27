using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Linq;
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
        private static readonly Regex PreloadNameRegex = new("Name=\\\"([^\\\"]+)\\\"", RegexOptions.Compiled);
        private static readonly Regex ShaderIncludeRegex = new("#include \\\"([^\\\"]+)\\\"", RegexOptions.Compiled);
        private static readonly string[] Locales = { "ar_ae", "ar_eg", "cs_cz", "de_de", "el_gr", "en_au", "en_gb", "en_ph", "en_pl", "en_sg", "en_us", "es_ar", "es_es", "es_mx", "fr_fr", "hu_hu", "id_id", "it_it", "ja_jp", "ko_kr", "ms_my", "pl_pl", "pt_br", "ro_ro", "ru_ru", "th_th", "tr_tr", "vi_vn", "vn_vn", "zh_cn", "zh_my", "zh_tw" };
        private static readonly Regex LocaleRegex = new($"({string.Join("|", Locales)})", RegexOptions.Compiled);
        private static readonly string[] ShaderExtensions = { ".ps_2_0", ".ps_3_0", ".vs_2_0", ".vs_3_0", ".ps", ".vs" };
        private static readonly string[] ShaderVariants = { ".dx11", ".dx9", ".dx9sm3", ".glsl", ".metal", "-dx11", "-metal" };
        private static readonly int[] EmbeddedShaderIndices = Enumerable.Range(0, 32).ToArray();
        private static readonly Regex ShaderPathRegex = new(
            @".*\.[pv]s(?:_[23]_0|(?=$|[.-]))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly ConcurrentDictionary<string, byte> _scannedWadCharacters = new(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] LuaExtensions = { "luabin64", "preload" };
        private static readonly string[] LuaCharacterPrefixes = { "", "spells/", "scripts/", "npcscripts", "npcscripts/" };
        private static readonly string[] LuaCommonPaths =
        {
            "data/spells", "data/spells/modules", "data/scripts", "data/shared/scripts",
            "data/shared/scripts/aicomponents", "data/shared/spells", "data/shared/npcscripts",
            "data/shared/tft/common", "data/shared/tft/items", "data/shared/tft/traits",
            "data/shared/spells/practicetool", "data/items", "data/items/spells",
            "data/items/spells/modules", "data/buildingblocks", "data/shared/gamemodes",
            "data/shared/spells/cheat"
        };
        private static readonly string[] SharedScriptDirectories =
        {
            "data/spells", "data/spells/modules", "data/scripts", "data/shared/scripts",
            "data/shared/scripts/aicomponents", "data/shared/spells", "data/shared/npcscripts",
            "data/shared/tft/common", "data/shared/tft/items", "data/shared/tft/traits",
            "data/shared/spells/practicetool", "data/items", "data/items/spells",
            "data/items/spells/modules", "data/buildingblocks", "data/shared/gamemodes"
        };
        private static readonly byte[][] BinPrefixesA = ToAsciiPrefixes("ASSETS/");
        private static readonly byte[][] BinPrefixesC = ToAsciiPrefixes("Common/");
        private static readonly byte[][] BinPrefixesD = ToAsciiPrefixes("DATA/", "DATA_SOON/", "DATA_Soon/");
        private static readonly byte[][] BinPrefixesG = ToAsciiPrefixes("Gameplay/", "Global/");
        private static readonly byte[][] BinPrefixesL = ToAsciiPrefixes("LEVELS/", "Loadouts/");
        private static readonly byte[][] BinPrefixesU = ToAsciiPrefixes("UX/", "UIAutoAtlas/");
        private static readonly byte[][] WadBinPrefixesA = ToAsciiPrefixes("ASSETS/");
        private static readonly byte[][] WadBinPrefixesC = ToAsciiPrefixes("Characters/", "ClientStates/");
        private static readonly byte[][] WadBinPrefixesD = ToAsciiPrefixes("DATA/");
        private static readonly byte[][] WadBinPrefixesG = ToAsciiPrefixes("Gameplay/");
        private static readonly byte[][] WadBinPrefixesL = ToAsciiPrefixes("Loadouts/");
        private static readonly byte[][] WadBinPrefixesM = ToAsciiPrefixes("Maps/");
        private static readonly byte[][] WadBinPrefixesP = ToAsciiPrefixes("Patching/");
        private static readonly byte[][] WadBinPrefixesS = ToAsciiPrefixes("Shaders/");
        private static readonly Regex AnimationBinPathRegex = new(
            @"^(?:assets|data)/characters/(?<character>[^/]+)/(?:animations/(?<skin>[^/]+)|skins/(?<skin>[^/]+)(?:/animations)?(?:/[^/]+)?)\.bin$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex KnownAnimationPathRegex = new(
            @"^(?:assets|data)/characters/(?<character>[^/]+)/skins/(?<skin>[^/]+)/animations/[^/]+\.anm$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AnimationSkinTokenRegex = new("skin\\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly string[] DottedBinTargetPrefixes =
        {
            "loadouts/companions",
            "loadouts/summoneremotesvfx",
            "loadouts/summoneremotes",
            "loadouts/tftdamageskins",
            "loadouts/tftzoomskins"
        };
        private const int AnimationNumberLimit = 360;
        private const int CustomBinSampleSize = 30_000;
        private const int CustomCharacterDdsSampleSize = 25_000;
        private const int CustomCharacterTexSampleSize = 20_000;
        private const int CustomWordAdditionSampleSize = 20_000;
        private const int CustomFocusedPathSampleSize = 20_000;
        private const int CustomBinCandidateBudget = 100_000_000;
        private const int CustomDataBinCandidateBudget = 100_000_000;
        private const int CustomCharacterDdsCandidateBudget = 100_000_000;
        private const int CustomCharacterTexCandidateBudget = 100_000_000;
        private const int CustomSwordlistCandidateBudget = 100_000_000;
        private const int CustomWordlistCandidateBudget = 100_000_000;
        private const int CustomWordAdditionCandidateBudget = 100_000_000;
        private const int CustomShaderCandidateBudget = 100_000_000;
        private const int SkinGroupCandidateBudget = 100_000_000;
        private const int SuffixSubstitutionCandidateBudget = 100_000_000;
        private const int CharacterSubstitutionCandidateBudget = 100_000_000;
        private const int SkinNumberSubstitutionCandidateBudget = 100_000_000;
        private const int EsportsBannerSingleCandidateBudget = 2_000_000;
        private const int EsportsBannerCompoundCandidateBudget = 10_000_000;
        private const int EsportsBannerDoubleCandidateBudget = 2_000_000;
        private const int EsportsBannerInsertionCandidateBudget = 750_000;
        private const int EsportsBannerDoubleWordLimit = 96;
        private static readonly uint AnimationFilePathNameHash = Fnv1a.HashLower("mAnimationFilePath");
        private static readonly uint ClipDataMapNameHash = Fnv1a.HashLower("mClipDataMap");
        private static readonly HashSet<string> SkippedExtensions = new(StringComparer.Ordinal)
        {
            "dds", "jpg", "png", "tga", "ttf", "otf", "ogg", "webm", "anm", "skl", "skn",
            "scb", "sco", "troybin", "bnk", "wpk", "tex"
        };
        private readonly record struct AnimationFileLink(uint NameHash, ulong PathHash, string Path);

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

        internal override bool ShouldSkip(string extension) => SkippedExtensions.Contains(extension);

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
            base.SubstituteNumbersCore(
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
            var words = Corpus.GetOrCreate("frequency-wordlist", HashGuessEngine.BuildFrequencyWordlist);
            return SubstituteBasenameWordsCore(
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

            IReadOnlyList<string> seedBins = binPaths.Take(CustomBinSampleSize).ToList();
            IReadOnlyList<string> words = binWordlist.Take(CustomBinSampleSize).ToList();
            return SubstituteBasenameWordsCore(
                engine,
                seedBins,
                words,
                oldWordCount: 1,
                newWordCount: 1,
                cancellationToken,
                candidateBudget: CustomBinCandidateBudget,
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

            IReadOnlyList<string> seedDataBins = dataPaths.Take(CustomBinSampleSize).ToList();
            IReadOnlyList<string> words = dataWordlist.Take(CustomBinSampleSize).ToList();
            return SubstituteBasenameWordsCore(
                engine,
                seedDataBins,
                words,
                oldWordCount: 1,
                newWordCount: 1,
                cancellationToken,
                candidateBudget: CustomDataBinCandidateBudget,
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

            IReadOnlyList<string> seedDdsPaths = characterDdsPaths.Take(CustomCharacterDdsSampleSize).ToList();
            IReadOnlyList<string> words = characterDdsWordlist.Take(CustomCharacterDdsSampleSize).ToList();
            return SubstituteBasenameWordsCore(
                engine,
                seedDdsPaths,
                words,
                oldWordCount: 1,
                newWordCount: 1,
                cancellationToken,
                candidateBudget: CustomCharacterDdsCandidateBudget,
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

            IReadOnlyList<string> seedTexPaths = characterTexPaths.Take(CustomCharacterTexSampleSize).ToList();
            IReadOnlyList<string> words = characterTexWordlist.Take(CustomCharacterTexSampleSize).ToList();
            return SubstituteBasenameWordsCore(
                engine,
                seedTexPaths,
                words,
                oldWordCount: 1,
                newWordCount: 1,
                cancellationToken,
                candidateBudget: CustomCharacterTexCandidateBudget,
                source: "GAME Custom: character TEX basename wordlist",
                progress);
        }

        internal int AddCustomBasenameWord(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            Action<int> progress = null)
        {
            IReadOnlyList<string> paths = Corpus.GetOrCreate(
                "custom-word-addition-paths",
                values => values.Take(CustomWordAdditionSampleSize).ToList());
            IReadOnlyList<string> words = Corpus.GetOrCreate(
                "custom-word-addition-wordlist",
                _ => BuildWordlist().Take(CustomWordAdditionSampleSize).ToList());

            int checkedCount = AddBasenameWordCore(
                engine,
                paths,
                words,
                cancellationToken,
                candidateBudget: CustomWordAdditionCandidateBudget);
            progress?.Invoke(checkedCount);
            return checkedCount;
        }

        internal int SubstituteSwordlistBasenameWords(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            Action<int> progress = null)
        {
            IReadOnlyList<string> paths = Corpus.GetOrCreate(
                "custom-focused-wordlist-paths",
                values => values.Take(CustomFocusedPathSampleSize).ToList());
            return SubstituteBasenameWordsCore(
                engine,
                paths,
                BuildSwordlist(),
                oldWordCount: 1,
                newWordCount: 1,
                cancellationToken,
                candidateBudget: CustomSwordlistCandidateBudget,
                source: "GAME Custom: SwordList basename substitution",
                progress);
        }

        internal int SubstituteWordlistBasenameWords(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            Action<int> progress = null)
        {
            IReadOnlyList<string> paths = Corpus.GetOrCreate(
                "custom-focused-wordlist-paths",
                values => values.Take(CustomFocusedPathSampleSize).ToList());
            return SubstituteBasenameWordsCore(
                engine,
                paths,
                BuildWordlist(),
                oldWordCount: 1,
                newWordCount: 1,
                cancellationToken,
                candidateBudget: CustomWordlistCandidateBudget,
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

            if (ShouldRun("game-custom-wordaddition"))
            {
                progress?.Report(engine.CreateProgress(
                    "GAME Custom: word addition", checkedCandidates));
                int progressOffset = checkedCandidates;
                checkedCandidates += AddCustomBasenameWord(
                    engine,
                    cancellationToken,
                    count => progress?.Report(engine.CreateProgress(
                        "GAME Custom: word addition", progressOffset + count)));
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
                checkedCandidates += GuessCustomShaders(
                    engine,
                    cancellationToken,
                    candidateBudget: CustomShaderCandidateBudget,
                    progress: count => progress?.Report(engine.CreateProgress(
                        "GAME Custom: shader vocabulary attack", progressOffset + count)));
            }

            return checkedCandidates;
        }

        internal int GuessCustomShaders(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            int candidateBudget = int.MaxValue,
            Action<int> progress = null)
        {
            if (engine.RemainingUnknownCount == 0 || candidateBudget <= 0) return 0;

            IReadOnlyList<string> shaderDirs = Corpus.GetOrCreate("custom-shader-directories", paths =>
            {
                var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in paths)
                {
                    if (path.StartsWith("assets/shaders/", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("data/shaders/", StringComparison.OrdinalIgnoreCase))
                    {
                        int lastSlash = path.LastIndexOf('/');
                        if (lastSlash > 0)
                        {
                            dirs.Add(path[..(lastSlash + 1)]);
                        }
                    }
                }

                dirs.Add("assets/shaders/");
                dirs.Add("assets/shaders/hlsl/");
                dirs.Add("assets/shaders/hlsl/environment/");
                dirs.Add("assets/shaders/hlsl/enveffectors/");
                dirs.Add("assets/shaders/hlsl/filters/");
                dirs.Add("assets/shaders/hlsl/hud/");
                dirs.Add("assets/shaders/hlsl/ssao/");
                dirs.Add("assets/shaders/hlsl/gamma/");
                dirs.Add("assets/shaders/hlsl/skinnedmesh/");
                dirs.Add("assets/shaders/hlsl/particlesystem/");
                dirs.Add("assets/shaders/hlsl/ui/");
                dirs.Add("assets/shaders/generated/");
                dirs.Add("assets/shaders/generated/shaders/");
                dirs.Add("data/shaders/");
                dirs.Add("data/shaders/hlsl/");
                return dirs.OrderBy(d => d, StringComparer.Ordinal).ToList();
            });

            IReadOnlyList<string> vocabulary = Corpus.GetOrCreate("custom-shader-vocabulary", _ =>
            {
                var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string path in KnownPaths)
                {
                    if (path.StartsWith("assets/shaders/", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("data/shaders/", StringComparison.OrdinalIgnoreCase))
                    {
                        string basename = GetBasename(path);
                        foreach (string token in basename.Split(new[] { '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (token.Length >= 2 && token.Length <= 24 && token.All(char.IsLetterOrDigit))
                            {
                                words.Add(token.ToLowerInvariant());
                            }
                        }
                    }
                }

                string[] graphicsSeed =
                {
                    "ssao", "hbao", "gtao", "sdf", "fxaa", "smaa", "taa", "pbr", "dof", "hdr", "lut",
                    "bloom", "fog", "fow", "env", "effectors", "effector", "simple", "blur", "gauss",
                    "edge", "aware", "composite", "compositor", "decal", "distortion", "outline",
                    "minimap", "hud", "terrain", "water", "clouds", "sky", "particle", "trail",
                    "copy", "blit", "resolve", "downsample", "upsample", "tonemap", "vignette",
                    "depth", "shadow", "mask", "stencil", "normal", "albedo", "specular", "roughness",
                    "metallic", "cubemap", "gamma", "noise", "radial", "gradient", "post", "light",
                    "shared", "uber", "unlit", "lit", "alpha", "blend", "filter", "sample"
                };

                foreach (string seed in graphicsSeed)
                {
                    words.Add(seed.ToLowerInvariant());
                }

                return words.OrderBy(w => w, StringComparer.Ordinal).ToList();
            });

            IReadOnlyList<string> compoundNames = Corpus.GetOrCreate("custom-shader-compound-names", _ =>
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string word in vocabulary)
                {
                    names.Add(word);
                    names.Add($"ps_{word}");
                    names.Add($"vs_{word}");
                }

                foreach (string w1 in vocabulary)
                {
                    foreach (string w2 in vocabulary)
                    {
                        if (w1.Length + w2.Length > 24) continue;
                        names.Add($"{w1}{w2}");
                        names.Add($"{w1}_{w2}");
                        names.Add($"ps_{w1}_{w2}");
                        names.Add($"vs_{w1}_{w2}");
                    }
                }

                return names.OrderBy(n => n, StringComparer.Ordinal).ToList();
            });

            IEnumerable<HashGuessCandidate> GenerateCandidates()
            {
                foreach (string dir in shaderDirs)
                {
                    foreach (string name in compoundNames)
                    {
                        string basePath = $"{dir}{name}";

                        foreach (string ext in ShaderExtensions)
                        {
                            yield return new HashGuessCandidate($"{basePath}{ext}", HashGuessStrategy.ShaderVariant);

                            foreach (string variant in ShaderVariants)
                            {
                                yield return new HashGuessCandidate($"{basePath}{ext}{variant}", HashGuessStrategy.ShaderVariant);

                                for (int index = 0; index <= 15; index++)
                                {
                                    yield return new HashGuessCandidate($"{basePath}{ext}{variant}_{index}", HashGuessStrategy.ShaderVariant);
                                }
                            }
                        }
                    }
                }
            }

            IEnumerable<HashGuessCandidate> candidates = GenerateCandidates();
            if (candidateBudget != int.MaxValue)
            {
                candidates = candidates.Take(candidateBudget);
            }

            return CheckIter(
                engine,
                candidates,
                "GAME Custom: shader vocabulary attack",
                cancellationToken,
                progress);
        }

        internal int AddBasenameWord(HashGuessEngine engine, CancellationToken cancellationToken, int candidateBudget = int.MaxValue)
        {
            var paths = Corpus.GetOrCreate("word-addition-paths", values => values.Where(path =>
                !path.Contains("assets/characters/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("vo/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("sfx/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("skins_skin", StringComparison.OrdinalIgnoreCase)).ToList());
            var words = Corpus.GetOrCreate("frequency-wordlist", HashGuessEngine.BuildFrequencyWordlist);
            return AddBasenameWordCore(
                engine,
                paths,
                words,
                cancellationToken,
                candidateBudget,
                source: "GAME basename word addition");
        }

        internal IEnumerable<HashGuessCandidate> SubstituteCharacter(int candidateBudget = CharacterSubstitutionCandidateBudget) => GenerateCharacterSubstitutionCandidates(candidateBudget);
        internal IEnumerable<HashGuessCandidate> SubstituteSkinNumbers(int candidateBudget = SkinNumberSubstitutionCandidateBudget) => GenerateSkinNumberCandidates(candidateBudget);
        internal IEnumerable<HashGuessCandidate> SubstituteSuffixes(int candidateBudget = int.MaxValue) => GenerateSuffixCandidates(candidateBudget);
        internal int SubstituteLang(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            string source = "Generated locale variant",
            Action<int> progress = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            IReadOnlyList<string> formats = KnownPaths
                .Where(path => LocaleRegex.IsMatch(path))
                .Select(path => LocaleRegex.Replace(path, "{locale}"))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            int checkedCount = 0;
            foreach (string format in ProgressIterator(formats, value => value, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkedCount += CheckIter(
                    engine,
                    Locales.Select(locale => new HashGuessCandidate(
                        format.Replace("{locale}", locale, StringComparison.Ordinal),
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

        internal int GuessCharacterFiles(
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

            int CheckCharacterPaths(IEnumerable<string> paths)
            {
                int remaining = candidateBudget == int.MaxValue ? int.MaxValue : candidateBudget - checkedCount;
                if (remaining <= 0 || engine.RemainingUnknownCount == 0) return 0;
                IEnumerable<HashGuessCandidate> candidatesToCheck = paths.Select(
                    path => new HashGuessCandidate(path, HashGuessStrategy.CharacterTemplate));
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
                    $"data/characters/{character}/themes/root.bin",
                    $"data/characters/{character}/{character}.bin",
                    $"data/characters/{character}/{character}.ddf",
                    $"data/characters/{character}/hud/{character}_circle.dds",
                    $"data/characters/{character}/hud/{character}_square.dds",
                    $"assets/characters/{character}/animations/shared.bin",
                    $"assets/characters/{character}/animations/root.bin",
                    $"assets/characters/{character}/hud/{character}_circle.dds",
                    $"assets/characters/{character}/hud/{character}_square.dds",
                    $"characters/{character}"
                });

                int skinLimit = character.Equals("sightward", StringComparison.OrdinalIgnoreCase) ? 500 : 350;
                checkedCount += CheckCharacterPaths(
                    Enumerable.Range(0, skinLimit).Select(skin =>
                        $"data/characters/{character}/skins/skin{skin}.bin"));
                checkedCount += CheckCharacterPaths(
                    Enumerable.Range(1, 9).Select(skin =>
                        $"data/characters/{character}/skins/skin{skin:D2}.bin"));
                checkedCount += CheckCharacterPaths(
                    Enumerable.Range(0, skinLimit).Select(skin =>
                        $"data/characters/{character}/animations/skin{skin}.bin"));
                checkedCount += CheckCharacterPaths(
                    Enumerable.Range(1, 9).Select(skin =>
                        $"data/characters/{character}/animations/skin{skin:D2}.bin"));
                if (character.StartsWith("pet", StringComparison.OrdinalIgnoreCase))
                {
                    checkedCount += CheckCharacterPaths(
                        Enumerable.Range(0, 10).Select(tier =>
                            $"data/characters/{character}/tiers/tier{tier}.bin"));

                    var themes = GetDynamicPetThemeNames(cancellationToken);
                    checkedCount += CheckCharacterPaths(themes.SelectMany(theme => new[]
                    {
                        $"data/characters/{character}/themes/{theme}/root.bin",
                        $"data/characters/{character}/themes/{theme}/tier1.bin",
                        $"data/characters/{character}/themes/{theme}/tier2.bin",
                        $"data/characters/{character}/themes/{theme}/tier3.bin",
                        $"data/characters/{character}/themes/{theme}/tier0.bin",
                        $"assets/characters/{character}/themes/{theme}/root.bin",
                        $"assets/characters/{character}/themes/{theme}/tier1.bin",
                        $"assets/characters/{character}/themes/{theme}/tier2.bin",
                        $"assets/characters/{character}/themes/{theme}/tier3.bin"
                    }));
                }

                checkedCount += CheckCharacterPaths(EnumerateCharacterAssetPaths(character, skinLimit));

                if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) break;
            }

            return checkedCount;
        }

        private static readonly string[] CharacterTextureSamplers = new[]
        {
            "_tx_cm", "_recall_tx_cm", "_weapon_tx_cm", "_body_tx_cm",
            "_tx_mask", "_tx_rm", "_tx_em", "_tx_gm",
            "_tx_outline", "_tx_coin", "_tx_noise", "_fx_mask", "_base_tx_cm"
        };

        private static readonly string[] CharacterTexturePrefixes = new[] { "", "2x_", "4x_" };
        private static readonly string[] CharacterTextureExtensions = new[] { ".tex", ".dds", ".project_jade.tex" };
        private static readonly string[] LoadscreenSuffixes = new[] { "", "_le" };

        private static readonly string[] DefaultUniversalAnimationActions = new[]
        {
            "idle", "idle1", "idle2", "idle3", "idle4", "idle_in",
            "run", "run_fast", "run_base", "walk",
            "attack1", "attack2", "attack3", "attack4", "crit",
            "spell", "spell1", "spell2", "spell3", "spell4",
            "spell1a", "spell2a", "spell3a", "spell4a",
            "spell1b", "spell2b", "spell3b", "spell4b",
            "spell1c", "spell2c", "spell3c", "spell4c",
            "spell1_cast", "spell2_cast", "spell3_cast", "spell4_cast",
            "spell1_windup", "spell2_windup", "spell3_windup", "spell4_windup",
            "spell1_loop", "spell2_loop", "spell3_loop", "spell4_loop",
            "spell1_winddown", "spell2_winddown", "spell3_winddown", "spell4_winddown",
            "death", "death2", "recall", "recall_windup",
            "dance", "taunt", "laugh", "joke",
            "channel", "channel_windup", "channel_loop", "channel_winddown",
            "celebration", "spawn", "homeguard", "respawn"
        };

        private IReadOnlyList<string> GetCharacterAnimationActions(string character)
        {
            return Corpus.GetOrCreate($"champion-animation-actions/{character.ToLowerInvariant()}", knownPaths =>
            {
                var actions = new HashSet<string>(DefaultUniversalAnimationActions, StringComparer.OrdinalIgnoreCase);

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
                }

                return actions.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        private IEnumerable<string> EnumerateCharacterAssetPaths(string character, int skinLimit)
        {
            var actions = GetCharacterAnimationActions(character);
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

            yield return $"assets/characters/{character}/hud/{character}_square.tex";
            yield return $"assets/characters/{character}/hud/{character}_circle.tex";
            yield return $"assets/characters/{character}/hud/{character}_square.dds";
            yield return $"assets/characters/{character}/hud/{character}_circle.dds";

            foreach (string prefix in CharacterTexturePrefixes)
            {
                foreach (string sampler in CharacterTextureSamplers)
                {
                    foreach (string ext in CharacterTextureExtensions)
                    {
                        yield return $"assets/characters/{character}/skins/base/{prefix}{character}_base{sampler}{ext}";
                        yield return $"assets/characters/{character}/skins/base/{prefix}{character}{sampler}{ext}";
                    }
                }
            }

            foreach (string action in actions)
            {
                yield return $"assets/characters/{character}/skins/base/animations/{character}_{action}.anm";
                yield return $"assets/characters/{character}/skins/base/animations/{action}.anm";
                if (baseChar != null)
                {
                    yield return $"assets/characters/{character}/skins/base/animations/{baseChar}_{action}.anm";
                }
            }

            foreach (string ext in CharacterTextureExtensions)
            {
                foreach (string suffix in LoadscreenSuffixes)
                {
                    yield return $"assets/characters/{character}/skins/base/{character}_loadscreen{suffix}{ext}";
                    yield return $"assets/characters/{character}/skins/base/{character}loadscreen{suffix}{ext}";
                    yield return $"assets/characters/{character}/skins/base/{character}_loadscreen_0{suffix}{ext}";
                    yield return $"assets/characters/{character}/skins/base/{character}loadscreen_0{suffix}{ext}";
                }
            }

            var skinNumbers = Enumerable.Range(0, Math.Min(skinLimit, 120))
                .Select(s => s.ToString(CultureInfo.InvariantCulture))
                .Concat(Enumerable.Range(1, 9).Select(s => s.ToString("D2", CultureInfo.InvariantCulture)))
                .Concat(Enumerable.Range(300, 51).Select(s => s.ToString(CultureInfo.InvariantCulture)))
                .Concat(Enumerable.Range(500, 51).Select(s => s.ToString(CultureInfo.InvariantCulture)))
                .Distinct(StringComparer.Ordinal);

            foreach (string skin in skinNumbers)
            {
                string skinTag = $"skin{skin}";

                yield return $"assets/characters/{character}/skins/{skinTag}/{character}_{skinTag}.skn";
                yield return $"assets/characters/{character}/skins/{skinTag}/{character}_{skinTag}.skl";
                yield return $"assets/characters/{character}/skins/{skinTag}/{character}.skn";
                yield return $"assets/characters/{character}/skins/{skinTag}/{character}.skl";

                foreach (string prefix in CharacterTexturePrefixes)
                {
                    foreach (string sampler in CharacterTextureSamplers)
                    {
                        foreach (string ext in CharacterTextureExtensions)
                        {
                            yield return $"assets/characters/{character}/skins/{skinTag}/{prefix}{character}_{skinTag}{sampler}{ext}";
                            yield return $"assets/characters/{character}/skins/{skinTag}/{prefix}{character}{sampler}{ext}";
                        }
                    }
                }

                foreach (string ext in CharacterTextureExtensions)
                {
                    foreach (string suffix in LoadscreenSuffixes)
                    {
                        yield return $"assets/characters/{character}/skins/{skinTag}/{character}_loadscreen_{skin}{suffix}{ext}";
                        yield return $"assets/characters/{character}/skins/{skinTag}/{character}loadscreen_{skin}{suffix}{ext}";
                        yield return $"assets/characters/{character}/skins/{skinTag}/{character}_{skinTag}_loadscreen{suffix}{ext}";
                        yield return $"assets/characters/{character}/skins/{skinTag}/{character}{skinTag}_loadscreen{suffix}{ext}";
                        yield return $"assets/characters/{character}/skins/{skinTag}/loadscreen_{skin}{suffix}{ext}";
                        yield return $"assets/characters/{character}/skins/{skinTag}/loadscreen_{skinTag}{suffix}{ext}";
                        yield return $"assets/characters/{character}/hud/{character}_loadscreen_{skin}{suffix}{ext}";
                        yield return $"assets/characters/{character}/hud/{character}loadscreen_{skin}{suffix}{ext}";
                    }
                }

                foreach (string action in actions)
                {
                    yield return $"assets/characters/{character}/skins/{skinTag}/animations/{character}_{skinTag}_{action}.anm";
                    yield return $"assets/characters/{character}/skins/{skinTag}/animations/{character}_{action}.anm";
                    yield return $"assets/characters/{character}/skins/{skinTag}/animations/{action}.anm";
                    yield return $"assets/characters/{character}/skins/{skinTag}/animations/recall.skins_{character}_{skinTag}.anm";
                    if (baseChar != null)
                    {
                        yield return $"assets/characters/{character}/skins/{skinTag}/animations/{baseChar}_{action}.anm";
                        yield return $"assets/characters/{character}/skins/{skinTag}/animations/{baseChar}_{skinTag}_{action}.anm";
                    }
                }

                yield return $"assets/characters/{character}/hud/{character}_circle_{skin}.dds";
                yield return $"assets/characters/{character}/hud/{character}_circle_{skin}.tex";
                yield return $"assets/characters/{character}/hud/{character}_square_{skin}.dds";
                yield return $"assets/characters/{character}/hud/{character}_square_{skin}.tex";
                yield return $"assets/characters/{character}/hud/icons2d/{character}_{skinTag}.dds";
                yield return $"assets/characters/{character}/hud/icons2d/{character}_{skinTag}.tex";
                yield return $"assets/characters/{character}/skins/{skinTag}/particles/{character}_{skinTag}_glow.tex";
                yield return $"assets/characters/{character}/skins/{skinTag}/particles/{character}_{skinTag}_glow.dds";
                yield return $"assets/characters/{character}/skins/{skinTag}/particles/color-hold.tex";
                yield return $"assets/characters/{character}/skins/{skinTag}/particles/color-hold.dds";
                yield return $"assets/characters/{character}/skins/{skinTag}/particles/common_color-hold.tex";
                yield return $"assets/characters/{character}/skins/{skinTag}/particles/common_color-hold.dds";
                yield return $"assets/characters/{character}/skins/{skinTag}/particles/aura_self.tex";
                yield return $"assets/characters/{character}/skins/{skinTag}/particles/aura_self.dds";
                yield return $"assets/characters/{character}/skins/{skinTag}/particles/alphaslice_mesh.tex";
                yield return $"assets/characters/{character}/skins/{skinTag}/particles/alphaslice_mesh.dds";
            }
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

            var shaderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in KnownPaths)
            {
                Match match = ShaderPathRegex.Match(path);
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
                checkedCandidates += SubstituteBasenameWordsCore(
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
            checkedCandidates += AddBasenameWordCore(
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
                checkedCandidates += CheckCandidates(engine, SubstituteSuffixes(SuffixSubstitutionCandidateBudget), "GAME suffix substitution", cancellationToken, progress, checkedCandidates);
            if (engine.RemainingUnknownCount > 0 && ShouldRun("game-ext-skinnumbers"))
                checkedCandidates += CheckCandidates(
                    engine,
                    SubstituteSkinNumbers(SkinNumberSubstitutionCandidateBudget),
                    "GAME skin number combinations",
                    cancellationToken,
                    progress,
                    checkedCandidates);
            if (engine.RemainingUnknownCount > 0 && ShouldRun("game-ext-characters"))
                checkedCandidates += CheckCandidates(
                    engine,
                    SubstituteCharacter(CharacterSubstitutionCandidateBudget),
                    "GAME character substitution",
                    cancellationToken,
                    progress,
                    checkedCandidates);

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
                            if (generated >= SkinGroupCandidateBudget) return generated;
                            string suffix = string.Concat(combination.SelectMany(value => value).OrderBy(value => value, StringComparer.Ordinal));
                            Check(engine, "data/" + pair.Key + suffix + ".bin", HashGuessStrategy.ChromaGroupVariant, "Local skins.json chroma groups");
                            generated++;
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
                _logService?.LogWarning("Hash Lab skipped skins.json chroma groups: " + exception.Message);
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
                            if (generated >= SkinGroupCandidateBudget) return generated;
                            Check(engine, $"data/{pair.Key}{string.Concat(combination)}.bin", HashGuessStrategy.ChromaGroupVariant, "Local skin groups");
                            generated++;
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

        private List<string> ExtractWordsFromDirectoryJsons(string rootDirectory, CancellationToken cancellationToken)
        {
            var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory)) return words.ToList();
                var regex = new Regex(@"[a-zA-Z0-9_]{3,20}", RegexOptions.Compiled);
                foreach (string file in Directory.EnumerateFiles(rootDirectory, "*.json", SearchOption.AllDirectories).Take(100))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (new FileInfo(file).Length > 2 * 1024 * 1024) continue;
                        foreach (Match match in regex.Matches(File.ReadAllText(file)))
                        {
                            string word = match.Value.ToLowerInvariant();
                            if (word.Length >= 4 && !int.TryParse(word, out _)) words.Add(word);
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        _logService?.LogDebug($"Hash Lab skipped JSON word source '{file}': {exception.Message}");
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logService?.LogError(exception, $"Hash Lab could not enumerate JSON word sources under '{rootDirectory}'.");
            }
            return words.ToList();
        }

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
            bool isBin = extension is "bin" or "inibin";
            if (isBin)
            {
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (int offset in FindBinPathOffsets(data, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (offset < 2) continue;
                    int length = ByteAt(data, offset - 2) | (ByteAt(data, offset - 1) << 8);
                    if (length <= 0 || offset + length > data.Count) continue;
                    if (!TryDecodeAscii(data, offset, length, out string path)) continue;
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
                                        EmbeddedShaderIndices.Select(index =>
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
                if (sourcePath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                {
                    GuessAnimationBinPaths(engine, data, sourcePath, sourceWadPath, sourceChunkHash, cancellationToken);
                    GuessMaterialAndMeshBinPaths(engine, data, sourcePath, sourceWadPath, sourceChunkHash, cancellationToken);
                    GuessRegaliaBinChunkLinks(engine, data, sourcePath, sourceWadPath, sourceChunkHash, cancellationToken);
                }
                GuessSpecialSkinBinPaths(engine, sourcePath, sourceWadPath, sourceChunkHash, cancellationToken);
                return;
            }

            if (extension == "preload")
            {
                string text = Encoding.Latin1.GetString(data.Array, data.Offset, data.Count);
                string directory = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? string.Empty;
                foreach (Match match in PreloadNameRegex.Matches(text))
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
                string directory = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? string.Empty;
                if (string.IsNullOrEmpty(directory)) return;
                foreach (Match match in ShaderIncludeRegex.Matches(text))
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
                string directory = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? string.Empty;
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

            CheckGameCandidates(GrepFileCandidates(data, cancellationToken));
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

                for (uint index = 0; index < objectCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long objectOffset = stream.Position;
                    uint objectSize = reader.ReadUInt32();
                    uint objectPathHash = reader.ReadUInt32();
                    long nextObjectOffset = objectOffset + sizeof(uint) + objectSize;
                    if (objectSize < 6 || nextObjectOffset < stream.Position || nextObjectOffset > stream.Length)
                        return;
                    foreach (string prefix in DottedBinTargetPrefixes)
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
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Match context = AnimationBinPathRegex.Match(PathUtils.NormalizePath(sourcePath));
            if (!context.Success || data.Array is null || data.Count == 0) return;

            var links = new HashSet<AnimationFileLink>();
            try
            {
                using var stream = new MemoryStream(data.Array, data.Offset, data.Count, writable: false);
                var tree = new BinTree(stream);
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
                    context.Groups["character"].Value,
                    context.Groups["skin"].Value,
                    unresolved,
                    engine.UnknownHashes,
                    cancellationToken),
                HashGuessStrategy.AnimationBinLink,
                sourceWadPath,
                cancellationToken,
                sourceChunkHash);
        }

        private void GuessMaterialAndMeshBinPaths(
            HashGuessEngine engine,
            ArraySegment<byte> data,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Match context = AnimationBinPathRegex.Match(PathUtils.NormalizePath(sourcePath));
            string character = context.Success ? context.Groups["character"].Value : null;
            string skin = context.Success ? context.Groups["skin"].Value : null;

            if (string.IsNullOrEmpty(character) && !string.IsNullOrEmpty(sourceWadPath))
            {
                string wadName = Path.GetFileNameWithoutExtension(sourceWadPath);
                if (wadName.EndsWith(".wad", StringComparison.OrdinalIgnoreCase))
                    wadName = Path.GetFileNameWithoutExtension(wadName);
                if (!wadName.Equals("global", StringComparison.OrdinalIgnoreCase) &&
                    !wadName.StartsWith("map", StringComparison.OrdinalIgnoreCase))
                {
                    character = wadName.ToLowerInvariant();
                }
            }

            if (string.IsNullOrEmpty(character) || data.Array is null || data.Count == 0) return;

            var chunkLinks = new HashSet<ulong>();
            try
            {
                using var stream = new MemoryStream(data.Array, data.Offset, data.Count, writable: false);
                var tree = new BinTree(stream);
                cancellationToken.ThrowIfCancellationRequested();
                foreach (ulong link in EnumerateChunkLinks(tree))
                {
                    if (engine.UnknownHashes.Contains(link))
                        chunkLinks.Add(link);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logService?.LogDebug($"GAME material/mesh BIN link scan skipped '{sourcePath}': {exception.Message}");
                return;
            }

            if (chunkLinks.Count == 0) return;

            var charactersToTest = character.StartsWith("jade_", StringComparison.OrdinalIgnoreCase) ||
                                   character.StartsWith("pet", StringComparison.OrdinalIgnoreCase)
                ? new[] { character }
                : new[] { character, $"jade_{character}" };

            var skinsToTest = !string.IsNullOrEmpty(skin)
                ? new[] { skin.ToLowerInvariant() }
                : GetChampionSkinNames(character, cancellationToken);

            var samplers = GetKnownTextureSamplers(cancellationToken);

            foreach (string charName in charactersToTest)
            {
                var templates = GetChampionSkinAssetTemplates(charName, cancellationToken);

                foreach (string skinName in skinsToTest)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (chunkLinks.Count == 0 || engine.RemainingUnknownCount == 0) break;

                    string baseName = $"{charName}_{skinName}";
                    string skinDir = $"assets/characters/{charName}/skins/{skinName}";
                    string dataSkinDir = $"data/characters/{charName}/skins/{skinName}";

                    string baseChar = charName.StartsWith("jade_", StringComparison.OrdinalIgnoreCase) ? charName[5..] : null;

                    foreach (string template in templates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (chunkLinks.Count == 0) break;
                        string resolved = template
                            .Replace("{skin}", skinName, StringComparison.OrdinalIgnoreCase)
                            .Replace("{character}", charName, StringComparison.OrdinalIgnoreCase);

                        TestResolvedTemplate(resolved);
                        if (baseChar != null && template.Contains("{character}", StringComparison.OrdinalIgnoreCase))
                        {
                            TestResolvedTemplate(template
                                .Replace("{skin}", skinName, StringComparison.OrdinalIgnoreCase)
                                .Replace("{character}", baseChar, StringComparison.OrdinalIgnoreCase));
                        }
                    }

                    void TestResolvedTemplate(string res)
                    {
                        CheckLinkCandidate($"{skinDir}/{res}");
                        CheckLinkCandidate($"{skinDir}/2x_{res}");
                        CheckLinkCandidate($"{dataSkinDir}/{res}");
                        CheckLinkCandidate($"{dataSkinDir}/2x_{res}");

                        if (res.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                        {
                            CheckLinkCandidate($"{skinDir}/{res[..^4]}.dds");
                            CheckLinkCandidate($"{skinDir}/2x_{res[..^4]}.dds");
                            CheckLinkCandidate($"{dataSkinDir}/{res[..^4]}.dds");
                            CheckLinkCandidate($"{dataSkinDir}/2x_{res[..^4]}.dds");
                        }
                        else if (res.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                        {
                            CheckLinkCandidate($"{skinDir}/{res[..^4]}.tex");
                            CheckLinkCandidate($"{skinDir}/2x_{res[..^4]}.tex");
                            CheckLinkCandidate($"{dataSkinDir}/{res[..^4]}.tex");
                            CheckLinkCandidate($"{dataSkinDir}/2x_{res[..^4]}.tex");
                        }
                    }

                    string baseCharBaseName = baseChar != null ? $"{baseChar}_{skinName}" : null;

                    foreach (string sampler in samplers)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (chunkLinks.Count == 0) break;
                        foreach (string prefix in CharacterTexturePrefixes)
                        {
                            CheckLinkCandidate($"{skinDir}/{prefix}{baseName}{sampler}.tex");
                            CheckLinkCandidate($"{skinDir}/{prefix}{baseName}{sampler}.dds");
                            CheckLinkCandidate($"{dataSkinDir}/{prefix}{baseName}{sampler}.tex");
                            CheckLinkCandidate($"{dataSkinDir}/{prefix}{baseName}{sampler}.dds");

                            if (baseCharBaseName != null)
                            {
                                CheckLinkCandidate($"{skinDir}/{prefix}{baseCharBaseName}{sampler}.tex");
                                CheckLinkCandidate($"{skinDir}/{prefix}{baseCharBaseName}{sampler}.dds");
                            }
                        }
                    }

                    CheckLinkCandidate($"{skinDir}/{baseName}.tex");
                    CheckLinkCandidate($"{skinDir}/{baseName}.dds");
                    CheckLinkCandidate($"{skinDir}/{baseName}.skn");
                    CheckLinkCandidate($"{skinDir}/{baseName}.skl");
                    CheckLinkCandidate($"{dataSkinDir}/{baseName}.skn");
                    CheckLinkCandidate($"{dataSkinDir}/{baseName}.skl");

                    if (baseCharBaseName != null)
                    {
                        CheckLinkCandidate($"{skinDir}/{baseCharBaseName}.tex");
                        CheckLinkCandidate($"{skinDir}/{baseCharBaseName}.dds");
                        CheckLinkCandidate($"{skinDir}/{baseCharBaseName}.skn");
                        CheckLinkCandidate($"{skinDir}/{baseCharBaseName}.skl");
                    }

                    var actions = GetCharacterAnimationActions(charName);
                    foreach (string action in actions)
                    {
                        CheckLinkCandidate($"{skinDir}/animations/{baseName}_{action}.anm");
                        CheckLinkCandidate($"{skinDir}/animations/{charName}_{action}.anm");
                        CheckLinkCandidate($"{skinDir}/animations/{action}.anm");
                        CheckLinkCandidate($"{dataSkinDir}/animations/{baseName}_{action}.anm");
                        CheckLinkCandidate($"{dataSkinDir}/animations/{charName}_{action}.anm");
                        CheckLinkCandidate($"{dataSkinDir}/animations/{action}.anm");

                        if (baseCharBaseName != null)
                        {
                            CheckLinkCandidate($"{skinDir}/animations/{baseCharBaseName}_{action}.anm");
                            CheckLinkCandidate($"{skinDir}/animations/{baseChar}_{action}.anm");
                        }
                    }
                }
            }

            void CheckLinkCandidate(string candidatePath)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong hash = XxHash64Ext.Hash(candidatePath);
                if (chunkLinks.Remove(hash))
                {
                    Check(engine, candidatePath, HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
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
            CancellationToken cancellationToken)
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
                using var stream = new MemoryStream(data.Array, data.Offset, data.Count, writable: false);
                var tree = new BinTree(stream);
                cancellationToken.ThrowIfCancellationRequested();
                foreach (ulong link in EnumerateChunkLinks(tree))
                {
                    if (engine.UnknownHashes.Contains(link))
                        unresolved.Add(link);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
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

        private IReadOnlyList<string> GetDynamicPetThemeNames(CancellationToken cancellationToken)
        {
            return Corpus.GetOrCreate("dynamic-pet-theme-names", knownPaths =>
            {
                var themes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "base" };
                var petThemeRegex = new Regex(@"^(?:assets|data)/characters/pet[^/]+/themes/([^/]+)/", RegexOptions.IgnoreCase);
                var skinThemeRegex = new Regex(@"^(?:assets|data)/characters/[^/]+/skins/(?:skin\d+|base)/[a-zA-Z0-9]+_([a-zA-Z0-9]+)_tx_cm\.", RegexOptions.IgnoreCase);
                var companionRegex = new Regex(@"(?:tooltip|loot|chibi|portal|icon|icon_square)[_-](?:pet|chibi)?[a-zA-Z0-9]+_([a-zA-Z0-9]+)[_-]", RegexOptions.IgnoreCase);

                for (int i = 0; i < knownPaths.Count; i++)
                {
                    string path = knownPaths[i];
                    if (path.Contains("/themes/", StringComparison.OrdinalIgnoreCase))
                    {
                        Match match = petThemeRegex.Match(path);
                        if (match.Success)
                        {
                            string t = match.Groups[1].Value.ToLowerInvariant();
                            if (t.Length <= 40 && !t.Contains('.'))
                                themes.Add(t);
                        }
                    }
                    else if (path.Contains("_tx_cm", StringComparison.OrdinalIgnoreCase))
                    {
                        Match match = skinThemeRegex.Match(path);
                        if (match.Success)
                        {
                            string t = match.Groups[1].Value.ToLowerInvariant();
                            if (t.Length <= 24 && !t.StartsWith("skin", StringComparison.OrdinalIgnoreCase) && !t.Equals("base", StringComparison.OrdinalIgnoreCase))
                                themes.Add(t);
                        }
                    }
                    else if (path.Contains("companions", StringComparison.OrdinalIgnoreCase) ||
                             path.Contains("rotationalshop", StringComparison.OrdinalIgnoreCase) ||
                             path.Contains("/hud/icon", StringComparison.OrdinalIgnoreCase))
                    {
                        Match match = companionRegex.Match(path);
                        if (match.Success)
                        {
                            string t = match.Groups[1].Value.ToLowerInvariant();
                            if (t.Length is >= 3 and <= 24 && !t.All(char.IsDigit) && !t.StartsWith("tier", StringComparison.OrdinalIgnoreCase))
                                themes.Add(t);
                        }
                    }
                }

                return themes.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        private IReadOnlyList<string> GetDynamicAnimationStems(CancellationToken cancellationToken)
        {
            return Corpus.GetOrCreate("dynamic-animation-stems", knownPaths =>
            {
                var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < knownPaths.Count; i++)
                {
                    string path = knownPaths[i];
                    if (path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase))
                    {
                        string fn = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                        if (fn.Length <= 40) stems.Add(fn);
                        string[] parts = fn.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (string p in parts)
                        {
                            if (p.Length is >= 3 and <= 30 && !p.StartsWith("skin", StringComparison.OrdinalIgnoreCase) && !p.All(char.IsDigit))
                                stems.Add(p);
                        }
                        if (fn.Contains('_'))
                        {
                            string lastPart = fn.Substring(fn.IndexOf('_') + 1);
                            if (lastPart.Length <= 35) stems.Add(lastPart);
                            if (lastPart.Contains('_'))
                            {
                                string afterSecond = lastPart.Substring(lastPart.IndexOf('_') + 1);
                                if (afterSecond.Length <= 30) stems.Add(afterSecond);
                            }
                        }
                    }
                }
                return stems.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        private void GuessSpecialSkinBinPaths(
            HashGuessEngine engine,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(sourceWadPath)) return;
            string wadName = Path.GetFileNameWithoutExtension(sourceWadPath);
            if (wadName.EndsWith(".wad", StringComparison.OrdinalIgnoreCase))
                wadName = Path.GetFileNameWithoutExtension(wadName);
            if (wadName.Equals("global", StringComparison.OrdinalIgnoreCase) ||
                wadName.StartsWith("map", StringComparison.OrdinalIgnoreCase))
                return;

            if (!_scannedWadCharacters.TryAdd(wadName, 0)) return;

            string champ = wadName.ToLowerInvariant();
            string[] aliases = champ.StartsWith("jade_", StringComparison.OrdinalIgnoreCase) ||
                               champ.StartsWith("pet", StringComparison.OrdinalIgnoreCase)
                ? new[] { champ }
                : new[] { champ, $"jade_{champ}" };

            var dynamicPetThemes = champ.StartsWith("pet", StringComparison.OrdinalIgnoreCase)
                ? GetDynamicPetThemeNames(cancellationToken)
                : Array.Empty<string>();
            var animStems = GetDynamicAnimationStems(cancellationToken);
            foreach (string alias in aliases)
            {
                CheckSpecialBin($"data/characters/{alias}/{alias}.bin");
                CheckSpecialBin($"data/characters/{alias}/skins/root.bin");
                CheckSpecialBin($"gameplay.hol{alias}ncvc.bin");
                CheckSpecialBin($"gameplay.{alias}comps.bin");

                string consonantStem = new string(alias.Where(c => !"aeiou_".Contains(c)).ToArray());
                if (!string.IsNullOrEmpty(consonantStem) && !consonantStem.Equals(alias, StringComparison.OrdinalIgnoreCase))
                {
                    CheckSpecialBin($"gameplay.{consonantStem}comps.bin");
                }

                for (int s = 0; s <= 60; s++)
                {
                    CheckSpecialBin($"gameplay.{alias}skin{s}viewcontroller.bin");
                }

                var dynamicSkins = GetChampionSkinNames(alias, cancellationToken);

                foreach (string skin in dynamicSkins)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CheckSpecialBin($"data/characters/{alias}/skins/{skin}.bin");
                    CheckSpecialBin($"data/characters/{alias}/animations/{skin}.bin");
                    CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/{alias}_{skin}_tx_cm.tex");
                    CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/{alias}_{skin}_tx_cm.dds");
                    CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/{alias}_{skin}_cm_tx.tex");
                    CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/{alias}_{skin}_cm_tx.dds");
                    CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/{alias}_{skin}_tx_d.tex");
                    CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/{alias}_{skin}_tx_d.dds");
                    CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/{alias}_{skin}_cubemap.dds");
                    CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/{alias}_{skin}_cubemap.tex");
                    CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/{alias}_{skin}.skn");
                    CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/{alias}_{skin}.skl");
                    CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/{alias}_{skin}.dds");
                    CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/{alias}_{skin}.tex");

                    var commonSubmeshTokens = new[] { "weapon", "weapons", "props", "body", "wings", "mask", "hair", "eyes", "sword", "recall", "head", "glass", "flower", "ult", "main" };
                    foreach (string sub in commonSubmeshTokens)
                    {
                        CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/{alias}_{skin}_{sub}_tx_cm.tex");
                        CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/{alias}_{skin}_{sub}_tx_cm.dds");
                    }

                    if (dynamicPetThemes.Count > 0)
                    {
                        foreach (string theme in dynamicPetThemes)
                        {
                            CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/{alias}_{skin}_{theme}_tx_cm.tex");
                            CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/{alias}_{skin}_{theme}_tx_cm.dds");
                            CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/{alias}_{theme}_tx_cm.tex");
                            CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/{alias}_{theme}_tx_cm.dds");
                        }
                    }

                    foreach (string stem in animStems)
                    {
                        CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/animations/{stem}.anm");
                        CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/animations/{alias}_{stem}.anm");
                        CheckSpecialBin($"assets/characters/{alias}/skins/{skin}/animations/{alias}_{skin}_{stem}.anm");
                        CheckSpecialBin($"assets/characters/{alias}/animations/{skin}/{stem}.anm");
                    }
                }
            }

            void CheckSpecialBin(string path)
            {
                ulong hash = XxHash64Ext.Hash(path);
                if (engine.UnknownHashes.Contains(hash))
                {
                    Check(engine, path, HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                }
            }
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

            int limit = Math.Max(maxAttested + 5, 25);
            if (character.Equals("sightward", StringComparison.OrdinalIgnoreCase)) limit = 500;
            for (int i = 0; i <= limit; i++)
            {
                skins.Add($"skin{i}");
                if (i <= 9) skins.Add($"skin{i:D2}");
            }

            if (character.StartsWith("jade_", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 300; i <= 350; i++) skins.Add($"skin{i}");
            }

            return skins.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private IReadOnlyList<string> GetChampionSkinAssetTemplates(string character, CancellationToken cancellationToken)
        {
            return Corpus.GetOrCreate($"champion-skin-templates/{character.ToLowerInvariant()}", knownPaths =>
            {
                var templates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string charPrefix = $"assets/characters/{character}/skins/";
                string dataCharPrefix = $"data/characters/{character}/skins/";

                string baseChar = character.StartsWith("jade_", StringComparison.OrdinalIgnoreCase) ? character[5..] : null;
                string basePrefix = baseChar != null ? $"assets/characters/{baseChar}/skins/" : null;
                string dataBasePrefix = baseChar != null ? $"data/characters/{baseChar}/skins/" : null;

                for (int pathIndex = 0; pathIndex < knownPaths.Count; pathIndex++)
                {
                    if ((pathIndex & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
                    string path = knownPaths[pathIndex];
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
                    int slash = rel.IndexOf('/');
                    if (slash < 0) continue;
                    string skinToken = rel[..slash];
                    string subPath = rel[(slash + 1)..];

                    if (subPath.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) ||
                        subPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string templated = subPath.Replace(skinToken, "{skin}", StringComparison.OrdinalIgnoreCase);
                    if (baseChar != null)
                        templated = templated.Replace(baseChar, "{character}", StringComparison.OrdinalIgnoreCase);
                    if (templated.Length > 0 && templated.Length < 260)
                        templates.Add(templated);
                }

                return templates.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        private static readonly string[] DefaultTextureSamplers =
        {
            "_tx_cm", "_base_tx_cm", "_tx_gm", "_tx_mask", "_tx_rm",
            "_tx_outline", "_base_tx_gm", "_tx_em", "_tx_coin", "_base_tx_rm", "_tx_noise"
        };

        private IReadOnlyList<string> GetKnownTextureSamplers(CancellationToken cancellationToken)
        {
            return Corpus.GetOrCreate("canonical-texture-samplers", knownPaths =>
            {
                var samplers = new HashSet<string>(DefaultTextureSamplers, StringComparer.OrdinalIgnoreCase);
                var regex = new Regex(@"(_(?:base_)?tx_[a-zA-Z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

                for (int i = 0; i < knownPaths.Count; i++)
                {
                    if ((i & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
                    string path = knownPaths[i];
                    if (!path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) &&
                        !path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var match = regex.Match(path);
                    if (match.Success)
                    {
                        samplers.Add(match.Value.ToLowerInvariant());
                    }
                }

                return samplers.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            });
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
                    candidateDirs.Add(dir.Replace('\\', '/'));
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
                        string dir = Path.GetDirectoryName(norm)?.Replace('\\', '/');
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
                            dirs.Add(dir.Replace('\\', '/'));
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
                foreach (string path in MatchAnimationVariants(name, character, skin, remaining, prefixes, suffixes))
                    yield return path;
                if (remaining.Count == 0) yield break;
            }

            if (remaining.Count == 0) yield break;

            IReadOnlyList<string> sourceNames = GetAnimationNames(character, cancellationToken);

            foreach (HashGuessCandidate candidate in GenerateNumberCandidates(
                         sourceNames.Where(name => name.Any(char.IsDigit)).Select(name => $"animations/{name}"),
                         AnimationNumberLimit,
                         int.MaxValue,
                         digits: null,
                         inferDigits: false,
                         includeCommonPadding: false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = GetBasename(candidate.Path);
                if (!attemptedNames.Add(name)) continue;
                foreach (string path in MatchAnimationVariants(name, character, skin, remaining, DefaultPrefixModifiers, DefaultSuffixModifiers))
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
            for (int pathIndex = 0; pathIndex < knownPaths.Count; pathIndex++)
            {
                if ((pathIndex & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
                string path = knownPaths[pathIndex];
                if (!path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase)) continue;
                Match context = KnownAnimationPathRegex.Match(PathUtils.NormalizePath(path));
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
            IReadOnlyList<string> suffixes = null)
        {
            foreach (string path in EnumerateAnimationNameVariants(character, skin, name, prefixes, suffixes))
            {
                if (remaining.Remove(XxHash64Ext.Hash(PathUtils.NormalizePath(path))))
                    yield return path;
            }

            string converted = AnimationSkinTokenRegex.Replace(name, skin);
            if (converted.Equals(name, StringComparison.OrdinalIgnoreCase)) yield break;
            foreach (string path in EnumerateAnimationNameVariants(character, skin, converted, prefixes, suffixes))
                if (remaining.Remove(XxHash64Ext.Hash(PathUtils.NormalizePath(path))))
                    yield return path;
        }

        private static IEnumerable<string> EnumerateAnimationNameVariants(
            string character,
            string skin,
            string name,
            IReadOnlyList<string> prefixModifiers = null,
            IReadOnlyList<string> suffixModifiers = null)
        {
            string stem = name.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            if (string.IsNullOrWhiteSpace(stem) || stem.Contains('/') || stem.Contains('\\')) yield break;
            stem = stem.ToLowerInvariant();

            string paddedSkin = Regex.IsMatch(skin, @"^skin\d$") ? "skin0" + skin[4..] : (Regex.IsMatch(skin, @"^skin0\d$") ? "skin" + skin[5..] : skin);
            string[] skinsToTry = string.Equals(skin, paddedSkin, StringComparison.OrdinalIgnoreCase) ? new[] { skin } : new[] { skin, paddedSkin };

            foreach (string sk in skinsToTry)
            foreach (string pre in prefixModifiers ?? DefaultPrefixModifiers)
            foreach (string suf in suffixModifiers ?? DefaultSuffixModifiers)
            {
                string s = pre + stem + suf;
                foreach (string root in AnimationRootPrefixes)
                {
                    yield return $"{root}/characters/{character}/skins/{sk}/animations/{s}.anm";
                    yield return $"{root}/characters/{character}/skins/{sk}/animations/{character}_{s}.anm";
                    yield return $"{root}/characters/{character}/skins/{sk}/animations/{character}_{sk}_{s}.anm";
                    yield return $"{root}/characters/{character}/skins/{sk}/animations/{sk}_{s}.anm";
                }
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
            var roots = tree.Objects.Values.SelectMany(obj => obj.Properties.Values)
                .Concat(tree.DataOverrides.Select(ovr => ovr.Property));
            foreach (BinTreeMap map in roots
                         .SelectMany(root => FindProperties(root, ClipDataMapNameHash))
                         .OfType<BinTreeMap>())
            foreach (var pair in map)
            {
                uint nameHash = pair.Key is BinTreeHash hash ? hash.Value : 0;
                foreach (BinTreeProperty path in FindProperties(pair.Value, AnimationFilePathNameHash).Take(1))
                {
                    if (path is BinTreeWadChunkLink link)
                        yield return new AnimationFileLink(nameHash, link.Value, null);
                    else if (path is BinTreeString text && !string.IsNullOrWhiteSpace(text.Value))
                        yield return new AnimationFileLink(nameHash, 0, PathUtils.NormalizePath(text.Value));
                }
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
            foreach (HashGuessCandidate candidate in GrepFileCandidates(data, CancellationToken.None))
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
                    foreach (string prefix in LuaCharacterPrefixes)
                    foreach (string extension in LuaExtensions)
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
                if (hashMap.TryGetValue(nameHashTruncated, out uint dirIndex) && dirIndex < (uint)SharedScriptDirectories.Length)
                {
                    string dir = SharedScriptDirectories[dirIndex];
                    foreach (string extension in LuaExtensions)
                    {
                        yield return new HashGuessCandidate($"{dir}/{name}.{extension}", HashGuessStrategy.LuaManifest);
                    }
                }
                else
                {
                    // Fallback for stripped scripts (Cheat* or Map scripts)
                    if (name.StartsWith("cheat", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string extension in LuaExtensions)
                            yield return new HashGuessCandidate($"data/shared/spells/cheat/{name}.{extension}", HashGuessStrategy.LuaManifest);
                    }
                    else
                    {
                        foreach (string prefix in LuaCommonPaths)
                        foreach (string extension in LuaExtensions)
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

        private static IEnumerable<HashGuessCandidate> GrepFileCandidates(
            ArraySegment<byte> data,
            CancellationToken cancellationToken)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach ((int offset, int length) in FindGeneralPathRanges(data, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = PathUtils.NormalizeGrepPath(Encoding.ASCII.GetString(data.Array, data.Offset + offset, length));
                if (path.Length > 0) paths.Add(path);
                if (offset < 2) continue;
                int encodedLength = ByteAt(data, offset - 2) | (ByteAt(data, offset - 1) << 8);
                if (encodedLength == 0 && offset >= 4)
                    encodedLength = ByteAt(data, offset - 4) | (ByteAt(data, offset - 3) << 8) |
                                    (ByteAt(data, offset - 2) << 16) | (ByteAt(data, offset - 1) << 24);
                if (encodedLength <= 0 || encodedLength >= length || offset + encodedLength > data.Count) continue;
                string shortened = PathUtils.NormalizeGrepPath(Encoding.ASCII.GetString(data.Array, data.Offset + offset, encodedLength));
                if (shortened.Length > 0) paths.Add(shortened);
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

        private static IEnumerable<(int Offset, int Length)> FindGeneralPathRanges(
            ArraySegment<byte> data,
            CancellationToken cancellationToken)
        {
            int limit = data.Count;
            for (int offset = 0; offset < limit; offset++)
            {
                if ((offset & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                byte[][] prefixes = GetPrefixes(ByteAt(data, offset));
                if (prefixes == null) continue;
                foreach (byte[] prefix in prefixes)
                {
                    if (offset + prefix.Length > limit) continue;

                    int prefixIndex = 1;
                    while (prefixIndex < prefix.Length && ByteAt(data, offset + prefixIndex) == prefix[prefixIndex]) prefixIndex++;
                    if (prefixIndex != prefix.Length) continue;

                    int end = offset + prefix.Length;
                    while (end < limit && IsGeneralPathByte(ByteAt(data, end)))
                    {
                        if ((end & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                        end++;
                    }
                    if (end == offset + prefix.Length) continue;
                    yield return (offset, end - offset);

                    offset = end - 1;
                    break;
                }
            }
        }

        private static bool IsGeneralPathByte(byte value) =>
            value is >= (byte)'0' and <= (byte)'9' or
                >= (byte)'A' and <= (byte)'Z' or
                >= (byte)'a' and <= (byte)'z' or
                (byte)'_' or (byte)'.' or (byte)' ' or (byte)'/' or (byte)'-';

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

        private static IEnumerable<int> FindBinPathOffsets(
            ArraySegment<byte> data,
            CancellationToken cancellationToken)
        {
            for (int offset = 0; offset < data.Count; offset++)
            {
                if ((offset & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                byte[][] needles = GetWadBinPrefixes(ByteAt(data, offset));
                if (needles == null) continue;
                foreach (byte[] needle in needles)
                {
                    if (offset + needle.Length > data.Count) continue;
                    int index = 1;
                    while (index < needle.Length && ByteAt(data, offset + index) == needle[index]) index++;
                    if (index != needle.Length) continue;
                    yield return offset;
                    break;
                }
            }
        }

        private static byte[][] GetWadBinPrefixes(byte firstByte) => firstByte switch
        {
            (byte)'A' => WadBinPrefixesA,
            (byte)'C' => WadBinPrefixesC,
            (byte)'D' => WadBinPrefixesD,
            (byte)'G' => WadBinPrefixesG,
            (byte)'L' => WadBinPrefixesL,
            (byte)'M' => WadBinPrefixesM,
            (byte)'P' => WadBinPrefixesP,
            (byte)'S' => WadBinPrefixesS,
            _ => null
        };

        private static byte[][] GetPrefixes(byte firstByte) => firstByte switch
        {
            (byte)'A' => BinPrefixesA,
            (byte)'C' => BinPrefixesC,
            (byte)'D' => BinPrefixesD,
            (byte)'G' => BinPrefixesG,
            (byte)'L' => BinPrefixesL,
            (byte)'U' => BinPrefixesU,
            _ => null
        };

        private static bool TryDecodeAscii(ArraySegment<byte> data, int offset, int length, out string value)
        {
            for (int index = 0; index < length; index++)
            {
                if (ByteAt(data, offset + index) > 0x7F)
                {
                    value = string.Empty;
                    return false;
                }
            }
            value = Encoding.ASCII.GetString(data.Array, data.Offset + offset, length);
            return true;
        }

        private static bool IsAscii(string value)
        {
            foreach (char character in value)
                if (character > 0x7F) return false;
            return true;
        }

        private static byte ByteAt(ArraySegment<byte> data, int index) => data.Array[data.Offset + index];

        private static byte[][] ToAsciiPrefixes(params string[] prefixes) => prefixes.Select(Encoding.ASCII.GetBytes).ToArray();

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
