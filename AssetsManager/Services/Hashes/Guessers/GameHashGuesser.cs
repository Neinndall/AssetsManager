using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes.Guessers
{
    internal sealed class GameHashGuesser : HashGuesser
    {
        private static readonly Regex PathRegex = new(
            @"(?:assets|common|data|data_soon|gameplay|global|levels|loadouts|ux|uiautoatlas|characters|shaders|maps|clientstates|patching)/[0-9a-z_./ -]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PreloadNameRegex = new("Name=\\\"([^\\\"]+)\\\"", RegexOptions.Compiled);
        private static readonly Regex ShaderIncludeRegex = new("#include \\\"([^\\\"]+)\\\"", RegexOptions.Compiled);
        private static readonly Regex LocaleRegex = new(@"(?<![a-z])(?:ar_ae|ar_eg|cs_cz|de_de|el_gr|en_au|en_gb|en_ph|en_pl|en_sg|en_us|es_ar|es_es|es_mx|fr_fr|hu_hu|id_id|it_it|ja_jp|ko_kr|ms_my|pl_pl|pt_br|ro_ro|ru_ru|th_th|tr_tr|vi_vn|vn_vn|zh_cn|zh_my|zh_tw)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly string[] Locales = { "ar_ae", "ar_eg", "cs_cz", "de_de", "el_gr", "en_au", "en_gb", "en_ph", "en_pl", "en_sg", "en_us", "es_ar", "es_es", "es_mx", "fr_fr", "hu_hu", "id_id", "it_it", "ja_jp", "ko_kr", "ms_my", "pl_pl", "pt_br", "ro_ro", "ru_ru", "th_th", "tr_tr", "vi_vn", "vn_vn", "zh_cn", "zh_my", "zh_tw" };
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
            "data/items/spells/modules", "data/buildingblocks", "data/shared/gamemodes"
        };
        private static readonly byte[][] BinPrefixesA = ToAsciiPrefixes("ASSETS/");
        private static readonly byte[][] BinPrefixesC = ToAsciiPrefixes("COMMON/", "CHARACTERS/", "CLIENTSTATES/");
        private static readonly byte[][] BinPrefixesD = ToAsciiPrefixes("DATA/", "DATA_SOON/");
        private static readonly byte[][] BinPrefixesG = ToAsciiPrefixes("GAMEPLAY/", "GLOBAL/");
        private static readonly byte[][] BinPrefixesL = ToAsciiPrefixes("LEVELS/", "LOADOUTS/");
        private static readonly byte[][] BinPrefixesM = ToAsciiPrefixes("MAPS/");
        private static readonly byte[][] BinPrefixesP = ToAsciiPrefixes("PATCHING/");
        private static readonly byte[][] BinPrefixesS = ToAsciiPrefixes("SHADERS/");
        private static readonly byte[][] BinPrefixesU = ToAsciiPrefixes("UX/", "UIAUTOATLAS/");
        private static readonly byte[][] GeneralPathPrefixes = ToAsciiPrefixes(
            "ASSETS/", "COMMON/", "DATA/", "DATA_SOON/", "GAMEPLAY/", "GLOBAL/", "LEVELS/",
            "LOADOUTS/", "UX/", "UIAUTOATLAS/", "CHARACTERS/", "SHADERS/", "MAPS/",
            "CLIENTSTATES/", "PATCHING/");

        private static readonly HashSet<string> SkippedExtensions = new(StringComparer.Ordinal)
        {
            "dds", "jpg", "png", "tga", "ttf", "otf", "ogg", "webm", "anm", "skl", "skn",
            "scb", "sco", "troybin", "bnk", "wpk", "tex"
        };

        private readonly LogService _logService;

        internal GameHashGuesser(HashFile hashFile, LogService logService = null)
            : base(hashFile, "*.wad.client")
        {
            if (hashFile.Domain != HashGuessDomain.Game) throw new ArgumentException("GAME guesser requires a GAME hash file.", nameof(hashFile));
            _logService = logService;
        }

        internal GameHashGuesser() : this(new HashFile(HashGuessDomain.Game, Array.Empty<string>())) { }

        internal override bool ShouldSkip(string extension) => SkippedExtensions.Contains(extension);

        internal override IEnumerable<HashGuessCandidate> GenerateCanonicalCandidates(HashGuesser otherDomain, int candidateBudget = int.MaxValue)
        {
            int generated = 0;
            foreach (HashGuessCandidate candidate in GuessCharacterFiles())
            {
                yield return candidate;
                if (CountCandidate(ref generated, candidateBudget)) yield break;
            }
        }

        internal override IEnumerable<HashGuessCandidate> GenerateLanguageCandidates(int candidateBudget = int.MaxValue)
        {
            int generated = 0;
            var formats = KnownPaths.Where(path => LocaleRegex.IsMatch(path))
                .Select(path => LocaleRegex.Replace(path, "{locale}"))
                .Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal);
            foreach (string format in formats)
            foreach (string locale in Locales)
            {
                yield return new HashGuessCandidate(format.Replace("{locale}", locale, StringComparison.Ordinal), HashGuessStrategy.LanguageVariant);
                if (CountCandidate(ref generated, candidateBudget)) yield break;
            }
        }

        internal IReadOnlyList<string> GetCharacters()
        {
            var characters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in KnownPaths)
            {
                Match match = Regex.Match(path, @"^(?:assets/|data/)?characters/([^/.]+)(?:/|$)", RegexOptions.IgnoreCase);
                if (match.Success) characters.Add(match.Groups[1].Value);
            }
            return characters.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal override IReadOnlyList<string> BuildWordlist() =>
            Corpus.GetOrCreate("wordlist", HashGuessEngine.BuildWordlist);

        internal IEnumerable<HashGuessCandidate> SubstituteNumbers(int maximum = 100, int? digits = null, bool inferDigits = false) =>
            GenerateNumberCandidates(maximum, int.MaxValue, digits, inferDigits, includeCommonPadding: false);

        internal IEnumerable<HashGuessCandidate> SubstituteBasicNumbers(int maximum = 100) =>
            SubstituteNumbers(maximum).Concat(SubstituteNumbers(maximum, digits: 2));

        internal IEnumerable<HashGuessCandidate> CheckBasenamePrefixes(IEnumerable<string> prefixes = null)
        {
            string[] values = (prefixes ?? new[] { "2x_", "2x_sd_", "4x_", "4x_sd_", "sd_" }).ToArray();
            foreach (string path in KnownPaths)
            {
                int separator = path.LastIndexOf('/');
                string directory = separator >= 0 ? path[..(separator + 1)] : string.Empty;
                string basename = separator >= 0 ? path[(separator + 1)..] : path;
                foreach (string prefix in values)
                    yield return new HashGuessCandidate(directory + prefix + basename, HashGuessStrategy.PrefixVariant);
            }
        }

        internal int SubstituteBasenameWords(HashGuessEngine engine, CancellationToken cancellationToken) =>
            RunBasenameWordSubstitution(engine, KnownPaths, BuildWordlist(), 1, 1, cancellationToken, int.MaxValue, "GAME basename word substitution");

        internal int AddBasenameWord(HashGuessEngine engine, CancellationToken cancellationToken)
        {
            IEnumerable<string> paths = KnownPaths.Where(path =>
                !path.Contains("assets/characters/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("vo/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("sfx/", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("skins_skin", StringComparison.OrdinalIgnoreCase));
            return RunWordAdditionAttack(engine, paths, BuildWordlist(), cancellationToken, int.MaxValue);
        }

        internal IEnumerable<HashGuessCandidate> SubstituteCharacter() => GenerateCharacterSubstitutionCandidates(int.MaxValue);
        internal IEnumerable<HashGuessCandidate> SubstituteSkinNumbers() => GenerateSkinNumberCandidates(int.MaxValue);
        internal IEnumerable<HashGuessCandidate> SubstituteSuffixes() => GenerateSuffixCandidates(int.MaxValue);
        internal IEnumerable<HashGuessCandidate> SubstituteLang() => GenerateLanguageCandidates(int.MaxValue);

        internal IEnumerable<HashGuessCandidate> GenerateCrossDomainCandidates(HashGuesser lcuGuesser, int candidateBudget = int.MaxValue)
        {
            int generated = 0;
            foreach (string lcuPath in lcuGuesser.KnownPaths)
            {
                Match match = Regex.Match(lcuPath, @"^plugins/rcp-be-lol-game-data/global/default/((?:assets|data)/.*)\.(png|jpg|dds|json)$", RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                string path = match.Groups[1].Value;
                string extension = match.Groups[2].Value.ToLowerInvariant();
                yield return new HashGuessCandidate(path + "." + extension, HashGuessStrategy.CrossDomainGame);
                if (CountCandidate(ref generated, candidateBudget)) yield break;
                if (extension is "json" or "dds") continue;
                yield return new HashGuessCandidate(path + ".dds", HashGuessStrategy.CrossDomainGame);
                if (CountCandidate(ref generated, candidateBudget)) yield break;
            }
        }

        internal IEnumerable<HashGuessCandidate> GuessFromLcuHashes(HashGuesser lcuGuesser) =>
            GenerateCrossDomainCandidates(lcuGuesser, int.MaxValue);

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

        internal IEnumerable<HashGuessCandidate> GuessCharacterFiles(IEnumerable<string> characters = null)
        {
            foreach (string character in (characters ?? GetCharacters()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.Ordinal))
            {
                foreach (string path in new[]
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
                })
                    yield return new HashGuessCandidate(path, HashGuessStrategy.CharacterTemplate);

                int skinLimit = character.Equals("sightward", StringComparison.OrdinalIgnoreCase) ? 500 : 200;
                for (int skin = 0; skin < skinLimit; skin++)
                {
                    yield return new HashGuessCandidate($"data/characters/{character}/skins/skin{skin}.bin", HashGuessStrategy.CharacterTemplate);
                    yield return new HashGuessCandidate($"data/characters/{character}/animations/skin{skin}.bin", HashGuessStrategy.CharacterTemplate);
                }
                if (character.StartsWith("pet", StringComparison.OrdinalIgnoreCase))
                    for (int tier = 0; tier < 10; tier++)
                        yield return new HashGuessCandidate($"data/characters/{character}/tiers/tier{tier}.bin", HashGuessStrategy.CharacterTemplate);
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
            var suffixCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = int.MaxValue };
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
            var characters = new Dictionary<string, (HashSet<string> Skins, Dictionary<(string Format, int Count), (int Support, int DistinctSupport)> Formats)>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in KnownPaths)
            {
                Match directory = directoryRegex.Match(path);
                if (!directory.Success || directory.Groups[1].Value.Equals("sightward", StringComparison.OrdinalIgnoreCase)) continue;
                string character = directory.Groups[1].Value;
                if (!characters.TryGetValue(character, out var data))
                {
                    data = (new HashSet<string>(StringComparer.OrdinalIgnoreCase), new Dictionary<(string, int), (int, int)>());
                    characters[character] = data;
                }
                data.Skins.Add(directory.Groups[2].Value.ToLowerInvariant());
                MatchCollection matches = skinRegex.Matches(path);
                var format = (skinRegex.Replace(path, "{skin}"), matches.Count);
                data.Formats.TryGetValue(format, out var support);
                bool distinct = matches.Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() == matches.Count;
                data.Formats[format] = (support.Support + 1, support.DistinctSupport + (distinct ? 1 : 0));
            }
            int generated = 0;
            var formats = characters.SelectMany(character => character.Value.Formats.Select(format => new
            {
                Character = character.Key, character.Value.Skins, Format = format.Key.Format, Count = format.Key.Count,
                Support = format.Value.Support, DistinctSupport = format.Value.DistinctSupport
            })).OrderBy(value => value.DistinctSupport > 0 ? 0 : 1)
                .ThenBy(value => value.Count == 2 ? 0 : value.Count == 1 ? 1 : value.Count)
                .ThenByDescending(value => value.DistinctSupport).ThenByDescending(value => value.Support)
                .ThenBy(value => value.Character, StringComparer.Ordinal).ThenBy(value => value.Format, StringComparer.Ordinal);
            foreach (var format in formats)
            {
                List<string> skins = format.Skins.OrderBy(value => value, StringComparer.Ordinal).ToList();
                if (format.Count > skins.Count) continue;
                foreach (IEnumerable<string> permutation in GetPermutations(skins, format.Count))
                {
                    string candidate = format.Format;
                    foreach (string skin in permutation)
                    {
                        int marker = candidate.IndexOf("{skin}", StringComparison.Ordinal);
                        candidate = candidate[..marker] + skin + candidate[(marker + 6)..];
                    }
                    yield return new HashGuessCandidate(candidate, HashGuessStrategy.SkinNumberVariant);
                    if (CountCandidate(ref generated, candidateBudget)) yield break;
                }
            }
        }

        internal IEnumerable<HashGuessCandidate> GeneratePrefixAndShaderCandidates(int candidateBudget = int.MaxValue)
        {
            int generated = 0;
            foreach (HashGuessCandidate candidate in CheckBasenamePrefixes(new[] { "tft_", "2x_", "2x_sd_", "4x_", "4x_sd_", "sd_" }).Concat(GuessShaderVariants()))
            {
                yield return candidate;
                if (CountCandidate(ref generated, candidateBudget)) yield break;
            }
        }

        internal IEnumerable<HashGuessCandidate> GuessShaderVariants()
        {
            var shaderPaths = new HashSet<string>(StringComparer.Ordinal);
            var regex = new Regex(@".*\.[pv]s(?:_[23]_0|(?=$|\.))", RegexOptions.IgnoreCase);
            foreach (string path in KnownPaths)
            {
                Match match = regex.Match(path);
                if (match.Success) shaderPaths.Add(match.Value);
            }
            foreach (string path in shaderPaths.OrderBy(value => value, StringComparer.Ordinal))
            foreach (string variant in ShaderVariants)
            {
                yield return new HashGuessCandidate(path + variant, HashGuessStrategy.ShaderVariant);
                for (int number = 0; number < 20000; number += 100)
                    yield return new HashGuessCandidate($"{path}{variant}_{number}", HashGuessStrategy.ShaderVariant);
            }
        }

        internal int RunCrossDomainAttacks(HashGuessEngine engine, HashGuesser lcuGuesser, CancellationToken cancellationToken)
        {
            int checkedCandidates = CheckCandidates(engine, GuessFromLcuHashes(lcuGuesser), "LCU to GAME", cancellationToken);
            if (engine.RemainingUnknownCount > 0)
                checkedCandidates += CheckCandidates(engine, GenerateExtensionCandidates(int.MaxValue), "GAME extension substitution", cancellationToken);
            if (engine.RemainingUnknownCount > 0)
                checkedCandidates += CheckCandidates(engine, GeneratePrefixAndShaderCandidates(), "GAME prefix or shader variant", cancellationToken);
            return checkedCandidates;
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
                   RunWordAdditionAttack(engine, paths, words, cancellationToken);
        }

        internal async Task<int> RunExtendedAttacksAsync(
            HashGuessEngine engine,
            string rootDirectory,
            IEnumerable<string> binEntryPaths,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken)
        {
            int checkedCandidates = 0;
            var paths = KnownPaths;

            checkedCandidates += CheckCandidates(engine, SubstituteSkinNumbers(), "GAME skin number combinations", cancellationToken);
            if (engine.RemainingUnknownCount > 0)
                checkedCandidates += CheckCandidates(engine, SubstituteCharacter(), "GAME character substitution", cancellationToken, progress, checkedCandidates);
            if (engine.RemainingUnknownCount > 0)
                checkedCandidates += CheckCandidates(engine, SubstituteSuffixes(), "GAME suffix substitution", cancellationToken);

            if (engine.RemainingUnknownCount > 0)
            {
                progress?.Report(engine.CreateProgress("BIN entries to GAME", checkedCandidates));
                checkedCandidates += CheckCandidates(engine, GuessFromBinEntryBasenames(binEntryPaths), "BIN entries to GAME", cancellationToken, progress, checkedCandidates);
            }

            if (engine.RemainingUnknownCount > 0)
                checkedCandidates += SubstituteBasenameWords(engine, cancellationToken);
            if (engine.RemainingUnknownCount > 0)
                checkedCandidates += AddBasenameWord(engine, cancellationToken);

            if (engine.RemainingUnknownCount > 0)
            {
                progress?.Report(engine.CreateProgress("Focused Attack: Bin paths", checkedCandidates));
                var binPaths = Corpus.GetOrCreate("bin-paths", values => values.Where(path => path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)).ToList());
                checkedCandidates += RunFocusedWordlistSubstitution(engine, binPaths.Take(25000), HashGuessEngine.BuildBasenameWordlist(binPaths).Take(20000), cancellationToken);

                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(engine.CreateProgress("Focused Attack: Data bin paths", checkedCandidates));
                    var dataBins = Corpus.GetOrCreate("data-bin-paths", values => values.Where(path => path.StartsWith("data/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)).ToList());
                    checkedCandidates += RunFocusedWordlistSubstitution(engine, dataBins.Take(25000), HashGuessEngine.BuildBasenameWordlist(dataBins).Take(20000), cancellationToken);
                }
                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(engine.CreateProgress("Focused Attack: Characters DDS paths", checkedCandidates));
                    var ddsPaths = Corpus.GetOrCreate("character-dds-paths", values => values.Where(path => path.StartsWith("assets/characters/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)).ToList());
                    checkedCandidates += RunFocusedWordlistSubstitution(engine, ddsPaths.Take(25000), HashGuessEngine.BuildBasenameWordlist(ddsPaths).Take(20000), cancellationToken);
                }
                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(engine.CreateProgress("Focused Attack: Characters TEX paths", checkedCandidates));
                    var texPaths = Corpus.GetOrCreate("character-tex-paths", values => values.Where(path => path.StartsWith("assets/characters/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)).ToList());
                    checkedCandidates += RunFocusedWordlistSubstitution(engine, texPaths.Take(25000), HashGuessEngine.BuildBasenameWordlist(texPaths).Take(20000), cancellationToken);
                }
                if (engine.RemainingUnknownCount > 0)
                {
                    progress?.Report(engine.CreateProgress("Focused Attack: Word insertions", checkedCandidates));
                    var additionPaths = paths.Where(path => !path.Contains("assets/characters/", StringComparison.OrdinalIgnoreCase) &&
                        !path.Contains("vo/", StringComparison.OrdinalIgnoreCase) && !path.Contains("sfx/", StringComparison.OrdinalIgnoreCase) &&
                        !path.Contains("skins_skin", StringComparison.OrdinalIgnoreCase));
                    checkedCandidates += RunWordAdditionAttack(engine, additionPaths.Take(20000), HashGuessEngine.BuildBasenameWordlist(paths).Take(20000), cancellationToken);
                }
            }

            checkedCandidates += await GuessSkinGroupsBinUsingChromas(engine, rootDirectory, cancellationToken);
            checkedCandidates += await GuessSkinGroupsBin(engine, cancellationToken);
            checkedCandidates += RunEsportsBannersAttack(engine, rootDirectory, cancellationToken);

            if (engine.RemainingUnknownCount > 0)
            {
                progress?.Report(engine.CreateProgress("GAME Cartesian Cross", checkedCandidates));
                checkedCandidates += SubstituteBasenames(engine, cancellationToken);
            }
            return checkedCandidates;
        }

        private static int CheckCandidates(
            HashGuessEngine engine,
            IEnumerable<HashGuessCandidate> candidates,
            string source,
            CancellationToken cancellationToken,
            IProgress<HashGuessProgress> progress = null,
            int progressOffset = 0)
        {
            int checkedCount = 0;
            foreach (HashGuessCandidate candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                engine.Check(candidate.Path, candidate.Strategy, source);
                checkedCount++;
                if (checkedCount % 5000 == 0)
                    progress?.Report(engine.CreateProgress(source, progressOffset + checkedCount));
                if (engine.RemainingUnknownCount == 0) break;
            }
            return checkedCount;
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
                        string suffix = string.Concat(combination.SelectMany(value => value).OrderBy(value => value, StringComparer.Ordinal));
                        engine.Check("data/" + pair.Key + suffix + ".bin", HashGuessStrategy.ChromaGroupVariant, "Local skins.json chroma groups");
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
                foreach (var pair in characters)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var skins = pair.Value.Select(value => $"_skins_skin{value}").OrderBy(value => value).ToList();
                    for (int length = 1; length <= skins.Count; length++)
                    foreach (IEnumerable<string> combination in GetCombinations(skins, length))
                    {
                        engine.Check($"data/{pair.Key}{string.Concat(combination)}.bin", HashGuessStrategy.ChromaGroupVariant, "Local skin groups");
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

        internal override void GrepWad(HashGuessEngine engine, ArraySegment<byte> data, string sourcePath, string sourceWadPath, ulong sourceChunkHash) =>
            CheckChunk(engine, data, sourcePath, sourceWadPath, sourceChunkHash);

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
                engine.Check(candidate.Path, candidate.Strategy, string.IsNullOrWhiteSpace(path) ? source : path);
                checkedCandidates++;
                if (engine.RemainingUnknownCount == 0) break;
            }
            return checkedCandidates;
        }

        protected override IEnumerable<HashGuessCandidate> ExtractCandidates(ArraySegment<byte> data, string sourcePath)
        {
            if (data.Count == 0) yield break;

            if (sourcePath.Equals("data/all_lua_files.manifest", StringComparison.OrdinalIgnoreCase))
            {
                foreach (HashGuessCandidate candidate in ExtractLuaManifestCandidates(data))
                    yield return candidate;
                yield break;
            }

            string extension = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
            if (extension is "bin" or "inibin")
            {
                foreach (int offset in FindBinPathOffsets(data))
                {
                    if (offset < 2) continue;
                    int length = ByteAt(data, offset - 2) | (ByteAt(data, offset - 1) << 8);
                    if (length <= 0 || offset + length > data.Count) continue;
                    string path = NormalizePath(Encoding.ASCII.GetString(data.Array, data.Offset + offset, length));
                    foreach (HashGuessCandidate candidate in ExpandGamePath(path, HashGuessStrategy.BinLengthPath))
                        yield return candidate;
                }
                yield break;
            }

            if (!TryDecodeWadText(data, out string text))
                text = Encoding.ASCII.GetString(data.Array, data.Offset, data.Count);
            if (extension == "preload")
            {
                string directory = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? string.Empty;
                foreach (Match match in PreloadNameRegex.Matches(text))
                {
                    string path = NormalizePath(match.Groups[1].Value);
                    foreach (HashGuessCandidate candidate in ExpandGamePath(path, HashGuessStrategy.PreloadReference))
                        yield return candidate;

                    if (path.EndsWith(".troy", StringComparison.OrdinalIgnoreCase))
                        yield return new HashGuessCandidate($"data/shared/particles/{path[..^5]}.troybin", HashGuessStrategy.PreloadReference);
                    if (!string.IsNullOrEmpty(directory))
                        yield return new HashGuessCandidate(directory + "/" + path + ".preload", HashGuessStrategy.PreloadReference);
                }
                yield break;
            }

            if (extension is "hls" or "ps_2_0" or "ps_3_0" or "vs_2_0" or "vs_3_0")
            {
                string directory = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? string.Empty;
                foreach (Match match in ShaderIncludeRegex.Matches(text))
                    yield return new HashGuessCandidate(NormalizePath(Path.Combine(directory, match.Groups[1].Value)), HashGuessStrategy.ShaderInclude);
                yield break;
            }

            if (extension == "atlas")
            {
                string directory = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? string.Empty;
                foreach (string line in text.Split('\n'))
                {
                    string candidate = NormalizePath(Path.Combine(directory, line.Trim()));
                    if (candidate.Length > 0)
                        yield return new HashGuessCandidate(candidate, HashGuessStrategy.AtlasReference);
                }
                yield break;
            }

            foreach (HashGuessCandidate candidate in GrepFileCandidates(data))
                yield return candidate;
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
            for (uint sharedIndex = 0; sharedIndex < sharedCount; sharedIndex++)
            {
                string name = ReadManifestString(reader).ToLowerInvariant();
                foreach (string prefix in LuaCommonPaths)
                foreach (string extension in LuaExtensions)
                    yield return new HashGuessCandidate($"{prefix}/{name}.{extension}", HashGuessStrategy.LuaManifest);

                for (int map = 0; map < 1000; map++)
                foreach (string prefix in new[] { string.Empty, "mutators/" })
                    yield return new HashGuessCandidate(
                        $"levels/map{map}/scripts/{prefix}{name}.luabin64",
                        HashGuessStrategy.LuaManifest);
            }

            uint hashCount = ReadManifestCount(reader);
            long hashBytes = checked((long)hashCount * sizeof(ulong));
            if (hashBytes > stream.Length - stream.Position)
                throw new InvalidDataException("Lua manifest hash table exceeds the available data.");
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
                string path = NormalizePath(Encoding.ASCII.GetString(data.Array, data.Offset + offset, length));
                if (path.Length > 0) paths.Add(path);

                if (offset < 2) continue;
                int encodedLength = ByteAt(data, offset - 2) | (ByteAt(data, offset - 1) << 8);
                if (encodedLength == 0 && offset >= 4)
                    encodedLength = ByteAt(data, offset - 4) | (ByteAt(data, offset - 3) << 8) |
                                    (ByteAt(data, offset - 2) << 16) | (ByteAt(data, offset - 1) << 24);
                if (encodedLength <= 0 || encodedLength >= length || offset + encodedLength > data.Count) continue;
                string shortened = NormalizePath(Encoding.ASCII.GetString(data.Array, data.Offset + offset, encodedLength));
                if (shortened.Length > 0) paths.Add(shortened);
            }

            var emitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in paths)
            foreach (HashGuessCandidate candidate in ExpandGamePath(path, HashGuessStrategy.EmbeddedPathGrep))
                if (emitted.Add(candidate.Path)) yield return candidate;
        }

        private static IEnumerable<(int Offset, int Length)> FindGeneralPathRanges(ArraySegment<byte> data)
        {
            int limit = data.Count;
            for (int offset = 0; offset < limit; offset++)
            {
                byte firstByte = ToUpperAscii(ByteAt(data, offset));

                foreach (byte[] prefix in GeneralPathPrefixes)
                {
                    if (prefix[0] != firstByte || offset + prefix.Length > limit) continue;

                    int prefixIndex = 1;
                    while (prefixIndex < prefix.Length && ToUpperAscii(ByteAt(data, offset + prefixIndex)) == prefix[prefixIndex]) prefixIndex++;
                    if (prefixIndex != prefix.Length) continue;

                    int end = offset + prefix.Length;
                    while (end < limit && IsGeneralPathByte(ByteAt(data, end))) end++;
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

        private static IEnumerable<HashGuessCandidate> ExpandGamePath(string path, HashGuessStrategy strategy)
        {
            if (path.Length == 0) yield break;
            yield return new HashGuessCandidate(path, strategy);

            if (path.Contains("data_soon/", StringComparison.OrdinalIgnoreCase))
                yield return new HashGuessCandidate(Regex.Replace(path, "data_soon/", "data/", RegexOptions.IgnoreCase), strategy);

            if (path.StartsWith("characters/", StringComparison.OrdinalIgnoreCase))
            {
                yield return new HashGuessCandidate($"assets/{path}", strategy);
                yield return new HashGuessCandidate($"data/{path}", strategy);
            }

            if (path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            {
                string prefix = path[..^4];
                yield return new HashGuessCandidate(prefix + ".luabin", HashGuessStrategy.LuaVariant);
                yield return new HashGuessCandidate(prefix + ".luabin64", HashGuessStrategy.LuaVariant);
                yield return new HashGuessCandidate(prefix + ".preload", HashGuessStrategy.LuaVariant);
            }
            else if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                yield return new HashGuessCandidate(path[..^4] + ".dds", HashGuessStrategy.ImageExtensionVariant);
            }
            else if (path.StartsWith("maps/mapgeometry/", StringComparison.OrdinalIgnoreCase))
            {
                yield return new HashGuessCandidate($"data/{path}.mapgeo", strategy);
                yield return new HashGuessCandidate($"data/{path}.materials.bin", strategy);
            }

            if (path.StartsWith("clientstates/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("patching/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("loadouts/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
            {
                int separator = path.LastIndexOf('/');
                if (separator > 0)
                {
                    string parent = path[..separator];
                    yield return new HashGuessCandidate(parent, strategy);
                    int parentSeparator = parent.LastIndexOf('/');
                    if (parentSeparator > 0)
                        yield return new HashGuessCandidate(parent[..parentSeparator], strategy);
                }
            }

            if (!path.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase)) yield break;
            foreach (string extension in ShaderExtensions)
            {
                yield return new HashGuessCandidate($"assets/shaders/generated/{path}{extension}", strategy);
                foreach (string variant in ShaderVariants)
                {
                    yield return new HashGuessCandidate($"assets/shaders/generated/{path}{extension}{variant}", strategy);
                }
            }
        }

        private static IReadOnlyList<int> FindBinPathOffsets(ArraySegment<byte> data)
        {
            var offsets = new HashSet<int>();
            for (int offset = 0; offset < data.Count; offset++)
            {
                byte[][] needles = ToUpperAscii(ByteAt(data, offset)) switch
                {
                    (byte)'A' => BinPrefixesA,
                    (byte)'C' => BinPrefixesC,
                    (byte)'D' => BinPrefixesD,
                    (byte)'G' => BinPrefixesG,
                    (byte)'L' => BinPrefixesL,
                    (byte)'M' => BinPrefixesM,
                    (byte)'P' => BinPrefixesP,
                    (byte)'S' => BinPrefixesS,
                    (byte)'U' => BinPrefixesU,
                    _ => null
                };
                if (needles == null) continue;
                foreach (byte[] needle in needles)
                {
                    if (offset + needle.Length > data.Count) continue;
                    int index = 1;
                    while (index < needle.Length && ToUpperAscii(ByteAt(data, offset + index)) == needle[index]) index++;
                    if (index == needle.Length) offsets.Add(offset);
                }
            }
            return offsets.OrderBy(offset => offset).ToList();
        }

        private static byte ToUpperAscii(byte value) => value is >= (byte)'a' and <= (byte)'z'
            ? (byte)(value - ('a' - 'A'))
            : value;

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
