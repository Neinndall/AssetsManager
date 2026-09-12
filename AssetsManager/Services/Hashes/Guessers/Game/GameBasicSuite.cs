using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models.Hashes;

namespace AssetsManager.Services.Hashes.Guessers.Game
{
    internal sealed partial class GameHashGuesser
    {
        internal IReadOnlyList<string> GetCharacters() =>
            Corpus.GetOrCreate("characters", values => values
                .Select(path => Regex.Match(path, @"^(?:assets/|data/)?characters/([^/.]+)(?:/|$)", RegexOptions.IgnoreCase))
                .Where(match => match.Success)
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList());

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

            string[] values = (prefixes ?? new[] { "2x_", "2x_sd_", "4x_", "4x_sd_", "sd_", "tft_", "common_", "base_", "sru_", "icon_" })
                .Select(prefix => prefix ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            int checkedCount = 0;
            foreach (string path in KnownPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int separator = path.LastIndexOf('/');
                ReadOnlySpan<char> directory = separator >= 0 ? path.AsSpan(0, separator + 1) : ReadOnlySpan<char>.Empty;
                ReadOnlySpan<char> basename = separator >= 0 ? path.AsSpan(separator + 1) : path.AsSpan();

                foreach (string prefix in values)
                {
                    engine.CheckNormalizedParts(
                        directory,
                        prefix.AsSpan(),
                        basename,
                        HashGuessStrategy.PrefixVariant,
                        "GAME basename prefixes");
                    checkedCount++;
                    if ((checkedCount & 0x3FFF) == 0) progress?.Invoke(checkedCount);
                    if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) return checkedCount;
                }
            }

            return checkedCount;
        }

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

            IReadOnlyList<string> formats = Corpus.GetOrCreate("game-locale-formats", known => known
                .Where(path => langsRegex.IsMatch(path))
                .Select(path => langsRegex.Replace(path, "{}"))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList());

