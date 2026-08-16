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
using LeagueToolkit.Core.Wad;
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

        internal IReadOnlyList<string> BuildMediaSwordlist() =>
            Corpus.GetOrCreate(
                "swordlist-media",
                paths => HashGuessEngine.BuildWordlist(
                    paths
                        .Where(IsRcpFeLolMediaPath)
                        .Select(Path.GetFileName)));

        private static bool IsRcpFeLolSvgPath(string path) =>
            path.Contains("-fe-lol-", StringComparison.Ordinal)
            && path.Contains(".svg", StringComparison.Ordinal);

        private static bool IsRcpFeLolPngJpgPath(string path) =>
            path.Contains("-fe-lol-", StringComparison.Ordinal)
            && (path.Contains(".png", StringComparison.Ordinal)
                || path.Contains(".jpg", StringComparison.Ordinal));

        private static bool IsRcpFeLolMediaPath(string path) =>
            path.Contains("-fe-lol-", StringComparison.Ordinal)
            && (path.Contains(".webm", StringComparison.Ordinal)
                || path.Contains(".ogg", StringComparison.Ordinal));

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

            // Sanctum and Gacha card frame variants
            string[] sanctumFolders = { "sanctum", "images/sanctum", "exalted", "images/exalted", "transcendent", "images/transcendent" };
            string[] sanctumTiers = { "tier1", "tier2", "tier3", "tierone", "tiertwo", "tierthree" };
            string[] sanctumSides = { "", "-back", "-front" };
            foreach (string folder in sanctumFolders)
            foreach (string tier in sanctumTiers)
            foreach (string side in sanctumSides)
            foreach (string ext in new[] { "svg", "png" })
            {
                CheckPatternIter(new[] { $"plugins/rcp-fe-lol-static-assets/global/default/{folder}/card-frame-{tier}{side}.{ext}" });
            }

            // ARAM Wardrobe and Kiwi Hub
            string[] aramFiles = {
                "celebration-icon.png", "celebration-bg.png", "open-lock.png", "skin-border.png",
                "icon-small-circle.png", "paw-expiration-rect.png", "icon-small.png", "icon-large.png"
            };
            foreach (string file in aramFiles)
            {
                CheckPatternIter(new[] {
                    $"plugins/rcp-fe-lol-static-assets/global/default/aram-wardrobe/{file}",
                    $"plugins/rcp-fe-lol-static-assets/global/default/images/aram-wardrobe/{file}"
                });
            }
            CheckPatternIter(new[] {
                "plugins/rcp-fe-lol-static-assets/global/default/kiwi-hub/kiwi-hub.svg",
                "plugins/rcp-fe-lol-static-assets/global/default/images/kiwi-hub/kiwi-hub.svg"
            });

            // Reward and Milestone Tracker states
            string[] trackerFolders = { "reward-tracker", "images/reward-tracker", "milestone-tracker", "images/milestone-tracker" };
            string[] trackerStates = { "future", "completed", "current", "locked", "claimed", "unlocked", "active" };
            string[] trackerPositions = { "left", "right", "center", "middle" };
            foreach (string folder in trackerFolders)
            foreach (string state in trackerStates)
            foreach (string pos in trackerPositions)
            foreach (string ext in new[] { "svg", "png" })
            {
                CheckPatternIter(new[] { $"plugins/rcp-fe-lol-static-assets/global/default/{folder}/{state}-{pos}.{ext}" });
            }

            // Frontend developer README files across active plugin directories
            var knownPluginDirs = KnownPaths
                .Where(p => p.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase))
                .Select(p => Path.GetDirectoryName(p)?.Replace('\\', '/'))
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in knownPluginDirs)
            {
                CheckPatternIter(new[] { $"{dir}/README.md", $"{dir}/readme.md" });
            }

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

        internal int MirrorDirectories(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            int candidateBudget = int.MaxValue,
            Action<int> progress = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            if (candidateBudget <= 0) return 0;

            const string source = "LCU directory mirroring";
            int checkedCount = 0;

            foreach (string path in KnownPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (checkedCount >= candidateBudget || engine.RemainingUnknownCount == 0) break;

                if (path.Contains("/global/default/images/"))
                {
                    Check(engine, path.Replace("/global/default/images/", "/global/default/"), HashGuessStrategy.LcuPattern, source);
                    Check(engine, path.Replace("/global/default/images/", "/global/default/assets/"), HashGuessStrategy.LcuPattern, source);
                    checkedCount += 2;
                }
                else if (path.Contains("/global/default/assets/"))
                {
                    Check(engine, path.Replace("/global/default/assets/", "/global/default/"), HashGuessStrategy.LcuPattern, source);
                    Check(engine, path.Replace("/global/default/assets/", "/global/default/images/"), HashGuessStrategy.LcuPattern, source);
                    checkedCount += 2;
                }
                else if (path.Contains("/global/default/"))
                {
                    Check(engine, path.Replace("/global/default/", "/global/default/images/"), HashGuessStrategy.LcuPattern, source);
                    Check(engine, path.Replace("/global/default/", "/global/default/assets/"), HashGuessStrategy.LcuPattern, source);
                    checkedCount += 2;
                }

                if ((checkedCount & 0x1fff) == 0)
                {
                    progress?.Invoke(checkedCount);
                }
            }

            progress?.Invoke(checkedCount);
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

        internal int UniversalPluginModifierAttack(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            string pluginPattern = null,
            IEnumerable<string> extensions = null,
            Action<int> progress = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            if (engine.RemainingUnknownCount == 0) return 0;

            var extSet = extensions != null
                ? new HashSet<string>(extensions.Select(e => e.TrimStart('.').ToLowerInvariant()), StringComparer.OrdinalIgnoreCase)
                : null;

            IEnumerable<string> paths = KnownPaths.Where(p => p.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(pluginPattern))
            {
                string prefix = pluginPattern.EndsWith("*", StringComparison.Ordinal)
                    ? $"plugins/{pluginPattern[..^1]}"
                    : $"plugins/{pluginPattern}/";
                paths = paths.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            }

            if (extSet != null)
            {
                paths = paths.Where(p => extSet.Contains(Path.GetExtension(p).TrimStart('.').ToLowerInvariant()));
            }

            var pathList = paths.ToList();
            if (pathList.Count == 0) return 0;

            var dynamicTokens = ExtractDynamicAffixes(pathList, limit: 150);

            string[] baseModifiers =
            {
                "hover", "active", "selected", "disabled", "pressed", "clicked", "focused", "default", "normal",
                "locked", "unlocked", "claimed", "completed", "current", "future", "idle",
                "small", "large", "mini", "medium", "sm", "md", "lg", "xl",
                "bg", "background", "icon", "border", "glow", "frame", "badge", "crest", "emblem",
                "tier1", "tier2", "tier3", "tier4", "tier5", "tier6", "tierone", "tiertwo", "tierthree",
                "back", "front", "left", "right", "center", "top", "bottom",
                "v2", "v3", "intro", "outro", "loop", "in", "out",
                "18x18", "12x24", "10x10", "13x13", "20x20", "24x24", "32x32", "64x64", "92x92", "112x112", "128x128", "256x256"
            };

            var modifiers = baseModifiers.Concat(dynamicTokens).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            char[] delimiters = { '-', '_' };

            int checkedCount = 0;
            const string source = "LCU universal plugin modifier attack";

            var dirGroups = pathList.GroupBy(p => Path.GetDirectoryName(p)?.Replace('\\', '/'))
                .Where(g => !string.IsNullOrEmpty(g.Key));

            foreach (var group in dirGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (engine.RemainingUnknownCount == 0) break;

                string dir = group.Key;
                var basenamesWithExt = group.Select(p => (
                    BaseName: Path.GetFileNameWithoutExtension(p),
                    Ext: Path.GetExtension(p)
                )).Distinct().ToList();

                foreach (var item in basenamesWithExt)
                {
                    string baseName = item.BaseName;
                    string ext = item.Ext;

                    foreach (string mod in modifiers)
                    {
                        foreach (char d in delimiters)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            engine.Check($"{dir}/{baseName}{d}{mod}{ext}", HashGuessStrategy.WordlistVariant, source);
                            engine.Check($"{dir}/{mod}{d}{baseName}{ext}", HashGuessStrategy.WordlistVariant, source);
                            checkedCount += 2;
                            if ((checkedCount & 0x1fff) == 0)
                            {
                                progress?.Invoke(checkedCount);
                            }
                            if (engine.RemainingUnknownCount == 0) return checkedCount;
                        }
                    }
                }
            }

            progress?.Invoke(checkedCount);
            return checkedCount;
        }

        private static string ExtractPluginName(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            int nextSlash = path.IndexOf('/', 8);
            return nextSlash > 8 ? path[8..nextSlash] : path[8..];
        }

        private static IReadOnlyList<string> ExtractDynamicAffixes(IEnumerable<string> paths, int limit = 40)
        {
            var tokenFrequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                string baseName = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(baseName)) continue;

                string[] tokens = baseName.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string token in tokens)
                {
                    if (token.Length >= 2 && token.Length <= 24 && !int.TryParse(token, out _))
                    {
                        tokenFrequency.TryGetValue(token, out int count);
                        tokenFrequency[token] = count + 1;
                    }
                }
            }

            return tokenFrequency
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key)
                .Take(limit)
                .ToList();
        }

        internal int RunScopedPluginAttacks(
            HashGuessEngine engine,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(engine);
            if (engine.RemainingUnknownCount == 0) return 0;

            int checkedCandidates = 0;

            var pluginGroups = KnownPaths
                .Where(p => p.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase))
                .GroupBy(ExtractPluginName)
                .Where(g => !string.IsNullOrEmpty(g.Key))
                .ToList();

            char[] delimiters = { '-', '_' };

            var progressClock = Stopwatch.StartNew();
            void ReportThrottled(string stageName, int currentTotal, bool force = false)
            {
                if (force || progressClock.ElapsedMilliseconds >= 80)
                {
                    progress?.Report(engine.CreateProgress(stageName, currentTotal));
                    progressClock.Restart();
                }
            }

            // Extract global dynamic affixes from known LCU assets corpus (zero hardcoded words)
            var globalDynamicAffixes = ExtractDynamicAffixes(KnownPaths, limit: 500);

            foreach (var group in pluginGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (engine.RemainingUnknownCount == 0) break;

                string plugin = group.Key;
                var pluginPaths = group.ToList();
                if (pluginPaths.Count == 0) continue;

                string stage = $"LCU Custom: dynamic scoped plugin {plugin}";
                ReportThrottled(stage, checkedCandidates, force: true);

                string source = $"Scoped plugin {plugin}";

                var dirGroups = pluginPaths
                    .GroupBy(p => Path.GetDirectoryName(p)?.Replace('\\', '/'))
                    .Where(g => !string.IsNullOrEmpty(g.Key))
                    .ToList();

                // Dynamically expand and mirror standard LCU directory hierarchies (/images/, /assets/, etc.)
                var expandedDirs = new HashSet<string>(dirGroups.Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
                foreach (var d in dirGroups.Select(g => g.Key))
                {
                    if (d.Contains("/global/default/images/"))
                        expandedDirs.Add(d.Replace("/global/default/images/", "/global/default/"));
                    else if (d.Contains("/global/default/assets/"))
                        expandedDirs.Add(d.Replace("/global/default/assets/", "/global/default/"));
                    else if (d.Contains("/global/default/"))
                    {
                        expandedDirs.Add(d.Replace("/global/default/", "/global/default/images/"));
                        expandedDirs.Add(d.Replace("/global/default/", "/global/default/assets/"));
                    }
                }

                var dirs = expandedDirs.ToList();
                var basenamesWithExt = pluginPaths
                    .Select(p => (BaseName: Path.GetFileNameWithoutExtension(p), Ext: Path.GetExtension(p)))
                    .Distinct()
                    .ToList();

                // 1. Dynamic Intra-Plugin Directory Cross-Product (streaming & bounded)
                int crossBudget = 100_000;
                int crossCount = 0;
                foreach (var dir in dirs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (engine.RemainingUnknownCount == 0 || crossCount >= crossBudget) break;
                    foreach (var item in basenamesWithExt)
                    {
                        engine.Check($"{dir}/{item.BaseName}{item.Ext}", HashGuessStrategy.WordlistVariant, source);
                        checkedCandidates++;
                        crossCount++;
                        if ((checkedCandidates & 0x1fff) == 0)
                            ReportThrottled(stage, checkedCandidates);
                        if (engine.RemainingUnknownCount == 0 || crossCount >= crossBudget) break;
                    }
                }

                // 2. Dynamic Affix Permutation (using dynamically harvested tokens from plugin & corpus)
                var pluginAffixes = ExtractDynamicAffixes(pluginPaths, limit: 500)
                    .Concat(globalDynamicAffixes.Take(500))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                int affixBudget = 100_000;
                int affixCount = 0;
                foreach (var dir in dirs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (engine.RemainingUnknownCount == 0 || affixCount >= affixBudget) break;

                    var localItems = dirGroups.FirstOrDefault(g => g.Key == dir)?.Select(p => (BaseName: Path.GetFileNameWithoutExtension(p), Ext: Path.GetExtension(p))).Distinct().ToList()
                        ?? basenamesWithExt.Take(100).ToList();

                    foreach (var item in localItems)
                    {
                        string baseName = item.BaseName;
                        string ext = item.Ext;

                        foreach (string aff in pluginAffixes)
                        {
                            foreach (char d in delimiters)
                            {
                                engine.Check($"{dir}/{baseName}{d}{aff}{ext}", HashGuessStrategy.WordlistVariant, source);
                                engine.Check($"{dir}/{aff}{d}{baseName}{ext}", HashGuessStrategy.WordlistVariant, source);
                                checkedCandidates += 2;
                                affixCount += 2;
                                if ((checkedCandidates & 0x1fff) == 0)
                                    ReportThrottled(stage, checkedCandidates);
                                if (engine.RemainingUnknownCount == 0 || affixCount >= affixBudget) break;
                            }
                            if (engine.RemainingUnknownCount == 0 || affixCount >= affixBudget) break;
                        }
                        if (engine.RemainingUnknownCount == 0 || affixCount >= affixBudget) break;
                    }
                }

                // 3. Dynamic Numeric Sequence Extrapolation (discovers sequences and tests forward range)
                if (engine.RemainingUnknownCount > 0)
                {
                    var numberedPaths = pluginPaths.Where(p => Regex.IsMatch(Path.GetFileNameWithoutExtension(p), @"\d+")).Take(300);
                    foreach (var path in numberedPaths)
                    {
                        string dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
                        string baseName = Path.GetFileNameWithoutExtension(path);
                        string ext = Path.GetExtension(path);
                        if (string.IsNullOrEmpty(dir)) continue;

                        var match = Regex.Match(baseName, @"\d+");
                        if (match.Success && int.TryParse(match.Value, out int seenNum))
                        {
                            string prefix = baseName[..match.Index];
                            string suffix = baseName[(match.Index + match.Length)..];
                            int maxRange = Math.Clamp(seenNum + 30, 20, 250);

                            for (int num = 0; num <= maxRange; num++)
                            {
                                string candidate = $"{dir}/{prefix}{num}{suffix}{ext}";
                                engine.Check(candidate, HashGuessStrategy.NumberVariant, source);
                                checkedCandidates++;
                                if ((checkedCandidates & 0x1fff) == 0)
                                    ReportThrottled(stage, checkedCandidates);
                                if (engine.RemainingUnknownCount == 0) break;
                            }
                        }
                        if (engine.RemainingUnknownCount == 0) break;
                    }
                }

                // 4. Dynamic Vocabulary Word Substitution (self-learning from plugin corpus)
                if (engine.RemainingUnknownCount > 0 && pluginPaths.Count >= 2)
                {
                    var pluginWords = HashGuessEngine.BuildWordlist(pluginPaths.Select(Path.GetFileName));
                    if (pluginWords.Count > 0)
                    {
                        int subCount1 = SubstituteBasenameWordsCore(
                            engine,
                            pluginPaths.Take(1000),
                            pluginWords.Take(200),
                            oldWordCount: 1,
                            newWordCount: 1,
                            cancellationToken,
                            candidateBudget: 100_000,
                            source: source,
                            progress: current => ReportThrottled(stage, checkedCandidates + current));
                        checkedCandidates += subCount1;
                    }
                }

                // 5. Dynamic UI Component & Resolution Synthesis (Figma exports, scaled sprites & resolution matrices)
                if (engine.RemainingUnknownCount > 0)
                {
                    string[] uiComponents = {
                        "icon", "frame", "border", "divider", "bg", "background", "btn", "button",
                        "mask", "overlay", "badge", "header", "footer", "panel", "card", "accent", "arrow", "chevron"
                    };

                    string[] dimensions = {
                        "18x18", "12x24", "10x10", "13x13", "20x20", "24x24", "32x32", "64x64", "92x92", "112x112", "128x128", "256x256"
                    };

                    string[] uiExts = { ".png", ".svg", ".webm", ".jpg" };

                    int uiBudget = 50_000;
                    int uiCount = 0;
                    foreach (var dir in dirs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (engine.RemainingUnknownCount == 0 || uiCount >= uiBudget) break;

                        foreach (var comp in uiComponents)
                        {
                            foreach (var ext in uiExts)
                            {
                                foreach (var dim in dimensions)
                                {
                                    engine.Check($"{dir}/{comp}_{dim}{ext}", HashGuessStrategy.WordlistVariant, source);
                                    engine.Check($"{dir}/{comp}-{dim}{ext}", HashGuessStrategy.WordlistVariant, source);
                                    checkedCandidates += 2;
                                    uiCount += 2;
                                    if ((checkedCandidates & 0x1fff) == 0)
                                        ReportThrottled(stage, checkedCandidates);
                                    if (engine.RemainingUnknownCount == 0 || uiCount >= uiBudget) break;
                                }
                                if (engine.RemainingUnknownCount == 0 || uiCount >= uiBudget) break;
                            }
                            if (engine.RemainingUnknownCount == 0 || uiCount >= uiBudget) break;
                        }
                    }
                }
            }

            return checkedCandidates;
        }

        internal int RunCustomAttacks(
            HashGuessEngine engine,
            IProgress<HashGuessProgress> progress,
            CancellationToken cancellationToken,
            IReadOnlySet<string> selectedSubMethods = null)
        {
            ArgumentNullException.ThrowIfNull(engine);

            int checkedCandidates = 0;
            bool ShouldRun(string subId) => selectedSubMethods == null || selectedSubMethods.Contains(subId);

            var customProgressClock = Stopwatch.StartNew();
            void ReportCustomThrottled(string stageName, int currentTotal)
            {
                if (customProgressClock.ElapsedMilliseconds >= 80)
                {
                    progress?.Report(engine.CreateProgress(stageName, currentTotal));
                    customProgressClock.Restart();
                }
            }

            // Phase 1: High-precision Scoped Plugin Engine (Intra-directory cross-product, scoped modifiers, scoped substitutions)
            if (engine.RemainingUnknownCount > 0 && ShouldRun("lcu-custom-scoped"))
            {
                int count = RunScopedPluginAttacks(engine, progress, cancellationToken);
                checkedCandidates += count;
            }

            // Phase 2: Deep Directory Mirroring (/images/, /assets/, root)
            if (engine.RemainingUnknownCount > 0 && ShouldRun("lcu-custom-mirroring"))
            {
                string stage = "LCU Custom: Directory mirroring";
                progress?.Report(engine.CreateProgress(stage, checkedCandidates));
                int count = MirrorDirectories(
                    engine,
                    cancellationToken,
                    candidateBudget: int.MaxValue,
                    progress: current => ReportCustomThrottled(stage, checkedCandidates + current));
                checkedCandidates += count;
            }

            // Phase 3: Universal Modifier Matrix across all plugins
            if (engine.RemainingUnknownCount > 0 && ShouldRun("lcu-custom-modifiers"))
            {
                string stage = "LCU Custom: Universal modifier attack";
                progress?.Report(engine.CreateProgress(stage, checkedCandidates));
                int count = UniversalPluginModifierAttack(
                    engine,
                    cancellationToken,
                    pluginPattern: "rcp-*",
                    progress: current => ReportCustomThrottled(stage, checkedCandidates + current));
                checkedCandidates += count;
            }

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
                candidateBudget: 250_000_000,
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
