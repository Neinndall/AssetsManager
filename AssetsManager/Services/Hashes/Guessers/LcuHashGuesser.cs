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
using AssetsManager.Utils;
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
        private static readonly Regex RegionLangRegex = new(@"^plugins/([^/]+)/[^/]+/[^/]+/", RegexOptions.Compiled);
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

        internal IReadOnlyList<string> BuildSwordlist() =>
            Corpus.GetOrCreate(
                "swordlist",
                paths => HashGuessEngine.BuildWordlist(
                    paths
                        .Where(path => path.Contains("-fe-lol-", StringComparison.Ordinal)
                            && path.Contains(".json", StringComparison.Ordinal))
                        .Select(Path.GetFileName)));

        internal IReadOnlyList<string> BuildSswordlist() =>
            Corpus.GetOrCreate(
                "sswordlist",
                paths => HashGuessEngine.BuildWordlist(
                    paths
                        .Where(IsRcpFeLolSvgPath)
                        .Select(Path.GetFileName)));

        internal IReadOnlyList<string> BuildPngJpgSwordlist() =>
            Corpus.GetOrCreate(
                "swordlist-png-jpg",
                paths => HashGuessEngine.BuildWordlist(
                    paths
                        .Where(IsRcpFeLolPngJpgPath)
                        .Select(Path.GetFileName)));

        private static bool IsRcpFeLolSvgPath(string path) =>
            path.Contains("-fe-lol-", StringComparison.Ordinal)
            && path.Contains(".svg", StringComparison.Ordinal);

        private static bool IsRcpFeLolPngJpgPath(string path) =>
            path.Contains("-fe-lol-", StringComparison.Ordinal)
            && (path.Contains(".png", StringComparison.Ordinal)
                || path.Contains(".jpg", StringComparison.Ordinal));

        protected override bool IncludeNumberPath(string path) => !NumberExcludedPathRegex.IsMatch(path);

        internal int SubstitutePlugin(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            int candidateBudget = int.MaxValue,
            Action<int> progress = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            if (candidateBudget < 0) throw new ArgumentOutOfRangeException(nameof(candidateBudget));
            if (candidateBudget == 0) return 0;

            IReadOnlyList<string> allPaths = KnownPaths
                .Where(path => path.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase))
                .ToList();
            IReadOnlyList<string> plugins = allPaths
                .Select(path => path.Split('/')[1])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(plugin => plugin, StringComparer.Ordinal)
                .ToList();
            IReadOnlyList<string> formats = allPaths
                .Select(path => Regex.Replace(path, @"^plugins/([^/]+)/", "plugins/{plugin}/", RegexOptions.IgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            const string source = "LCU plugin substitution";
            int checkedCount = 0;
            foreach (string format in ProgressIterator(formats, value => value, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int remaining = candidateBudget == int.MaxValue ? int.MaxValue : candidateBudget - checkedCount;
                if (remaining <= 0 || engine.RemainingUnknownCount == 0) break;

                IEnumerable<HashGuessCandidate> candidates = plugins.Select(plugin =>
                    new HashGuessCandidate(
                        format.Replace("{plugin}", plugin, StringComparison.Ordinal),
                        HashGuessStrategy.PluginVariant));
                if (remaining != int.MaxValue) candidates = candidates.Take(remaining);

                checkedCount += CheckIter(engine, candidates, source, cancellationToken);
                progress?.Invoke(checkedCount);
            }

            return checkedCount;
        }

        internal int GuessPatterns(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            int candidateBudget = int.MaxValue,
            Action<int> progress = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            if (candidateBudget < 0) throw new ArgumentOutOfRangeException(nameof(candidateBudget));
            if (candidateBudget == 0) return 0;

            const string source = "LCU patterns";
            int checkedCount = 0;

            void CheckPatternIter(IEnumerable<string> paths)
            {
                if (engine.RemainingUnknownCount == 0) return;
                int remaining = candidateBudget == int.MaxValue ? int.MaxValue : candidateBudget - checkedCount;
                if (remaining <= 0) return;

                IEnumerable<HashGuessCandidate> candidates = paths.Select(
                    path => new HashGuessCandidate(path, HashGuessStrategy.LcuPattern));
                if (remaining != int.MaxValue) candidates = candidates.Take(remaining);

                checkedCount += CheckIter(engine, candidates, source, cancellationToken);
                progress?.Invoke(checkedCount);
            }

            var perkPrimary = Enumerable.Range(80, 6).Select(value => value * 100).ToList();
            foreach (int primary in perkPrimary)
            {
                var perkSecondary = Enumerable.Range(primary, 100).ToList();
                CheckPatternIter(
                    perkPrimary.Prepend(0).SelectMany(style =>
                        perkSecondary.Prepend(0).Select(perk =>
                            $"plugins/rcp-fe-lol-perks/global/default/images/inventory-card/{primary}/p{primary}_s{style}_k{perk}.jpg")));

                CheckPatternIter(
                    new[] { "environment.jpg", "construct.png" }
                        .Concat(perkSecondary.Select(perk => $"keystones/{perk}.png"))
                        .Concat(perkPrimary.Select(style => $"second/{style}.png"))
                        .Select(path => $"plugins/rcp-fe-lol-perks/global/default/images/construct/{primary}/{path}"));

                if (engine.RemainingUnknownCount == 0 || checkedCount >= candidateBudget) return checkedCount;
            }

            IEnumerable<string> sanitizerPaths = Enumerable.Range(0, 5).SelectMany(index =>
                new[] { "filter", "unfilter", "whitelist" }.SelectMany(action =>
                    new[] { $"{index}.{action}.csv" }
                        .Concat(Locales.Select(locale =>
                        {
                            string[] parts = locale.Split('_');
                            return $"{index}.{action}.language.{parts[0]}.csv";
                        }))
                        .Concat(Locales.Select(locale =>
                        {
                            string[] parts = locale.Split('_');
                            return $"{index}.{action}.country.{parts[1]}.csv";
                        }))
                        .Concat(Regions
                            .Where(region => !region.Equals("global", StringComparison.OrdinalIgnoreCase))
                            .Select(region => $"{index}.{action}.region.{region}.csv"))
                        .Concat(Locales.Select(locale => $"{index}.{action}.locale.{locale}.csv"))));

            IEnumerable<string> sanitizerNames = new[]
            {
                "allowedchars", "breakingchars", "projectedchars", "projectedchars1337",
                "punctuationchars", "variantaliases"
            }.SelectMany(name =>
                Locales.Select(locale => $"{name}.locale.{locale}.txt")
                    .Concat(Locales.Select(locale => $"{name}.language.{locale.Split('_')[0]}.txt")));

            CheckPatternIter(
                sanitizerPaths
                    .Concat(sanitizerNames)
                    .Select(path => $"plugins/rcp-be-sanitizer/global/default/{path}"));

            if (engine.RemainingUnknownCount == 0 || checkedCount >= candidateBudget) return checkedCount;

            foreach (string path in KnownPaths.Where(path =>
                         path.StartsWith("plugins/rcp-fe-lol-loot/global/default/assets/loot_item_icons/", StringComparison.OrdinalIgnoreCase) &&
                         path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) break;
                Check(engine, path[..^4] + "_splash.png", HashGuessStrategy.LcuPattern, source);
                checkedCount++;
                progress?.Invoke(checkedCount);
            }

            return checkedCount;
        }

        internal int SubstituteRegionLang(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            int candidateBudget = int.MaxValue,
            Action<int> progress = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            if (candidateBudget < 0) throw new ArgumentOutOfRangeException(nameof(candidateBudget));
            if (candidateBudget == 0) return 0;

            IReadOnlyList<string> known = KnownPaths.ToList();
            IReadOnlyList<string> languages = Locales.Append("default").ToList();
            var regionLanguages = Regions
                .SelectMany(region => languages, (region, language) => (Region: region, Language: language))
                .ToList();

            const string source = "Generated region or locale variant";
            int checkedCount = 0;
            foreach (var regionLanguage in ProgressIterator(
                         regionLanguages,
                         value => $"{value.Region}/{value.Language}",
                         cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int remaining = candidateBudget == int.MaxValue ? int.MaxValue : candidateBudget - checkedCount;
                if (remaining <= 0 || engine.RemainingUnknownCount == 0) break;

                string replacement = $"plugins/$1/{regionLanguage.Region}/{regionLanguage.Language}/";
                IEnumerable<HashGuessCandidate> candidates = known.Select(path =>
                    new HashGuessCandidate(
                        RegionLangRegex.Replace(path, replacement),
                        HashGuessStrategy.LanguageVariant));
                if (remaining != int.MaxValue) candidates = candidates.Take(remaining);

                checkedCount += CheckIter(engine, candidates, source, cancellationToken);
                progress?.Invoke(checkedCount);
            }

            return checkedCount;
        }

        internal int SubstituteBasenameWords(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            string plugin = null,
            string fileExtension = null,
            IEnumerable<string> words = null,
            int oldWordCount = 1,
            int newWordCount = 1,
            int candidateBudget = int.MaxValue,
            Action<int> progress = null)
        {
            IEnumerable<string> paths = KnownPaths;
            if (!string.IsNullOrEmpty(plugin))
            {
                string pluginPrefix = plugin.EndsWith("*", StringComparison.Ordinal)
                    ? $"plugins/{plugin[..^1]}"
                    : $"plugins/{plugin}/";
                paths = paths.Where(path => path.StartsWith(pluginPrefix, StringComparison.Ordinal));
            }
            if (!string.IsNullOrEmpty(fileExtension))
                paths = paths.Where(path => path.EndsWith(fileExtension, StringComparison.Ordinal));
            return SubstituteBasenameWordsCore(
                engine, paths, words ?? BuildWordlist(), oldWordCount, newWordCount,
                cancellationToken, candidateBudget, "LCU basename word substitution", progress);
        }

        internal int RunCustomAttacks(
            HashGuessEngine engine,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(engine);

            IReadOnlyList<string> words = BuildSswordlist();
            int checkedCandidates = 0;
            var variants = new[]
            {
                (OldWordCount: 1, NewWordCount: 1),
                (OldWordCount: 1, NewWordCount: 2),
                (OldWordCount: 2, NewWordCount: 2)
            };

            int RunVariants(
                IReadOnlyList<string> variantWords,
                IEnumerable<string> extensions,
                string label)
            {
                int variantCheckedCandidates = 0;
                foreach (string extension in extensions)
                foreach ((int oldWordCount, int newWordCount) in variants)
                {
                    if (engine.RemainingUnknownCount == 0) return variantCheckedCandidates;

                    string stage = $"LCU Custom: rcp-fe-lol-* {label} basename {oldWordCount}->{newWordCount}";
                    progress?.Report(engine.CreateProgress(stage, checkedCandidates + variantCheckedCandidates));
                    int progressOffset = checkedCandidates + variantCheckedCandidates;
                    int count = SubstituteBasenameWords(
                        engine,
                        cancellationToken,
                        plugin: "rcp-fe-lol-*",
                        fileExtension: extension,
                        words: variantWords,
                        oldWordCount: oldWordCount,
                        newWordCount: newWordCount,
                        candidateBudget: 50_000_000,
                        progress: current => progress?.Report(
                            engine.CreateProgress(stage, progressOffset + current)));
                    variantCheckedCandidates += count;
                }

                return variantCheckedCandidates;
            }

            checkedCandidates += RunVariants(words, new[] { "svg" }, "SVG");
            if (engine.RemainingUnknownCount > 0)
                checkedCandidates += RunVariants(BuildPngJpgSwordlist(), new[] { "png", "jpg" }, "PNG/JPG");

            return checkedCandidates;
        }

        internal int AddBasenameWord(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            Action<int> progress = null) =>
            AddBasenameWordCore(
                engine,
                KnownPaths,
                BuildWordlist(),
                cancellationToken,
                candidateBudget: 50_000_000,
                source: "LCU basename word addition",
                progress: progress);

        internal int SubstituteNumbers(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            int maximum = 10_000,
            int? digits = null,
            Action<int> progress = null) =>
            base.SubstituteNumbersCore(
                engine,
                KnownPaths.Where(path => !NumberExcludedPathRegex.IsMatch(path)),
                maximum,
                digits,
                inferDigits: false,
                cancellationToken: cancellationToken,
                source: "Generated numeric variant",
                progress: progress);

        internal override void GrepWad(HashGuessEngine engine, ArraySegment<byte> data, string sourcePath, string sourceWadPath, ulong sourceChunkHash)
        {
            if (data.Count == 0 || !TryDecodeWadText(data, out string text)) return;
            void CheckLcuCandidates(IEnumerable<HashGuessCandidate> candidates) =>
                CheckIter(engine, candidates, sourceWadPath, CancellationToken.None, sourceChunkHash: sourceChunkHash);

            if (Path.GetExtension(sourcePath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                var structuredCandidates = new List<HashGuessCandidate>();
                bool stopAfterStructuredJson = ExtractStructuredJsonCandidates(data, sourcePath, structuredCandidates);
                CheckLcuCandidates(structuredCandidates);
                if (stopAfterStructuredJson) return;
            }

            CheckLcuCandidates(
                PluginPathRegex.Matches(text).Cast<Match>().Select(match =>
                    new HashGuessCandidate(NormalizePath(match.Value), HashGuessStrategy.LcuEmbeddedPath)));
            CheckLcuCandidates(
                FrontendPathRegex.Matches(text).Cast<Match>().Select(match =>
                    new HashGuessCandidate(
                        $"plugins/rcp-fe-{match.Groups[1].Value}/global/default/{match.Groups[2].Value}".ToLowerInvariant(),
                        HashGuessStrategy.LcuEmbeddedPath)));
            CheckLcuCandidates(
                DataPathRegex.Matches(text).Cast<Match>().Select(match =>
                    new HashGuessCandidate(
                        $"plugins/rcp-be-lol-game-data/global/default/data/{match.Groups[1].Value}".ToLowerInvariant(),
                        HashGuessStrategy.LcuEmbeddedPath)));
            CheckLcuCandidates(
                AssetPathRegex.Matches(text).Cast<Match>().Select(match =>
                    new HashGuessCandidate(
                        $"plugins/rcp-be-lol-game-data/global/default/{match.Groups[1].Value}".ToLowerInvariant(),
                        HashGuessStrategy.LcuEmbeddedPath)));

            foreach (Match match in CssUrlRegex.Matches(text))
            {
                string contextualPath = ResolveRelativePath(sourcePath, match.Groups[1].Value);
                if (contextualPath.Length > 0)
                    CheckLcuCandidates(new[] { new HashGuessCandidate(contextualPath, HashGuessStrategy.LcuEmbeddedPath) });
            }
            foreach (Match match in HtmlAssetRegex.Matches(text))
            {
                string contextualPath = ResolveRelativePath(sourcePath, match.Groups[1].Value);
                if (contextualPath.Length > 0)
                    CheckLcuCandidates(new[] { new HashGuessCandidate(contextualPath, HashGuessStrategy.LcuEmbeddedPath) });
            }

            var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in RelativePathRegex.Matches(text))
            {
                string relativePath = match.Groups[1].Value;
                relativePaths.Add(relativePath);
                string contextualPath = ResolveRelativePath(sourcePath, relativePath);
                if (contextualPath.Length > 0)
                    CheckLcuCandidates(new[] { new HashGuessCandidate(contextualPath, HashGuessStrategy.LcuEmbeddedPath) });
            }
            foreach (Match match in FileNameRegex.Matches(text)) relativePaths.Add(match.Groups[1].Value);
            foreach (Match match in TemplateRegex.Matches(text)) relativePaths.Add(match.Groups[1].Value + "/template.html");
            foreach (Match match in SourceMapRegex.Matches(text)) relativePaths.Add(match.Groups[1].Value);

            CheckLcuCandidates(
                relativePaths.Select(path => new HashGuessCandidate(NormalizePath(path), HashGuessStrategy.LcuRelativeBasename)));
        }

        internal int GuessFromGameHashes(
            HashGuessEngine engine,
            HashGuesser gameGuesser,
            CancellationToken cancellationToken,
            int candidateBudget = int.MaxValue,
            Action<int> progress = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(gameGuesser);
            if (gameGuesser.Domain != HashGuessDomain.Game)
                throw new ArgumentException("Cross-domain LCU guessing requires a GAME guesser.", nameof(gameGuesser));
            if (candidateBudget < 0) throw new ArgumentOutOfRangeException(nameof(candidateBudget));
            if (candidateBudget == 0) return 0;

            const string basePath = "plugins/rcp-be-lol-game-data/global/default";
            const string source = "LCU from GAME hashes";
            int checkedCount = 0;

            bool CheckGamePath(string path)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) return false;
                Check(engine, path, HashGuessStrategy.CrossDomainAsset, source);
                checkedCount++;
                progress?.Invoke(checkedCount);
                return true;
            }

            foreach (string path in gameGuesser.KnownPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) break;

                if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    string prefix = path[..^4];
                    if (!CheckGamePath($"{basePath}/{prefix}.png")) break;
                    if (!CheckGamePath($"{basePath}/{prefix}.jpg")) break;
                }
                else if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    if (!CheckGamePath($"{basePath}/{path}")) break;
                }
            }

            return checkedCount;
        }

        // Dedicated, opt-in coverage for the v1 check_iter patterns used by CDTB tooling.
        // It deliberately stays out of Basic and Extended because its wordlist cross-product is expensive.
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
                .Select(PathUtils.NormalizePath)
                .Where(word => word.Length > 0)
                .Distinct(StringComparer.Ordinal)
                // Short, composable terms produce useful v1 names early (for example augment + list),
                // while retaining the complete CDTB word corpus for exhaustive coverage.
                .OrderBy(word => word.Length)
                .ThenBy(word => word, StringComparer.Ordinal)
                .ToArray();
            // GREP already derives installed localized paths from their WAD contents.
            // V1 guessing therefore stays on the default path unless a caller explicitly opts into locales.
            string[] localeList = (locales ?? Array.Empty<string>())
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
                checkedCandidates += CheckIter(
                    engine,
                    new[] { new HashGuessCandidate(defaultPath, HashGuessStrategy.LcuPattern) },
                    source,
                    cancellationToken);
                Report(phase);
                if (engine.RemainingUnknownCount == 0) return false;

                // A localized path is only attempted after its default counterpart is known or resolved.
                // This preserves the useful locale expansion without multiplying every word pair by all locales.
                ulong defaultHash = XxHash64Ext.Hash(PathUtils.NormalizePath(defaultPath));
                bool hasDefaultEvidence = engine.Matches.ContainsKey(defaultHash) || knownHashes.ContainsKey(defaultHash);
                if (!hasDefaultEvidence) return true;

                checkedCandidates += CheckIter(
                    engine,
                    localeList.Select(locale => new HashGuessCandidate(
                        $"{v1Prefix}{locale}/v1/{fileName}",
                        HashGuessStrategy.LcuPattern)),
                    source,
                    cancellationToken);
                Report(phase);
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