            int checkedCount = 0;
            foreach (string format in ProgressIterator(formats, value => value, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int marker = format.IndexOf("{}", StringComparison.Ordinal);
                if (marker < 0) continue;
                ReadOnlySpan<char> formatPrefix = format.AsSpan(0, marker);
                ReadOnlySpan<char> formatSuffix = format.AsSpan(marker + 2);

                foreach (string lang in langs)
                {
                    engine.CheckNormalizedParts(
                        formatPrefix,
                        lang.AsSpan(),
                        formatSuffix,
                        HashGuessStrategy.LanguageVariant,
                        source);
                    checkedCount++;
                }

                if ((checkedCount & 0x3FFF) == 0) progress?.Invoke(checkedCount);
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
            const string pluginPrefix = "plugins/rcp-be-lol-game-data/global/default/";
            int checkedCount = 0;
            foreach (string lcuPath in lcuGuesser.KnownPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) break;

                if (!lcuPath.StartsWith(pluginPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                ReadOnlySpan<char> rel = lcuPath.AsSpan(pluginPrefix.Length);
                if (!rel.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) &&
                    !rel.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) continue;

                int dot = rel.LastIndexOf('.');
                if (dot <= 0) continue;

                ReadOnlySpan<char> stem = rel[..dot];
                ReadOnlySpan<char> ext = rel[(dot + 1)..];

                if (ext.Equals("png", StringComparison.OrdinalIgnoreCase) || ext.Equals("jpg", StringComparison.OrdinalIgnoreCase))
                {
                    engine.CheckNormalizedParts(stem, ReadOnlySpan<char>.Empty, ".dds".AsSpan(), HashGuessStrategy.CrossDomainGame, source);
                    checkedCount++;
                }
                else if (ext.Equals("json", StringComparison.OrdinalIgnoreCase))
                {
                    engine.CheckNormalizedParts(stem, ReadOnlySpan<char>.Empty, ".json".AsSpan(), HashGuessStrategy.CrossDomainGame, source);
                    checkedCount++;
                }

                if ((checkedCount & 0x1fff) == 0)
                {
                    progress?.Invoke(checkedCount);
                }
            }

            progress?.Invoke(checkedCount);
            return checkedCount;
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

            IReadOnlyList<string> HudIcons2dBasenames = Corpus.GetOrCreate(
                "hud-icons2d-basenames",
                knownPaths => knownPaths
                    .Where(path => path.Contains("/hud/icons2d/", StringComparison.OrdinalIgnoreCase))
                    .Select(path => path[(path.LastIndexOf('/') + 1)..])
                    .Where(baseName => baseName.Contains('.'))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList());
            IReadOnlyDictionary<string, List<string>> HudChampStemFiles = Corpus.GetOrCreate(
                "hud-champ-stem-files",
                knownPaths => BuildHudChampStemFiles(knownPaths));
            IReadOnlyDictionary<string, RecallContext> RecallContexts = Corpus.GetOrCreate(
                "recall-contexts",
                BuildRecallContexts);

            int checkedCount = 0;
            const string source = "GAME character files";
            const HashGuessStrategy strategy = HashGuessStrategy.CharacterTemplate;
            Span<char> pathBuf = stackalloc char[384];
            int w = 0;
            var emittedCharacterPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            bool CheckSpan(ReadOnlySpan<char> path)
            {
                if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) return false;
                cancellationToken.ThrowIfCancellationRequested();
                engine.CheckNormalizedPath(path, strategy, source);
                checkedCount++;
                if ((checkedCount & 0x3FFF) == 0) progress?.Invoke(checkedCount);
                return true;
            }

            int CheckCharacterPaths(IEnumerable<string> paths)
            {
                int remaining = candidateBudget == int.MaxValue ? int.MaxValue : candidateBudget - checkedCount;
                if (remaining <= 0 || engine.RemainingUnknownCount == 0) return 0;
                IEnumerable<HashGuessCandidate> candidatesToCheck = paths.Select(
                    path => new HashGuessCandidate(path, HashGuessStrategy.CharacterTemplate))
                    .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Path) && emittedCharacterPaths.Add(candidate.Path));
                if (remaining != int.MaxValue) candidatesToCheck = candidatesToCheck.Take(remaining);
                int checkedPaths = CheckIter(engine, candidatesToCheck, source, cancellationToken);
                progress?.Invoke(checkedCount + checkedPaths);
                return checkedPaths;
            }

            ReadOnlySpan<string> abilities = ["", "p", "q", "w", "e", "r"];
            ReadOnlySpan<string> numbers = ["", "1", "2", "3", "4"];
            ReadOnlySpan<string> suffixes = ["", "_passive"];
            ReadOnlySpan<string> tiers = ["starter", "signature", "premium", "base"];
            const int nskins = 400;

            foreach (string character in ProgressIterator(characterList, value => value, cancellationToken))
            {
                if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) break;

                // Root and base files
                ReadOnlySpan<string> fixedPatterns = [
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
                    $"assets/characters/{character}/skins/base/{character}_base_tx_gm.tex",
                    $"assets/characters/{character}/skins/base/{character}loadscreen.tex",
                    $"assets/characters/{character}/skins/base/{character}_loadscreen.tex",
                    $"characters/{character}"
                ];
                foreach (string fixedPath in fixedPatterns)
                {
                    if (!CheckSpan(fixedPath.AsSpan())) goto ChampionDone;
                }

                // Skins, animations, HUD icons, loadscreens and textures
                for (int skin = 0; skin < nskins; skin++)
                {
                    if (pathBuf.TryWrite(CultureInfo.InvariantCulture, $"data/characters/{character}/skins/skin{skin}.bin", out w) && !CheckSpan(pathBuf[..w])) goto ChampionDone;
                    if (pathBuf.TryWrite(CultureInfo.InvariantCulture, $"data/characters/{character}/animations/skin{skin}.bin", out w) && !CheckSpan(pathBuf[..w])) goto ChampionDone;
                    if (pathBuf.TryWrite(CultureInfo.InvariantCulture, $"assets/characters/{character}/hud/{character}_circle_{skin}.tex", out w) && !CheckSpan(pathBuf[..w])) goto ChampionDone;
                    if (pathBuf.TryWrite(CultureInfo.InvariantCulture, $"assets/characters/{character}/hud/{character}_square_{skin}.tex", out w) && !CheckSpan(pathBuf[..w])) goto ChampionDone;
                    if (pathBuf.TryWrite(CultureInfo.InvariantCulture, $"assets/characters/{character}/skins/skin{skin:D2}/{character}loadscreen_{skin}.tex", out w) && !CheckSpan(pathBuf[..w])) goto ChampionDone;
                    if (pathBuf.TryWrite(CultureInfo.InvariantCulture, $"assets/characters/{character}/skins/skin{skin:D2}/{character}_loadscreen_{skin}.tex", out w) && !CheckSpan(pathBuf[..w])) goto ChampionDone;
                    if (pathBuf.TryWrite(CultureInfo.InvariantCulture, $"assets/characters/{character}/skins/skin{skin:D2}/{character}loadscreen_{skin}_le.tex", out w) && !CheckSpan(pathBuf[..w])) goto ChampionDone;
                    if (pathBuf.TryWrite(CultureInfo.InvariantCulture, $"assets/characters/{character}/skins/skin{skin:D2}/{character}_loadscreen_{skin}_le.tex", out w) && !CheckSpan(pathBuf[..w])) goto ChampionDone;
                    if (pathBuf.TryWrite(CultureInfo.InvariantCulture, $"assets/characters/{character}/skins/skin{skin:D2}/{character}_skin{skin:D2}_tx_cm.tex", out w) && !CheckSpan(pathBuf[..w])) goto ChampionDone;

                    foreach (string tier in tiers)
                    {
                        if (pathBuf.TryWrite(CultureInfo.InvariantCulture, $"assets/characters/{character}/skins/skin{skin:D2}/ui/{character}_skin{skin:D2}_loadscreen_augments_border_{tier}.tex", out w) && !CheckSpan(pathBuf[..w])) goto ChampionDone;
                    }

                    if (skin is >= 1 and <= 9)
                    {
                        if (pathBuf.TryWrite(CultureInfo.InvariantCulture, $"data/characters/{character}/skins/skin{skin:D2}.bin", out w) && !CheckSpan(pathBuf[..w])) goto ChampionDone;
                        if (pathBuf.TryWrite(CultureInfo.InvariantCulture, $"data/characters/{character}/animations/skin{skin:D2}.bin", out w) && !CheckSpan(pathBuf[..w])) goto ChampionDone;
                        foreach (string tier in tiers)
                        {
                            if (pathBuf.TryWrite(CultureInfo.InvariantCulture, $"assets/characters/{character}/skins/skin{skin}/ui/{character}_skin{skin}_loadscreen_augments_border_{tier}.tex", out w) && !CheckSpan(pathBuf[..w])) goto ChampionDone;
                        }
                    }
                }

                // Ability icons
                foreach (string ability in abilities)
                {
                    foreach (string number in numbers)
                    {
                        foreach (string suffix in suffixes)
                        {
                            if (pathBuf.TryWrite(CultureInfo.InvariantCulture, $"assets/characters/{character}/hud/icons2d/{character}_{ability}{number}{suffix}.dds", out w))
                                if (!CheckSpan(pathBuf[..w])) goto ChampionDone;
                        }
                    }
                }

                // Shared HUD icons
                foreach (string baseName in HudIcons2dBasenames)
                {
                    if (pathBuf.TryWrite(CultureInfo.InvariantCulture, $"assets/characters/{character}/hud/icons2d/{baseName}", out w))
                        if (!CheckSpan(pathBuf[..w])) goto ChampionDone;
                }

                // Jade HUD files
                if (!character.StartsWith("jade_", StringComparison.OrdinalIgnoreCase) &&
                    HudChampStemFiles.TryGetValue(character, out List<string> hudStemFiles))
                {
                    const string hudMarker = "/hud/";
                    foreach (string knownPath in hudStemFiles)
                    {
                        int marker = knownPath.IndexOf(hudMarker, StringComparison.OrdinalIgnoreCase);
                        if (marker >= 0)
                        {
                            ReadOnlySpan<char> suffix = knownPath.AsSpan(marker + hudMarker.Length - 1);
                            if (pathBuf.TryWrite(CultureInfo.InvariantCulture, $"assets/characters/jade_{character}{suffix}", out w))
                                if (!CheckSpan(pathBuf[..w])) goto ChampionDone;
                        }
                    }
                }

                // Recall textures
                if (RecallContexts.TryGetValue(character, out RecallContext recallContext))
                {
                    checkedCount += CheckCharacterPaths(
                        recallContext.Lines.SelectMany(WalkRecallTextureLine));
                    if (recallContext.DarkFolders.Count > 0 && recallContext.Themes.Count > 0)
                    {
                        checkedCount += CheckCharacterPaths(
                            from folder in recallContext.DarkFolders
                            from theme in recallContext.Themes
                            from number in Enumerable.Range(1, 5)
                            from candidate in RecallFileCandidates(folder, $"{character}_{theme}", number)
                            select candidate);
                    }
                    if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) break;
                }

                // Pet tiers and themes
                if (character.StartsWith("pet", StringComparison.OrdinalIgnoreCase))
                {
                    for (int tier = 0; tier < 10; tier++)
                    {
                        if (pathBuf.TryWrite(CultureInfo.InvariantCulture, $"data/characters/{character}/tiers/tier{tier}.bin", out w))
                            if (!CheckSpan(pathBuf[..w])) goto ChampionDone;
                    }

                    IReadOnlyDictionary<string, IReadOnlyList<string>> themesByPet =
                        GetDynamicPetThemeNames(cancellationToken);
                    IReadOnlyList<string> petThemes = themesByPet.TryGetValue(
                        character,
                        out IReadOnlyList<string> observedThemes)
                        ? observedThemes
                        : new[] { "base" };

                    foreach (string theme in petThemes)
                    {
                        if (pathBuf.TryWrite(CultureInfo.InvariantCulture, $"data/characters/{character}/themes/{theme}/root.bin", out w))
                            if (!CheckSpan(pathBuf[..w])) goto ChampionDone;

                        for (int tier = 1; tier <= 3; tier++)
                        {
                            if (pathBuf.TryWrite(CultureInfo.InvariantCulture, $"data/characters/{character}/themes/{theme}/tier{tier}.bin", out w))
                                if (!CheckSpan(pathBuf[..w])) goto ChampionDone;
                        }
                    }
                }

            ChampionDone:
                if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) break;
            }

            progress?.Invoke(checkedCount);
            return checkedCount;
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

            var shaderPattern = new Regex(@".*\.(?:[pv]s(?:_[23]_0)?|cs)(?=$|[.-])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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

        private static Dictionary<string, List<string>> BuildHudChampStemFiles(IReadOnlyList<string> knownPaths)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in knownPaths)
            {
                const string marker = "assets/characters/";
                if (!path.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                    continue;
                string rest = path[marker.Length..];
                int slash = rest.IndexOf('/');
                if (slash <= 0)
                    continue;
                string champ = rest[..slash];
                if (champ.StartsWith("jade_", StringComparison.OrdinalIgnoreCase) ||
                    champ.StartsWith("pet", StringComparison.OrdinalIgnoreCase))
                    continue;
                int hud = rest.IndexOf("/hud/", StringComparison.OrdinalIgnoreCase);
                if (hud < 0)
                    continue;
                string file = rest[(rest.LastIndexOf('/') + 1)..];
                if (!file.StartsWith(champ, StringComparison.OrdinalIgnoreCase) || !file.Contains('.'))
                    continue;
                if (!result.TryGetValue(champ, out List<string> files))
                    result[champ] = files = new List<string>();
                files.Add(path);
            }

            return result;
        }

        private sealed record RecallTextureLine(string Folder, string Stem, int SkinNumber, int RecallNumber, int SkinWidth);

        private sealed record RecallContext(List<RecallTextureLine> Lines, List<string> DarkFolders, List<string> Themes);

        private static Dictionary<string, RecallContext> BuildRecallContexts(IReadOnlyList<string> knownPaths)
        {
            var linePattern = new Regex(
                @"^assets/characters/(?<champ>[^/]+)/skins/(?<folder>skin(?<folderNum>\d+))/(?:(?<hd>2x_|4x_))?(?<stem>.+)_rc(?<recallNum>\d+)_tx_(?<map>cm|gm|rm)\.(?<ext>tex|dds)$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var lines = new Dictionary<string, List<RecallTextureLine>>(StringComparer.OrdinalIgnoreCase);
            var folderCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var folderChamp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var champThemes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in knownPaths)
            {
                string normalized = path.Replace('\\', '/');
                Match lineMatch = normalized.Contains("_rc", StringComparison.OrdinalIgnoreCase)
                    ? linePattern.Match(normalized)
                    : Match.Empty;
                if (lineMatch.Success &&
                    int.TryParse(lineMatch.Groups["folderNum"].Value, out int folderNum) &&
                    int.TryParse(lineMatch.Groups["recallNum"].Value, out int recallNum))
                {
                    string lineChamp = lineMatch.Groups["champ"].Value.ToLowerInvariant();
                    if (!lines.TryGetValue(lineChamp, out List<RecallTextureLine> champLines))
                        lines[lineChamp] = champLines = new List<RecallTextureLine>();
                    string lineFolder = $"assets/characters/{lineMatch.Groups["champ"].Value}/skins/{lineMatch.Groups["folder"].Value}/";
                    string stem = lineMatch.Groups["stem"].Value;
                    if (!champLines.Any(line => line.Folder.Equals(lineFolder, StringComparison.OrdinalIgnoreCase) &&
                                                line.Stem.Equals(stem, StringComparison.OrdinalIgnoreCase)))
                        champLines.Add(new RecallTextureLine(
                            lineFolder,
                            stem,
                            folderNum,
                            recallNum,
                            lineMatch.Groups["folder"].Value.Length - "skin".Length));
                }

                if (!normalized.StartsWith("assets/characters/", StringComparison.OrdinalIgnoreCase))
                    continue;
                string[] parts = normalized.Split('/');
                if (parts.Length < 5 || !parts[3].Equals("skins", StringComparison.OrdinalIgnoreCase))
                    continue;
                string champ = parts[2].ToLowerInvariant();
                string folder = string.Join('/', parts[..5]) + "/";
                folderCounts[folder] = folderCounts.TryGetValue(folder, out int count) ? count + 1 : 1;
                folderChamp.TryAdd(folder, champ);

                if (parts.Length >= 7 && parts[5].Equals("animations", StringComparison.OrdinalIgnoreCase) &&
                    parts[^1].EndsWith(".anm", StringComparison.OrdinalIgnoreCase))
                {
                    // Theme words sit between the champion stem and the trailing action
                    // (jade_nami_koi_attack1 -> koi); the action itself is never a theme.
                    string[] tokens = parts[^1][..^4].ToLowerInvariant()
                        .Split('_', StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length < 3)
                        continue;
                    if (!champThemes.TryGetValue(champ, out HashSet<string> themes))
                        champThemes[champ] = themes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string token in tokens[1..^1])
                    {
                        if (token.Length < 3 || token.StartsWith("skin", StringComparison.OrdinalIgnoreCase) ||
                            token == "jade" || champ.Contains(token, StringComparison.OrdinalIgnoreCase))
                            continue;
                        themes.Add(token);
                    }
                }
            }

            foreach (HashSet<string> themes in champThemes.Values)
            {
                themes.RemoveWhere(token =>
                    BaseAnimationActions.Contains(token, StringComparer.OrdinalIgnoreCase) ||
                    (token.Length == 2 && token.All(char.IsDigit)));
            }

            var darkFolders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var orderedThemes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach ((string champ, HashSet<string> themes) in champThemes)
            {
                if (themes.Count == 0)
                    continue;
                orderedThemes[champ] = themes.OrderBy(t => t, StringComparer.Ordinal).Take(12).ToList();
            }

            foreach ((string folder, int count) in folderCounts)
            {
                if (count > 2 || !folderChamp.TryGetValue(folder, out string champ))
                    continue;
                if (!orderedThemes.ContainsKey(champ))
                    continue;
                if (!darkFolders.TryGetValue(champ, out List<string> folders))
                    darkFolders[champ] = folders = new List<string>();
                if (folders.Count < 12)
                    folders.Add(folder);
            }

            var result = new Dictionary<string, RecallContext>(StringComparer.OrdinalIgnoreCase);
            foreach (string champ in lines.Keys
                         .Concat(darkFolders.Keys)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                lines.TryGetValue(champ, out List<RecallTextureLine> champLines);
                darkFolders.TryGetValue(champ, out List<string> champFolders);
                orderedThemes.TryGetValue(champ, out List<string> themes);
                result[champ] = new RecallContext(
                    champLines ?? new List<RecallTextureLine>(),
                    champFolders ?? new List<string>(),
                    themes ?? new List<string>());
            }

            return result;
        }

        private static IEnumerable<string> RecallFileCandidates(string folder, string stem, int recallNumber)
        {
            string recall = recallNumber.ToString("D2", CultureInfo.InvariantCulture);
            foreach (string map in new[] { "cm", "gm", "rm" })
            foreach (string extension in new[] { "tex", "dds" })
            foreach (string hd in new[] { "", "2x_", "4x_" })
                yield return $"{folder}{hd}{stem}_rc{recall}_tx_{map}.{extension}";
        }

        private static IEnumerable<string> WalkRecallTextureLine(RecallTextureLine line)
        {
            foreach (int step in new[] { 1, 2, 3, -1, -2 })
            {
                int folderNum = line.SkinNumber + step;
                int recallNum = line.RecallNumber + step;
                if (folderNum < 0 || recallNum < 1 || recallNum > 30)
                    continue;
                string folderSkin = "skin" + folderNum.ToString().PadLeft(line.SkinWidth, '0');
                string folder = Regex.Replace(
                    line.Folder,
                    @"/skins/skin\d+/",
                    $"/skins/{folderSkin}/",
                    RegexOptions.IgnoreCase);
                foreach (string candidate in RecallFileCandidates(folder, line.Stem, recallNum))
                    yield return candidate;
            }
        }
    }
}
