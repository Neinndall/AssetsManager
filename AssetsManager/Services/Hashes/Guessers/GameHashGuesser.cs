using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
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
            @"^data/characters/(?<character>[^/]+)/animations/(?<skin>[^/]+)\.bin$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex KnownAnimationPathRegex = new(
            @"^(?:assets|data)/characters/(?<character>[^/]+)/skins/(?<skin>[^/]+)/animations/[^/]+\.anm$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AnimationSkinTokenRegex = new("skin\\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private const int AnimationNumberLimit = 10_000;
        private const int CustomBinSampleSize = 30_000;
        private const int CustomCharacterDdsSampleSize = 25_000;
        private const int CustomCharacterTexSampleSize = 20_000;
        private const int CustomWordAdditionSampleSize = 20_000;
        private const int CustomFocusedPathSampleSize = 20_000;
        private const int CustomSwordlistCandidateBudget = 10_000_000;
        private const int CustomWordlistCandidateBudget = 10_000_000;
        private const int SkinGroupCandidateBudget = 5_000_000;
        private const int CharacterSubstitutionCandidateBudget = 10_000_000;
        private const int SkinNumberSubstitutionCandidateBudget = 10_000_000;
        private static readonly uint AnimationFilePathNameHash = Fnv1a.HashLower("mAnimationFilePath");
        private static readonly uint ClipDataMapNameHash = Fnv1a.HashLower("mClipDataMap");
        private static readonly uint AnimationResourceDataNameHash = Fnv1a.HashLower("mAnimationResourceData");
        private static readonly HashSet<string> SkippedExtensions = new(StringComparer.Ordinal)
        {
            "dds", "jpg", "png", "tga", "ttf", "otf", "ogg", "webm", "anm", "skl", "skn",
            "scb", "sco", "troybin", "bnk", "wpk", "tex"
        };
        private readonly record struct AnimationFileLink(uint NameHash, ulong PathHash);

        private readonly LogService _logService;

        internal GameHashGuesser(HashFile hashFile, LogService logService = null)
            : base(hashFile, "*.wad.client")
        {
            if (hashFile.Domain != HashGuessDomain.Game) throw new ArgumentException("GAME guesser requires a GAME hash file.", nameof(hashFile));
            _logService = logService;
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

        internal IEnumerable<HashGuessCandidate> SubstituteNumbers(int maximum = 200, int? digits = null, bool inferDigits = false) =>
            GenerateNumberCandidates(maximum, int.MaxValue, digits, inferDigits, includeCommonPadding: false);

        internal int SubstituteNumbers(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            int maximum = 200,
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

            string[] values = (prefixes ?? new[] { "2x_", "2x_sd_", "4x_", "4x_sd_", "sd_" }).ToArray();
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

            IReadOnlyList<string> seedDataBins = dataPaths.Take(CustomBinSampleSize).ToList();
            IReadOnlyList<string> words = dataWordlist.Take(CustomBinSampleSize).ToList();
            return SubstituteBasenameWordsCore(
                engine,
                seedDataBins,
                words,
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

            IReadOnlyList<string> seedDdsPaths = characterDdsPaths.Take(CustomCharacterDdsSampleSize).ToList();
            IReadOnlyList<string> words = characterDdsWordlist.Take(CustomCharacterDdsSampleSize).ToList();
            return SubstituteBasenameWordsCore(
                engine,
                seedDdsPaths,
                words,
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

            IReadOnlyList<string> seedTexPaths = characterTexPaths.Take(CustomCharacterTexSampleSize).ToList();
            IReadOnlyList<string> words = characterTexWordlist.Take(CustomCharacterTexSampleSize).ToList();
            return SubstituteBasenameWordsCore(
                engine,
                seedTexPaths,
                words,
                oldWordCount: 1,
                newWordCount: 1,
                cancellationToken,
                candidateBudget: int.MaxValue,
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
                candidateBudget: int.MaxValue);
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
            CancellationToken cancellationToken)
        {
            int checkedCandidates = 0;
            if (engine.RemainingUnknownCount == 0) return checkedCandidates;

            progress?.Report(engine.CreateProgress("GAME Custom: BIN basename wordlist", checkedCandidates));
            int progressOffset = checkedCandidates;
            checkedCandidates += SubstituteBinBasenameWords(
                engine,
                cancellationToken,
                count => progress?.Report(engine.CreateProgress(
                    "GAME Custom: BIN basename wordlist", progressOffset + count)));

            if (engine.RemainingUnknownCount == 0) return checkedCandidates;

            progress?.Report(engine.CreateProgress(
                "GAME Custom: data BIN basename wordlist", checkedCandidates));
            progressOffset = checkedCandidates;
            checkedCandidates += SubstituteDataBinBasenameWords(
                engine,
                cancellationToken,
                count => progress?.Report(engine.CreateProgress(
                    "GAME Custom: data BIN basename wordlist", progressOffset + count)));

            if (engine.RemainingUnknownCount == 0) return checkedCandidates;

            progress?.Report(engine.CreateProgress(
                "GAME Custom: character DDS basename wordlist", checkedCandidates));
            progressOffset = checkedCandidates;
            checkedCandidates += SubstituteCharacterDdsBasenameWords(
                engine,
                cancellationToken,
                count => progress?.Report(engine.CreateProgress(
                    "GAME Custom: character DDS basename wordlist", progressOffset + count)));

            if (engine.RemainingUnknownCount == 0) return checkedCandidates;

            progress?.Report(engine.CreateProgress(
                "GAME Custom: character TEX basename wordlist", checkedCandidates));
            progressOffset = checkedCandidates;
            checkedCandidates += SubstituteCharacterTexBasenameWords(
                engine,
                cancellationToken,
                count => progress?.Report(engine.CreateProgress(
                    "GAME Custom: character TEX basename wordlist", progressOffset + count)));

            if (engine.RemainingUnknownCount == 0) return checkedCandidates;

            progress?.Report(engine.CreateProgress(
                "GAME Custom: SwordList basename substitution", checkedCandidates));
            progressOffset = checkedCandidates;
            checkedCandidates += SubstituteSwordlistBasenameWords(
                engine,
                cancellationToken,
                count => progress?.Report(engine.CreateProgress(
                    "GAME Custom: SwordList basename substitution", progressOffset + count)));

            if (engine.RemainingUnknownCount == 0) return checkedCandidates;

            progress?.Report(engine.CreateProgress(
                "GAME Custom: WordList basename substitution", checkedCandidates));
            progressOffset = checkedCandidates;
            checkedCandidates += SubstituteWordlistBasenameWords(
                engine,
                cancellationToken,
                count => progress?.Report(engine.CreateProgress(
                    "GAME Custom: WordList basename substitution", progressOffset + count)));

            if (engine.RemainingUnknownCount == 0) return checkedCandidates;

            progress?.Report(engine.CreateProgress(
                "GAME Custom: basename word addition", checkedCandidates));
            progressOffset = checkedCandidates;
            checkedCandidates += AddCustomBasenameWord(
                engine,
                cancellationToken,
                count => progress?.Report(engine.CreateProgress(
                    "GAME Custom: basename word addition", progressOffset + count)));
            return checkedCandidates;
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

        internal IEnumerable<HashGuessCandidate> SubstituteCharacter(int candidateBudget = int.MaxValue) => GenerateCharacterSubstitutionCandidates(candidateBudget);
        internal IEnumerable<HashGuessCandidate> SubstituteSkinNumbers(int candidateBudget = int.MaxValue) => GenerateSkinNumberCandidates(candidateBudget);
        internal IEnumerable<HashGuessCandidate> SubstituteSuffixes() => GenerateSuffixCandidates(int.MaxValue);
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
                progress?.Invoke(checkedCount);
            }

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

            IReadOnlyList<string> characterList = (characters ?? GetCharacters())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
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
                    $"data/characters/{character}/{character}.bin",
                    $"data/characters/{character}/{character}.ddf",
                    $"data/characters/{character}/hud/{character}_circle.dds",
                    $"data/characters/{character}/hud/{character}_square.dds",
                    $"assets/characters/{character}/hud/{character}_circle.dds",
                    $"assets/characters/{character}/hud/{character}_square.dds",
                    $"characters/{character}"
                });

                int skinLimit = character.Equals("sightward", StringComparison.OrdinalIgnoreCase) ? 500 : 200;
                checkedCount += CheckCharacterPaths(
                    Enumerable.Range(0, skinLimit).Select(skin =>
                        $"data/characters/{character}/skins/skin{skin}.bin"));
                checkedCount += CheckCharacterPaths(
                    Enumerable.Range(0, skinLimit).Select(skin =>
                        $"data/characters/{character}/animations/skin{skin}.bin"));
                if (character.StartsWith("pet", StringComparison.OrdinalIgnoreCase))
                    checkedCount += CheckCharacterPaths(
                        Enumerable.Range(0, 10).Select(tier =>
                            $"data/characters/{character}/tiers/tier{tier}.bin"));

                if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) break;
            }

            return checkedCount;
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
            Action<int> progress = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            if (candidateBudget < 0) throw new ArgumentOutOfRangeException(nameof(candidateBudget));
            if (candidateBudget == 0) return 0;

            var shaderPaths = new HashSet<string>(StringComparer.Ordinal);
            var regex = new Regex(@".*\.[pv]s(?:_[23]_0|(?=$|[.-]))", RegexOptions.IgnoreCase);
            foreach (string path in KnownPaths)
            {
                Match match = regex.Match(path);
                if (match.Success) shaderPaths.Add(match.Value);
            }

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
                IEnumerable<HashGuessCandidate> numberedVariants =
                    ShaderVariants.SelectMany(variant => Enumerable.Range(0, 20000 / 100).Select(index =>
                        new HashGuessCandidate(
                            $"{path}{variant}_{index * 100}",
                            HashGuessStrategy.ShaderVariant)));
                if (remaining != int.MaxValue) numberedVariants = numberedVariants.Take(remaining);
                checkedCount += CheckIter(engine, numberedVariants, "GAME shader variants", cancellationToken);
                progress?.Invoke(checkedCount);
            }

            return checkedCount;
        }

        internal int RunEsportsBannersAttack(HashGuessEngine engine, string rootDirectory, CancellationToken cancellationToken)
        {
            var paths = KnownPaths.Where(path => path.StartsWith("assets/esports/", StringComparison.OrdinalIgnoreCase)).ToList();
            if (paths.Count == 0) return 0;
            var words = HashGuessEngine.BuildWordlist(paths).ToList();
            foreach (string keyword in new[]
            {
                "halloflegends", "air", "pg", "action", "lrn", "faker", "es", "spirit", "blossom",
                "uzi", "gll", "kaktus", "kotsovolos", "kb", "trophy", "league", "legends", "greek",
                "masters", "visa", "al", "2024", "arabian", "2025", "2026", "five", "elite", "series",
                "arcane", "lolesports", "omen", "moviestar", "audi", "kitkat", "emea"
            })
                if (!words.Contains(keyword, StringComparer.OrdinalIgnoreCase)) words.Add(keyword);
            foreach (string word in ExtractWordsFromDirectoryJsons(rootDirectory, cancellationToken))
                if (!words.Contains(word, StringComparer.OrdinalIgnoreCase)) words.Add(word);
            return RunFocusedWordlistSubstitution(engine, paths, words, cancellationToken) +
                   AddBasenameWordCore(engine, paths, words, cancellationToken);
        }

        internal async Task<int> RunExtendedAttacksAsync(
            HashGuessEngine engine,
            string rootDirectory,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken)
        {
            int checkedCandidates = 0;

            if (engine.RemainingUnknownCount > 0)
                checkedCandidates += await GuessSkinGroupsBin(engine, cancellationToken);
            if (engine.RemainingUnknownCount > 0)
                checkedCandidates += await GuessSkinGroupsBinUsingChromas(engine, rootDirectory, cancellationToken);
            if (engine.RemainingUnknownCount > 0)
                checkedCandidates += CheckCandidates(engine, SubstituteSuffixes(), "GAME suffix substitution", cancellationToken, progress, checkedCandidates);
            if (engine.RemainingUnknownCount > 0)
                checkedCandidates += CheckCandidates(
                    engine,
                    SubstituteSkinNumbers(SkinNumberSubstitutionCandidateBudget),
                    "GAME skin number combinations",
                    cancellationToken,
                    progress,
                    checkedCandidates);
            if (engine.RemainingUnknownCount > 0)
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
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory)) return 0;
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
                    foreach (IEnumerable<List<string>> combination in GetCombinations(tokens, length))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (generated >= SkinGroupCandidateBudget) return generated;
                        string suffix = string.Concat(combination.SelectMany(value => value).OrderBy(value => value, StringComparer.Ordinal));
                        Check(engine, "data/" + pair.Key + suffix + ".bin", HashGuessStrategy.ChromaGroupVariant, "Local skins.json chroma groups");
                        generated++;
                        if (engine.RemainingUnknownCount == 0) return generated;
                    }
                }
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
            CancellationToken cancellationToken) =>
            GuessChromaGroupsAsync(engine, rootDirectory, cancellationToken);

        private string LoadLocalSkinsJson(string rootDirectory, CancellationToken cancellationToken)
        {
            ulong skinsJsonHash = XxHash64Ext.Hash(RiotCatalogDefinitions.SkinsJsonPath);
            IEnumerable<string> wadPaths = Directory.EnumerateFiles(rootDirectory, "*.wad", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".wad", StringComparison.OrdinalIgnoreCase))
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

        internal Task<int> GuessSkinGroupsBinLocalAsync(HashGuessEngine engine, CancellationToken cancellationToken)
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
                foreach (var pair in characters.OrderBy(pair => pair.Value.Count))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var skins = pair.Value.Select(value => $"_skins_skin{value}").OrderBy(value => value).ToList();
                    for (int length = 1; length <= skins.Count; length++)
                    foreach (IEnumerable<string> combination in GetCombinations(skins, length))
                    {
                        if (generated >= SkinGroupCandidateBudget) return generated;
                        Check(engine, $"data/{pair.Key}{string.Concat(combination)}.bin", HashGuessStrategy.ChromaGroupVariant, "Local skin groups");
                        generated++;
                        if (engine.RemainingUnknownCount == 0) return generated;
                    }
                }
                return generated;
            }, cancellationToken);
        }

        internal Task<int> GuessSkinGroupsBin(HashGuessEngine engine, CancellationToken cancellationToken) =>
            GuessSkinGroupsBinLocalAsync(engine, cancellationToken);

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

        internal override void GrepWad(HashGuessEngine engine, ArraySegment<byte> data, string sourcePath, string sourceWadPath, ulong sourceChunkHash)
        {
            if (data.Count == 0) return;

            void CheckGame(string path, HashGuessStrategy strategy = HashGuessStrategy.BinLengthPath)
            {
                if (!string.IsNullOrEmpty(path))
                    Check(engine, path, strategy, sourceWadPath, sourceChunkHash);
            }

            void CheckGameIter(IEnumerable<string> paths, HashGuessStrategy strategy = HashGuessStrategy.BinLengthPath) =>
                CheckIter(engine, paths, strategy, sourceWadPath, sourceChunkHash);

            void CheckGameCandidates(IEnumerable<HashGuessCandidate> candidates) =>
                CheckIter(engine, candidates, sourceWadPath, CancellationToken.None, sourceChunkHash: sourceChunkHash);

            if (sourcePath.Equals("data/all_lua_files.manifest", StringComparison.OrdinalIgnoreCase))
            {
                CheckGameCandidates(ExtractLuaManifestCandidates(data));
                return;
            }

            string extension = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
            if (extension is "bin" or "inibin")
            {
                foreach (int offset in FindBinPathOffsets(data))
                {
                    if (offset < 2) continue;
                    int length = ByteAt(data, offset - 2) | (ByteAt(data, offset - 1) << 8);
                    if (length <= 0 || offset + length > data.Count) continue;
                    if (!TryDecodeAscii(data, offset, length, out string path)) continue;
                    path = NormalizePath(path);

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
                    else if (path.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase))
                    {
                        CheckGameIter(
                            ShaderExtensions.Select(extensionName =>
                                $"assets/shaders/generated/{path}{extensionName}"));
                        CheckGameIter(
                            ShaderExtensions.SelectMany(extensionName =>
                                ShaderVariants.Select(variant =>
                                    $"assets/shaders/generated/{path}{extensionName}{variant}")));
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

                if (sourcePath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                    GrepAnimationBinLinks(engine, data, sourcePath, sourceWadPath, sourceChunkHash);
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

            CheckGameCandidates(GrepFileCandidates(data));
        }

        internal IReadOnlyList<string> GenerateAnimationContextCandidates(string character, string skin)
        {
            if (string.IsNullOrWhiteSpace(character) || string.IsNullOrWhiteSpace(skin)) return Array.Empty<string>();
            return GetAnimationCandidateIndex(character, skin).Values.ToList();
        }

        private void GrepAnimationBinLinks(
            HashGuessEngine engine,
            ArraySegment<byte> data,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash)
        {
            Match context = AnimationBinPathRegex.Match(PathUtils.NormalizePath(sourcePath));
            if (!context.Success || data.Array is null || data.Count == 0) return;

            var links = new HashSet<AnimationFileLink>();
            try
            {
                using var stream = new MemoryStream(data.Array, data.Offset, data.Count, writable: false);
                var tree = new BinTree(stream);
                foreach (AnimationFileLink link in EnumerateAnimationFileLinks(tree))
                    if (link.PathHash != 0 && engine.UnknownHashes.Contains(link.PathHash)) links.Add(link);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logService?.LogDebug($"GAME animation BIN link scan skipped '{sourcePath}': {exception.Message}");
                return;
            }

            if (links.Count == 0) return;
            IReadOnlyDictionary<ulong, string> candidates = GetAnimationCandidateIndex(
                context.Groups["character"].Value,
                context.Groups["skin"].Value);
            CheckIter(
                engine,
                GetMatchingAnimationPaths(links.Select(link => link.PathHash), candidates),
                HashGuessStrategy.AnimationBinLink,
                sourceWadPath,
                sourceChunkHash);

            if (engine.RemainingUnknownCount > 0)
                CheckIter(
                    engine,
                    EnumerateAnimationFallbackPaths(
                        context.Groups["character"].Value,
                        context.Groups["skin"].Value,
                        links,
                        engine.UnknownHashes),
                    HashGuessStrategy.AnimationBinLink,
                    sourceWadPath,
                    sourceChunkHash);
        }

        private static IEnumerable<string> GetMatchingAnimationPaths(
            IEnumerable<ulong> targetHashes,
            IReadOnlyDictionary<ulong, string> candidates)
        {
            foreach (ulong targetHash in targetHashes.Distinct())
                if (candidates.TryGetValue(targetHash, out string path))
                    yield return path;
        }

        private IReadOnlyDictionary<ulong, string> GetAnimationCandidateIndex(string character, string skin)
        {
            string normalizedCharacter = character.ToLowerInvariant();
            string normalizedSkin = skin.ToLowerInvariant();
            HashCorpusIndex corpus = Corpus;
            return corpus.GetOrCreate(
                $"animation-candidates/{normalizedCharacter}/{normalizedSkin}",
                paths => BuildAnimationCandidateIndex(paths, normalizedCharacter, normalizedSkin));
        }

        private static IReadOnlyDictionary<ulong, string> BuildAnimationCandidateIndex(
            IReadOnlyList<string> knownPaths,
            string character,
            string skin)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var animationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var characterAnimationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in knownPaths)
            {
                if (!path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase)) continue;
                string name = GetBasename(path);
                if (name.Length == 0) continue;

                animationNames.Add(name);
                Match knownContext = KnownAnimationPathRegex.Match(path);
                if (knownContext.Success && knownContext.Groups["character"].Value.Equals(character, StringComparison.OrdinalIgnoreCase))
                    characterAnimationNames.Add(name);
            }

            HashSet<string> reusablePrefixes = GetReusableAnimationPrefixes(characterAnimationNames);
            foreach (string name in animationNames)
            {
                AddAnimationNameVariants(paths, character, skin, name);

                string convertedName = AnimationSkinTokenRegex.Replace(name, skin);
                if (!convertedName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    AddAnimationNameVariants(paths, character, skin, convertedName);
            }

            foreach (string name in characterAnimationNames)
            {
                foreach (string prefix in reusablePrefixes)
                {
                    AddAnimationNameVariants(paths, character, skin, prefix + "_" + name);
                    string convertedName = AnimationSkinTokenRegex.Replace(name, skin);
                    if (!convertedName.Equals(name, StringComparison.OrdinalIgnoreCase))
                        AddAnimationNameVariants(paths, character, skin, prefix + "_" + convertedName);
                }
            }

            var result = new Dictionary<ulong, string>();
            foreach (string path in paths.OrderBy(value => value, StringComparer.Ordinal))
                result.TryAdd(XxHash64Ext.Hash(PathUtils.NormalizePath(path)), path);
            return result;
        }

        private IEnumerable<string> EnumerateAnimationFallbackPaths(
            string character,
            string skin,
            IEnumerable<AnimationFileLink> links,
            IReadOnlyCollection<ulong> unknownHashes)
        {
            var remaining = links
                .Where(link => link.PathHash != 0 && unknownHashes.Contains(link.PathHash))
                .Select(link => link.PathHash)
                .ToHashSet();
            if (remaining.Count == 0) yield break;

            IReadOnlyList<string> contextualNames = GetAnimationNames(character, contextual: true);
            IReadOnlyList<string> sourceNames = contextualNames.Count > 0
                ? contextualNames
                : GetAnimationNames(character, contextual: false);
            foreach (HashGuessCandidate candidate in GenerateNumberCandidates(
                         sourceNames.Where(name => name.Any(char.IsDigit)).Select(name => $"animations/{name}"),
                         AnimationNumberLimit,
                         int.MaxValue,
                         digits: null,
                         inferDigits: false,
                         includeCommonPadding: false))
            {
                foreach (string path in EnumerateUnresolvedAnimationVariants(GetBasename(candidate.Path), character, skin, remaining))
                    yield return path;
                if (remaining.Count == 0) yield break;
            }

            HashSet<uint> nameHashes = links
                .Where(link => remaining.Contains(link.PathHash) && link.NameHash != 0)
                .Select(link => link.NameHash)
                .ToHashSet();
            if (nameHashes.Count == 0) yield break;

            IReadOnlyList<string> allNames = GetAnimationNames(character, contextual: false);
            var formats = Corpus.GetOrCreate(
                "animation-word-formats",
                _ => BuildBasenameWordFormats(allNames.Select(name => $"animations/{name}"), 1, 1));
            IReadOnlyList<string> words = Corpus.GetOrCreate("frequency-wordlist", HashGuessEngine.BuildFrequencyWordlist);
            var generatedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string prefix, string suffix) in formats)
            foreach (string word in words)
            {
                string generated = GetBasename(prefix + word + suffix);
                string stem = generated.EndsWith(".anm", StringComparison.OrdinalIgnoreCase)
                    ? generated[..^4]
                    : generated;
                if (!nameHashes.Contains(Fnv1a.HashLower(stem)) || !generatedNames.Add(generated)) continue;

                foreach (string path in EnumerateUnresolvedAnimationVariants(generated, character, skin, remaining))
                    yield return path;
                if (remaining.Count == 0) yield break;
            }
        }

        private IReadOnlyList<string> GetAnimationNames(string character, bool contextual)
        {
            string key = contextual ? $"animation-names/{character.ToLowerInvariant()}" : "animation-names/all";
            return Corpus.GetOrCreate(
                key,
                paths => BuildAnimationNames(paths, contextual ? character : null));
        }

        private static IReadOnlyList<string> BuildAnimationNames(IReadOnlyList<string> knownPaths, string character)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in knownPaths)
            {
                Match context = KnownAnimationPathRegex.Match(PathUtils.NormalizePath(path));
                if (!context.Success || (character != null && !context.Groups["character"].Value.Equals(character, StringComparison.OrdinalIgnoreCase)))
                    continue;

                string name = GetBasename(path);
                if (name.Length > 0) names.Add(name);
            }

            return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IEnumerable<string> EnumerateUnresolvedAnimationVariants(
            string name,
            string character,
            string skin,
            ISet<ulong> remaining)
        {
            foreach (string path in EnumerateAnimationNameVariants(character, skin, name))
            {
                if (remaining.Remove(XxHash64Ext.Hash(PathUtils.NormalizePath(path))))
                    yield return path;
            }
        }

        private static HashSet<string> GetReusableAnimationPrefixes(IEnumerable<string> names)
        {
            var knownNames = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in knownNames)
            {
                for (int separator = name.IndexOf('_'); separator > 0; separator = name.IndexOf('_', separator + 1))
                {
                    string prefix = name[..separator];
                    string remainder = name[(separator + 1)..];
                    if (prefix.Length > 0 && prefix.Length <= 12 && prefix.All(char.IsLetterOrDigit) && remainder.Length > 0 && knownNames.Contains(remainder))
                        prefixes.Add(prefix);
                }
            }
            return prefixes;
        }

        private static void AddAnimationNameVariants(ISet<string> paths, string character, string skin, string name)
        {
            foreach (string path in EnumerateAnimationNameVariants(character, skin, name))
                paths.Add(path);
        }

        private static IEnumerable<string> EnumerateAnimationNameVariants(string character, string skin, string name)
        {
            string stem = name.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            if (string.IsNullOrWhiteSpace(stem) || stem.Contains('/') || stem.Contains('\\')) yield break;
            stem = stem.ToLowerInvariant();

            yield return $"assets/characters/{character}/skins/{skin}/animations/{stem}.anm";
            yield return $"assets/characters/{character}/skins/{skin}/animations/{character}_{stem}.anm";
            yield return $"assets/characters/{character}/skins/{skin}/animations/{character}_{skin}_{stem}.anm";
            yield return $"assets/characters/{character}/skins/{skin}/animations/{skin}_{stem}.anm";
        }

        private static string GetBasename(string path)
        {
            int separator = path.LastIndexOf('/');
            return separator >= 0 ? path[(separator + 1)..] : path;
        }

        private static IEnumerable<AnimationFileLink> EnumerateAnimationFileLinks(BinTree tree)
        {
            foreach (BinTreeObject item in tree.Objects.Values)
                if (item.Properties.TryGetValue(ClipDataMapNameHash, out BinTreeProperty property) &&
                    property is BinTreeMap map)
                    foreach (AnimationFileLink target in EnumerateAnimationFileLinks(map))
                        yield return target;

            foreach (BinTreeDataOverride item in tree.DataOverrides)
                if (item.Property is BinTreeMap map && item.Property.NameHash == ClipDataMapNameHash)
                    foreach (AnimationFileLink target in EnumerateAnimationFileLinks(map))
                        yield return target;
        }

        private static IEnumerable<AnimationFileLink> EnumerateAnimationFileLinks(BinTreeMap map)
        {
            foreach (var pair in map)
            {
                if (pair.Value is not BinTreeStruct clip ||
                    !clip.Properties.TryGetValue(AnimationResourceDataNameHash, out BinTreeProperty resource) ||
                    resource is not BinTreeStruct animationResource ||
                    !animationResource.Properties.TryGetValue(AnimationFilePathNameHash, out BinTreeProperty path) ||
                    path is not BinTreeWadChunkLink link)
                    continue;

                yield return new AnimationFileLink(
                    pair.Key is BinTreeHash hash ? hash.Value : 0,
                    link.Value);
            }
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
            foreach (HashGuessCandidate candidate in GrepFileCandidates(data))
            {
                Check(engine, candidate.Path, candidate.Strategy, string.IsNullOrWhiteSpace(path) ? source : path);
                checkedCandidates++;
                if (engine.RemainingUnknownCount == 0) break;
            }
            return checkedCandidates;
        }

        private static IEnumerable<HashGuessCandidate> ExtractLuaManifestCandidates(ArraySegment<byte> data)
        {
            using var stream = new MemoryStream(data.Array, data.Offset, data.Count, false);
            using var reader = new BinaryReader(stream, new UTF8Encoding(false, true), true);
            if (stream.Length < 8) yield break;

            reader.ReadBytes(4);
            uint characterCount = ReadManifestCount(reader);
            for (uint characterIndex = 0; characterIndex < characterCount; characterIndex++)
            {
                string character = ReadManifestString(reader).ToLowerInvariant();
                uint childCount = ReadManifestCount(reader);
                for (uint childIndex = 0; childIndex < childCount; childIndex++)
                {
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
                sharedNames.Add(ReadManifestString(reader).ToLowerInvariant());
            }

            uint hashCount = ReadManifestCount(reader);
            long hashBytes = checked((long)hashCount * sizeof(ulong));
            if (hashBytes > stream.Length - stream.Position)
                throw new InvalidDataException("Lua manifest hash table exceeds the available data.");

            var hashMap = new Dictionary<ulong, uint>((int)Math.Min(hashCount, 100_000));
            for (uint i = 0; i < hashCount; i++)
            {
                ulong entry = reader.ReadUInt64();
                uint dirIndex = (uint)(entry & 0x1F);
                ulong xxh3Truncated = entry >> 5;
                hashMap[xxh3Truncated] = dirIndex;
            }

            foreach (string name in sharedNames)
            {
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

        private static IEnumerable<HashGuessCandidate> GrepFileCandidates(ArraySegment<byte> data)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach ((int offset, int length) in FindGeneralPathRanges(data))
            {
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
            foreach (HashGuessCandidate candidate in ExpandGrepFilePath(path, HashGuessStrategy.EmbeddedPathGrep))
                if (emitted.Add(candidate.Path)) yield return candidate;
        }

        private static IEnumerable<(int Offset, int Length)> FindGeneralPathRanges(ArraySegment<byte> data)
        {
            int limit = data.Count;
            for (int offset = 0; offset < limit; offset++)
            {
                byte[][] prefixes = GetPrefixes(ByteAt(data, offset));
                if (prefixes == null) continue;
                foreach (byte[] prefix in prefixes)
                {
                    if (offset + prefix.Length > limit) continue;

                    int prefixIndex = 1;
                    while (prefixIndex < prefix.Length && ByteAt(data, offset + prefixIndex) == prefix[prefixIndex]) prefixIndex++;
                    if (prefixIndex != prefix.Length) continue;

                    int end = offset + prefix.Length;
                    while (end < limit && IsGeneralPathByte(ByteAt(data, end))) end++;
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

        private static IEnumerable<int> FindBinPathOffsets(ArraySegment<byte> data)
        {
            for (int offset = 0; offset < data.Count; offset++)
            {
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

        private static IEnumerable<IEnumerable<T>> GetCombinations<T>(IReadOnlyList<T> values, int length)
        {
            if (length == 1) return values.Select(value => new[] { value }.AsEnumerable());
            return values.SelectMany(
                (value, index) => GetCombinations(values.Skip(index + 1).ToList(), length - 1),
                (value, tail) => new[] { value }.Concat(tail));
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
