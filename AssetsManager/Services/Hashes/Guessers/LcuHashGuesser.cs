using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes.Guessers
{
    internal sealed class LcuHashGuesser : HashGuesser
    {
        private static readonly Regex PluginPathRegex = new(@"plugins/[0-9a-z_./@-]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FrontendPathRegex = new(@"\bfe/([^/]+)/([a-zA-Z0-9/_.@-]+)", RegexOptions.Compiled);
        private static readonly Regex DataPathRegex = new(@"/DATA/([a-zA-Z0-9/_.@-]+)", RegexOptions.Compiled);
        private static readonly Regex AssetPathRegex = new(@"\blol-game-data/assets/([a-zA-Z0-9/_.@-]+)", RegexOptions.Compiled);
        private static readonly Regex RelativePathRegex = new(@"[^a-zA-Z0-9/_.\\-]((?:\.|\.\.)/[a-zA-Z0-9/_.-]+)", RegexOptions.Compiled);
        private static readonly Regex FileNameRegex = new("[\\\"']([a-zA-Z0-9][a-zA-Z0-9/_.@-]*\\.(?:js|json|webm|html|[a-z]{3}))\\b", RegexOptions.Compiled);
        private static readonly Regex CssUrlRegex = new("url\\(\\s*[\\\"']?([^\\\"')?#]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HtmlAssetRegex = new("(?:src|href|poster|data-src)\\s*=\\s*[\\\"']([^\\\"'?#]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TemplateRegex = new("<template id=\\\"[^\\\"]*-template-([^\\\"]+)\\\"", RegexOptions.Compiled);
        private static readonly Regex SourceMapRegex = new("sourceMappingURL=(.*?\\.js)\\.map", RegexOptions.Compiled);
        private static readonly Regex SplashNameRegex = new(@"-splash-([^.]+)", RegexOptions.Compiled);
        private static readonly Regex NumberExcludedPathRegex = new(@"(?:^(?:plugins/rcp-be-lol-game-data/[^/]+/[^/]+/v1/champion-|plugins/rcp-be-lol-game-data/global/default/(?:data|assets)/characters/|plugins/rcp-be-lol-game-data/global/default/data/items/icons2d/\d+_|plugins/rcp-be-lol-game-data/[^/]+/[^/]+/v1/champions/-1\.json)|/[0-9a-f]{32}\.)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex WordlistExcludedPathRegex = new(@"(?:^plugins/rcp-be-lol-game-data/global/default/data/characters/|/[0-9a-f]{32}\.)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly string[] Locales = { "ar_ae", "ar_eg", "cs_cz", "de_de", "el_gr", "en_au", "en_gb", "en_ph", "en_pl", "en_sg", "en_us", "es_ar", "es_es", "es_mx", "fr_fr", "hu_hu", "id_id", "it_it", "ja_jp", "ko_kr", "ms_my", "pl_pl", "pt_br", "ro_ro", "ru_ru", "th_th", "tr_tr", "vi_vn", "vn_vn", "zh_cn", "zh_my", "zh_tw" };
        private static readonly string[] Regions = { "br", "cn", "eun", "eune", "euw", "garena2", "garena3", "id", "jp", "kr", "la", "la1", "la2", "lan", "las", "na", "oc", "oc1", "oce", "pbe", "ph", "ph2", "ru", "sg", "tencent", "th", "th2", "tr", "tw", "tw2", "vn", "vn2", "global" };

        private static readonly HashSet<string> SkippedExtensions = new(StringComparer.Ordinal)
        {
            "png", "jpg", "ttf", "webm", "ogg", "dds", "tga"
        };

        private readonly object _directorySync = new();
        private IReadOnlyList<string> _knownDirectories;
        private long _knownDirectoryRevision = -1;
        private readonly LogService _logService;

        internal LcuHashGuesser(HashFile hashFile, LogService logService)
            : base(hashFile, "*.wad")
        {
            if (hashFile.Domain != HashGuessDomain.Lcu) throw new ArgumentException("LCU guesser requires an LCU hash file.", nameof(hashFile));
            _logService = logService;
        }

        internal LcuHashGuesser(IEnumerable<string> knownPaths, LogService logService)
            : this(new HashFile(HashGuessDomain.Lcu, knownPaths), logService) { }

        internal override bool ShouldSkip(string extension) => SkippedExtensions.Contains(extension);
        internal IReadOnlyList<string> WordlistPaths => Corpus.GetOrCreate(
            "wordlist-paths",
            paths => paths.Where(path => !WordlistExcludedPathRegex.IsMatch(path)).ToList());
        internal override IReadOnlyList<string> BuildWordlist() =>
            Corpus.GetOrCreate("wordlist", _ => HashGuessEngine.BuildWordlist(WordlistPaths));

        internal override IEnumerable<HashGuessCandidate> GenerateCanonicalCandidates(HashGuesser otherDomain, int candidateBudget = int.MaxValue)
        {
            const string basePath = "plugins/rcp-be-lol-game-data/global/default/";
            int generated = 0;
            foreach (string path in otherDomain.KnownPaths)
            {
                if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    string prefix = path[..^4];
                    foreach (string extension in new[] { ".png", ".jpg" })
                    {
                        yield return new HashGuessCandidate(basePath + prefix + extension, HashGuessStrategy.CrossDomainAsset);
                        if (CountCandidate(ref generated, candidateBudget)) yield break;
                    }
                }
                else if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new HashGuessCandidate(basePath + path, HashGuessStrategy.CrossDomainAsset);
                    if (CountCandidate(ref generated, candidateBudget)) yield break;
                }
            }
        }

        internal override IEnumerable<HashGuessCandidate> GenerateLanguageCandidates(int candidateBudget = int.MaxValue)
        {
            var formats = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in KnownPaths)
            {
                Match match = Regex.Match(path, @"^plugins/([^/]+)/[^/]+/[^/]+/(.*)$", RegexOptions.IgnoreCase);
                if (match.Success) formats.Add($"plugins/{match.Groups[1].Value}/{{region}}/{{locale}}/{match.Groups[2].Value}");
            }
            int generated = 0;
            foreach (string format in formats.OrderBy(path => path, StringComparer.Ordinal))
            foreach (string region in Regions)
            foreach (string locale in Locales.Append("default"))
            {
                yield return new HashGuessCandidate(
                    format.Replace("{region}", region, StringComparison.Ordinal).Replace("{locale}", locale, StringComparison.Ordinal),
                    HashGuessStrategy.LanguageVariant);
                if (CountCandidate(ref generated, candidateBudget)) yield break;
            }
        }

        protected override bool IncludeNumberPath(string path) => !NumberExcludedPathRegex.IsMatch(path);

        internal IEnumerable<HashGuessCandidate> GeneratePluginCandidates(int candidateBudget = int.MaxValue)
        {
            var paths = KnownPaths.Where(path => path.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)).ToList();
            var plugins = paths.Select(path => path.Split('/')[1]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var formats = paths.Select(path => Regex.Replace(path, @"^plugins/[^/]+/", "plugins/{plugin}/", RegexOptions.IgnoreCase))
                .Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal);
            int generated = 0;
            foreach (string format in formats)
            foreach (string plugin in plugins)
            {
                yield return new HashGuessCandidate(format.Replace("{plugin}", plugin, StringComparison.Ordinal), HashGuessStrategy.PluginVariant);
                if (CountCandidate(ref generated, candidateBudget)) yield break;
            }
        }

        internal IEnumerable<HashGuessCandidate> GenerateLcuExtensionCandidates(int candidateBudget = int.MaxValue) =>
            GenerateExtensionCandidates(KnownPaths, candidateBudget);

        internal IEnumerable<HashGuessCandidate> GeneratePatternCandidates()
        {
            var paths = KnownPaths.Where(path => path.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)).ToList();
            var perkPrimary = Enumerable.Range(80, 6).Select(value => value * 100).ToList();
            foreach (int primary in perkPrimary)
            {
                var secondary = Enumerable.Range(primary, 100).ToList();
                foreach (int style in perkPrimary.Prepend(0))
                foreach (int perk in secondary.Prepend(0))
                    yield return new HashGuessCandidate($"plugins/rcp-fe-lol-perks/global/default/images/inventory-card/{primary}/p{primary}_s{style}_k{perk}.jpg", HashGuessStrategy.LcuPattern);
                yield return new HashGuessCandidate($"plugins/rcp-fe-lol-perks/global/default/images/construct/{primary}/environment.jpg", HashGuessStrategy.LcuPattern);
                yield return new HashGuessCandidate($"plugins/rcp-fe-lol-perks/global/default/images/construct/{primary}/construct.png", HashGuessStrategy.LcuPattern);
                foreach (int perk in secondary)
                    yield return new HashGuessCandidate($"plugins/rcp-fe-lol-perks/global/default/images/construct/{primary}/keystones/{perk}.png", HashGuessStrategy.LcuPattern);
                foreach (int style in perkPrimary)
                    yield return new HashGuessCandidate($"plugins/rcp-fe-lol-perks/global/default/images/construct/{primary}/second/{style}.png", HashGuessStrategy.LcuPattern);
            }

            foreach (string action in new[] { "filter", "unfilter", "whitelist" })
            for (int index = 0; index < 5; index++)
            {
                yield return new HashGuessCandidate($"plugins/rcp-be-sanitizer/global/default/{index}.{action}.csv", HashGuessStrategy.LcuPattern);
                foreach (string locale in Locales)
                {
                    string[] parts = locale.Split('_');
                    yield return new HashGuessCandidate($"plugins/rcp-be-sanitizer/global/default/{index}.{action}.language.{parts[0]}.csv", HashGuessStrategy.LcuPattern);
                    yield return new HashGuessCandidate($"plugins/rcp-be-sanitizer/global/default/{index}.{action}.country.{parts[1]}.csv", HashGuessStrategy.LcuPattern);
                    yield return new HashGuessCandidate($"plugins/rcp-be-sanitizer/global/default/{index}.{action}.locale.{locale}.csv", HashGuessStrategy.LcuPattern);
                }
                foreach (string region in Regions)
                    yield return new HashGuessCandidate($"plugins/rcp-be-sanitizer/global/default/{index}.{action}.region.{region}.csv", HashGuessStrategy.LcuPattern);
            }
            foreach (string name in new[] { "allowedchars", "breakingchars", "projectedchars", "projectedchars1337", "punctuationchars", "variantaliases" })
            foreach (string locale in Locales)
            {
                yield return new HashGuessCandidate($"plugins/rcp-be-sanitizer/global/default/{name}.locale.{locale}.txt", HashGuessStrategy.LcuPattern);
                yield return new HashGuessCandidate($"plugins/rcp-be-sanitizer/global/default/{name}.language.{locale.Split('_')[0]}.txt", HashGuessStrategy.LcuPattern);
            }
            foreach (string path in paths.Where(path => path.StartsWith("plugins/rcp-fe-lol-loot/global/default/assets/loot_item_icons/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
                yield return new HashGuessCandidate(path[..^4] + "_splash.png", HashGuessStrategy.LcuPattern);
        }

        internal IEnumerable<HashGuessCandidate> SubstituteRegionLang() => GenerateLanguageCandidates(int.MaxValue);

        internal int SubstituteBasenameWords(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            string plugin = null,
            string fileExtension = null,
            IEnumerable<string> words = null,
            int oldWordCount = 1,
            int newWordCount = 1,
            Action<int> progress = null)
        {
            IEnumerable<string> paths = KnownPaths;
            if (!string.IsNullOrWhiteSpace(plugin))
                paths = paths.Where(path => path.StartsWith($"plugins/{plugin}/", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(fileExtension))
                paths = paths.Where(path => path.EndsWith(fileExtension, StringComparison.OrdinalIgnoreCase));
            return RunBasenameWordSubstitution(
                engine, paths, words ?? BuildWordlist(), oldWordCount, newWordCount,
                cancellationToken, int.MaxValue, "LCU basename word substitution", progress);
        }

        internal int AddBasenameWord(HashGuessEngine engine, CancellationToken cancellationToken) =>
            RunWordAdditionAttack(engine, KnownPaths, BuildWordlist(), cancellationToken, int.MaxValue);

        internal IEnumerable<HashGuessCandidate> SubstituteNumbers(int maximum = 10_000, int? digits = null, bool inferDigits = false) =>
            GenerateNumberCandidates(maximum, int.MaxValue, digits, inferDigits, includeCommonPadding: false);

        internal IEnumerable<HashGuessCandidate> SubstitutePlugin() => GeneratePluginCandidates(int.MaxValue);

        internal override void GrepWad(HashGuessEngine engine, ArraySegment<byte> data, string sourcePath, string sourceWadPath, ulong sourceChunkHash) =>
            CheckChunk(engine, data, sourcePath, sourceWadPath, sourceChunkHash);

        internal IEnumerable<HashGuessCandidate> GuessFromGameHashes(HashGuesser gameGuesser) =>
            GenerateCanonicalCandidates(gameGuesser, int.MaxValue);

        internal IEnumerable<HashGuessCandidate> GuessPatterns() => GeneratePatternCandidates();

        internal int SubstitutePartiesBasenameWordPairs(HashGuessEngine engine, CancellationToken cancellationToken)
        {
            var partyImages = Corpus.GetOrCreate("party-images", paths => paths.Where(path =>
                    path.StartsWith("plugins/rcp-fe-lol-parties/", StringComparison.OrdinalIgnoreCase) &&
                    path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList());
            var imageWords = Corpus.GetOrCreate("splash-image-words", paths =>
                HashGuessEngine.BuildBasenameWordlist(paths.Where(path =>
                    path.Contains("-fe-lol-s", StringComparison.OrdinalIgnoreCase) &&
                    path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))));

            return RunBasenameWordSubstitution(
                engine, partyImages, imageWords, 1, 2, cancellationToken, int.MaxValue,
                "LCU parties PNG word-pair substitution");
        }

        // Dedicated, opt-in coverage for the v1 check_iter patterns used by CDTB tooling.
        // It deliberately stays out of Basic and Advanced because its wordlist cross-product is expensive.
        internal int RunV1PathPatterns(
            HashGuessEngine engine,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            IEnumerable<string> words = null,
            IEnumerable<string> locales = null)
        {
            ArgumentNullException.ThrowIfNull(engine);

            string[] wordList = (words ?? BuildWordlist())
                .Where(word => !string.IsNullOrWhiteSpace(word))
                .Select(HashGuessEngine.NormalizePath)
                .Where(word => word.Length > 0)
                .Distinct(StringComparer.Ordinal)
                // Short, composable terms produce useful v1 names early (for example augment + list),
                // while retaining the complete CDTB word corpus for exhaustive coverage.
                .OrderBy(word => word.Length)
                .ThenBy(word => word, StringComparer.Ordinal)
                .ToArray();
            string[] localeList = (locales ?? Locales)
                .Where(locale => !string.IsNullOrWhiteSpace(locale))
                .Where(locale => !locale.Equals("default", StringComparison.OrdinalIgnoreCase))
                .Select(locale => locale.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            IReadOnlyDictionary<ulong, string> knownHashes = HashFile.Load();

            const string v1Prefix = "plugins/rcp-be-lol-game-data/global/";
            const string source = "LCU v1 path patterns";
            int checkedCandidates = 0;
            int lastReported = -1;
            var progressClock = Stopwatch.StartNew();

            void Report(string phase, bool force = false)
            {
                if (!force && ((checkedCandidates & 0x3fff) != 0 || progressClock.ElapsedMilliseconds < 100)) return;
                if (lastReported == checkedCandidates) return;
                progress?.Report(engine.CreateProgress($"LCU V1 Paths: {phase}", checkedCandidates));
                lastReported = checkedCandidates;
                progressClock.Restart();
            }

            bool CheckDefaultThenLocales(string fileName, string phase)
            {
                string defaultPath = $"{v1Prefix}default/v1/{fileName}";
                cancellationToken.ThrowIfCancellationRequested();
                bool resolvedDefault = Check(engine, defaultPath, HashGuessStrategy.LcuPattern, source);
                bool hasDefaultEvidence = resolvedDefault || knownHashes.ContainsKey(XxHash64Ext.Hash(defaultPath));
                if (checkedCandidates != int.MaxValue) checkedCandidates++;
                Report(phase);
                if (engine.RemainingUnknownCount == 0) return false;

                // A localized path is only attempted after its default counterpart is known or resolved.
                // This preserves the useful locale expansion without multiplying every word pair by all locales.
                if (!hasDefaultEvidence) return true;
                foreach (string locale in localeList)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Check(engine, $"{v1Prefix}{locale}/v1/{fileName}", HashGuessStrategy.LcuPattern, source);
                    if (checkedCandidates != int.MaxValue) checkedCandidates++;
                    Report(phase);
                    if (engine.RemainingUnknownCount == 0) return false;
                }
                return true;
            }

            Report("preparing", force: true);
            foreach (string a in wordList)
            {
                // The non-TFT paths are first because they are both broadly useful and cheap to resolve early.
                if (!CheckDefaultThenLocales($"{a}.json", "single names") ||
                    !CheckDefaultThenLocales($"tft{a}.json", "TFT single names") ||
                    !CheckDefaultThenLocales($"tft{a}s.json", "TFT plural names"))
                {
                    Report("completed", force: true);
                    return checkedCandidates;
                }

                foreach (string b in wordList)
                {
                    // These are the unique candidate sets from the 24 CDTB check_iter expressions.
                    // Because both words iterate over the full list, swapping a/b would only repeat work.
                    if (!CheckDefaultThenLocales($"{a}{b}s.json", "word pairs") ||
                        !CheckDefaultThenLocales($"{a}{b}n.json", "word pairs") ||
                        !CheckDefaultThenLocales($"{a}-{b}s.json", "word pairs") ||
                        !CheckDefaultThenLocales($"{a}-{b}.json", "word pairs") ||
                        !CheckDefaultThenLocales($"{a}{b}.json", "word pairs") ||
                        !CheckDefaultThenLocales($"{a}{b}", "word pairs") ||
                        !CheckDefaultThenLocales($"tft{a}{b}s.json", "TFT pairs") ||
                        !CheckDefaultThenLocales($"tft{a}-{b}s.json", "TFT pairs") ||
                        !CheckDefaultThenLocales($"tft{a}{b}.json", "TFT pairs") ||
                        !CheckDefaultThenLocales($"tft{a}-{b}.json", "TFT pairs") ||
                        !CheckDefaultThenLocales($"tft-{a}{b}.json", "TFT pairs"))
                    {
                        Report("completed", force: true);
                        return checkedCandidates;
                    }
                }
            }

            Report("completed", force: true);
            return checkedCandidates;
        }

        private static bool IsFrontendJsonPath(string path) =>
            path.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("-fe-lol-", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        private static IReadOnlyList<string> BuildFrontendJsonWordlist(IEnumerable<string> paths) =>
            HashGuessEngine.BuildBasenameWordlist(paths.Where(IsFrontendJsonPath)).Take(2_000).ToList();

        internal int RunAdvancedAttacks(HashGuessEngine engine, IProgress<HashGuessProgress> progress, CancellationToken cancellationToken)
        {
            var paths = KnownPaths;
            int checkedCandidates = 0;

            if (engine.RemainingUnknownCount > 0)
            {
                var frontendJsonPaths = Corpus.GetOrCreate("frontend-json-paths", values => values.Where(IsFrontendJsonPath).ToList());
                var frontendJsonWords = Corpus.GetOrCreate("frontend-json-words", _ => BuildFrontendJsonWordlist(frontendJsonPaths));
                if (frontendJsonPaths.Count > 0 && frontendJsonWords.Count > 0)
                {
                    progress?.Report(engine.CreateProgress("LCU Advanced: frontend JSON", checkedCandidates));
                    checkedCandidates += RunFocusedWordlistSubstitution(engine, frontendJsonPaths, frontendJsonWords, cancellationToken);
                    if (engine.RemainingUnknownCount > 0)
                        checkedCandidates += RunWordAdditionAttack(engine, frontendJsonPaths, frontendJsonWords, cancellationToken);
                }
            }
            if (engine.RemainingUnknownCount > 0)
            {
                checkedCandidates += AddBasenameWord(engine, cancellationToken);
            }
            if (engine.RemainingUnknownCount > 0)
            {
                progress?.Report(engine.CreateProgress("LCU Advanced: parties PNG word pairs", checkedCandidates));
                checkedCandidates += SubstitutePartiesBasenameWordPairs(engine, cancellationToken);
            }
            if (engine.RemainingUnknownCount == 0) return checkedCandidates;

            progress?.Report(engine.CreateProgress("Focused Attack: LCU static-assets", checkedCandidates));
            var staticAssets = Corpus.GetOrCreate("static-svg-paths", values => values.Where(path => path.StartsWith("plugins/rcp-fe-lol-static-assets/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)).ToList());
            var staticWords = Corpus.GetOrCreate("static-svg-words", _ => HashGuessEngine.BuildBasenameWordlist(staticAssets).Take(5000).ToList());
            checkedCandidates += RunFocusedWordlistSubstitution(engine, staticAssets, staticWords, cancellationToken);
            if (engine.RemainingUnknownCount > 0)
                checkedCandidates += RunFocusedWordlistDoubleSubstitution(engine, staticAssets, staticWords, cancellationToken);

            if (engine.RemainingUnknownCount > 0)
            {
                progress?.Report(engine.CreateProgress("Focused Attack: LCU navigation", checkedCandidates));
                var navigation = Corpus.GetOrCreate("navigation-svg-paths", values => values.Where(path => path.StartsWith("plugins/rcp-fe-lol-navigation/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)).ToList());
                checkedCandidates += RunFocusedWordlistSubstitution(engine, navigation, staticWords, cancellationToken);
                if (engine.RemainingUnknownCount > 0)
                    checkedCandidates += RunFocusedWordlistDoubleSubstitution(engine, navigation, staticWords, cancellationToken);
            }
            if (engine.RemainingUnknownCount > 0)
            {
                progress?.Report(engine.CreateProgress("Focused Attack: LCU parties", checkedCandidates));
                var parties = Corpus.GetOrCreate("party-images", values => values.Where(path => path.StartsWith("plugins/rcp-fe-lol-parties/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList());
                var partyWords = Corpus.GetOrCreate("party-image-words", _ => HashGuessEngine.BuildBasenameWordlist(parties).Take(5000).ToList());
                checkedCandidates += RunFocusedWordlistSubstitution(engine, parties, partyWords, cancellationToken);
            }
            return checkedCandidates;
        }

        protected override void CheckCandidate(
            HashGuessEngine engine,
            HashGuessCandidate candidate,
            string sourceWadPath,
            ulong sourceChunkHash)
        {
            if (candidate.Strategy != HashGuessStrategy.LcuRelativeBasename)
            {
                base.CheckCandidate(engine, candidate, sourceWadPath, sourceChunkHash);
                return;
            }

            foreach (string directory in GetKnownDirectories())
                engine.CheckCombined(
                    directory,
                    candidate.Path,
                    candidate.Strategy,
                    sourceWadPath,
                    sourceChunkHash);
        }

        private IReadOnlyList<string> GetKnownDirectories()
        {
            IReadOnlyList<string> paths = KnownPaths;
            long revision = HashFile.Revision;
            lock (_directorySync)
            {
                if (_knownDirectories == null || _knownDirectoryRevision != revision)
                {
                    _knownDirectories = HashGuessEngine.BuildDirectoryList(paths);
                    _knownDirectoryRevision = revision;
                }
                return _knownDirectories;
            }
        }

        protected override IEnumerable<HashGuessCandidate> ExtractCandidates(ArraySegment<byte> data, string sourcePath)
        {
            if (data.Count == 0) yield break;

            if (!TryDecodeWadText(data, out string text))
                yield break;
            if (Path.GetExtension(sourcePath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                var structuredCandidates = new List<HashGuessCandidate>();
                bool stopAfterStructuredJson = ExtractStructuredJsonCandidates(data, sourcePath, structuredCandidates);
                foreach (HashGuessCandidate candidate in structuredCandidates)
                    yield return candidate;
                if (stopAfterStructuredJson) yield break;
            }

            foreach (Match match in PluginPathRegex.Matches(text))
                yield return new HashGuessCandidate(NormalizePath(match.Value), HashGuessStrategy.LcuEmbeddedPath);
            foreach (Match match in FrontendPathRegex.Matches(text))
                yield return new HashGuessCandidate(
                    $"plugins/rcp-fe-{match.Groups[1].Value}/global/default/{match.Groups[2].Value}".ToLowerInvariant(),
                    HashGuessStrategy.LcuEmbeddedPath);
            foreach (Match match in DataPathRegex.Matches(text))
                yield return new HashGuessCandidate(
                    $"plugins/rcp-be-lol-game-data/global/default/data/{match.Groups[1].Value}".ToLowerInvariant(),
                    HashGuessStrategy.LcuEmbeddedPath);
            foreach (Match match in AssetPathRegex.Matches(text))
                yield return new HashGuessCandidate(
                    $"plugins/rcp-be-lol-game-data/global/default/{match.Groups[1].Value}".ToLowerInvariant(),
                    HashGuessStrategy.LcuEmbeddedPath);

            foreach (Match match in CssUrlRegex.Matches(text))
            {
                string contextualPath = ResolveRelativePath(sourcePath, match.Groups[1].Value);
                if (contextualPath.Length > 0)
                    yield return new HashGuessCandidate(contextualPath, HashGuessStrategy.LcuEmbeddedPath);
            }
            foreach (Match match in HtmlAssetRegex.Matches(text))
            {
                string contextualPath = ResolveRelativePath(sourcePath, match.Groups[1].Value);
                if (contextualPath.Length > 0)
                    yield return new HashGuessCandidate(contextualPath, HashGuessStrategy.LcuEmbeddedPath);
            }

            var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in RelativePathRegex.Matches(text))
            {
                string relativePath = match.Groups[1].Value;
                relativePaths.Add(relativePath);
                string contextualPath = ResolveRelativePath(sourcePath, relativePath);
                if (contextualPath.Length > 0)
                    yield return new HashGuessCandidate(contextualPath, HashGuessStrategy.LcuEmbeddedPath);
            }
            foreach (Match match in FileNameRegex.Matches(text)) relativePaths.Add(match.Groups[1].Value);
            foreach (Match match in TemplateRegex.Matches(text)) relativePaths.Add(match.Groups[1].Value + "/template.html");
            foreach (Match match in SourceMapRegex.Matches(text)) relativePaths.Add(match.Groups[1].Value);

            foreach (string relativePath in relativePaths)
                yield return new HashGuessCandidate(NormalizePath(relativePath), HashGuessStrategy.LcuRelativeBasename);
        }

        private bool ExtractStructuredJsonCandidates(
            ArraySegment<byte> data,
            string sourcePath,
            ICollection<HashGuessCandidate> candidates)
        {
            try
            {
                using var document = JsonDocument.Parse(data.AsMemory());
                JsonElement root = document.RootElement;
                if (sourcePath.Equals("plugins/rcp-fe-lol-loot/global/default/trans.json", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (JsonProperty property in root.EnumerateObject())
                        candidates.Add(new HashGuessCandidate(
                            $"plugins/rcp-be-lol-game-data/global/default/v1/hextech-images/{property.Name}.png",
                            HashGuessStrategy.LcuPattern));
                    return true;
                }
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("pluginDependencies", out _) && root.TryGetProperty("name", out _))
                {
                    AddPluginDescriptionCandidates(root, candidates);
                }
                else if (root.ValueKind == JsonValueKind.Object &&
                         root.TryGetProperty("musicVolume", out _) && root.TryGetProperty("files", out _))
                {
                    AddSplashCandidates(root, candidates);
                    return true;
                }
                else if (sourcePath.Equals("plugins/rcp-be-lol-game-data/global/default/v1/champion-summary.json", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (JsonElement element in root.EnumerateArray())
                    {
                        if (!element.TryGetProperty("id", out JsonElement idProperty)) continue;
                        string championId = idProperty.ToString();
                        candidates.Add(new HashGuessCandidate(
                            $"plugins/rcp-be-lol-game-data/global/default/v1/champions/{championId}.json",
                            HashGuessStrategy.LcuPattern));
                        candidates.Add(new HashGuessCandidate(
                            $"plugins/rcp-be-lol-game-data/global/default/v1/champion-splashes/{championId}/metadata.json",
                            HashGuessStrategy.LcuPattern));
                    }
                }
                else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("recommendedItemDefaults", out _))
                {
                    AddRecommendedItemCandidates(root, candidates);
                }
                return false;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogInvalidJson("LCU", sourcePath, exception);
                return false;
            }
        }

        private static void AddPluginDescriptionCandidates(JsonElement root, ICollection<HashGuessCandidate> candidates)
        {
            if (!root.TryGetProperty("name", out JsonElement nameProperty)) return;
            string name = nameProperty.GetString()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(name)) return;
            foreach (string subpath in new[] { "index.html", "init.js", "init.js.map", "bundle.js", "trans.json", "css/main.css", "license.json" })
                candidates.Add(new HashGuessCandidate($"plugins/{name}/global/default/{subpath}", HashGuessStrategy.LcuPattern));
        }

        private static void AddSplashCandidates(JsonElement root, ICollection<HashGuessCandidate> candidates)
        {
            if (!root.TryGetProperty("files", out JsonElement filesProperty) || filesProperty.ValueKind != JsonValueKind.Object)
                return;

            var filePaths = filesProperty.EnumerateObject()
                .Select(property => property.Value.GetString()?.ToLowerInvariant() ?? string.Empty)
                .ToList();
            var splashNames = filePaths
                .SelectMany(path => SplashNameRegex.Matches(path).Select(match => match.Groups[1].Value.ToLowerInvariant()))
                .ToHashSet(StringComparer.Ordinal);

            foreach (string splashName in splashNames)
            {
                candidates.Add(new HashGuessCandidate(
                    $"plugins/rcp-fe-lol-splash/global/default/splash-assets/{splashName}/config.json",
                    HashGuessStrategy.LcuPattern));
                foreach (string filePath in filePaths)
                {
                    candidates.Add(new HashGuessCandidate(
                        $"plugins/rcp-fe-lol-splash/global/default/splash-assets/{splashName}/{filePath}",
                        HashGuessStrategy.LcuPattern));
                }
            }
        }

        private static void AddRecommendedItemCandidates(JsonElement root, ICollection<HashGuessCandidate> candidates)
        {
            if (!root.TryGetProperty("recommendedItemDefaults", out JsonElement property) || property.ValueKind != JsonValueKind.Array)
                return;
            foreach (JsonElement value in property.EnumerateArray())
            {
                string path = value.GetString()?.ToLowerInvariant();
                if (!string.IsNullOrEmpty(path))
                    candidates.Add(new HashGuessCandidate($"plugins/rcp-be-lol-game-data/global/default{path}", HashGuessStrategy.LcuPattern));
            }
        }

        private static string ResolveRelativePath(string sourcePath, string relativePath)
        {
            if (!sourcePath.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            relativePath = relativePath.Trim();
            if (relativePath.Length == 0 || relativePath.StartsWith('/') || relativePath.StartsWith('#') ||
                relativePath.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return string.Empty;

            int separator = sourcePath.LastIndexOf('/');
            if (separator < 0) return string.Empty;
            var segments = new List<string>(sourcePath[..separator].Split('/', StringSplitOptions.RemoveEmptyEntries));
            foreach (string segment in relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".") continue;
                if (segment == "..")
                {
                    if (segments.Count <= 1) return string.Empty;
                    segments.RemoveAt(segments.Count - 1);
                }
                else
                {
                    segments.Add(segment);
                }
            }
            return NormalizePath(string.Join('/', segments));
        }

        private void LogInvalidJson(string kind, string sourcePath, Exception exception)
        {
            _logService?.LogDebug($"Hash Lab skipped invalid {kind} JSON '{sourcePath}': {exception.Message}");
        }
    }
}
