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
using AssetsManager.Services.Hashes;

namespace AssetsManager.Services.Hashes.Guessers.Lcu
{
    internal sealed partial class LcuHashGuesser
    {
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

            string[] locales = { "ar_ae", "ar_eg", "cs_cz", "de_de", "el_gr", "en_au", "en_gb", "en_ph", "en_pl", "en_sg", "en_us", "es_ar", "es_es", "es_mx", "fr_fr", "hu_hu", "id_id", "it_it", "ja_jp", "ko_kr", "ms_my", "pl_pl", "pt_br", "ro_ro", "ru_ru", "th_th", "tr_tr", "vi_vn", "vn_vn", "zh_cn", "zh_my", "zh_tw" };
            string[] regions = { "br", "cn", "eun", "eune", "euw", "garena2", "garena3", "id", "jp", "kr", "la", "la1", "la2", "lan", "las", "me1", "na", "oc", "oc1", "oce", "pbe", "ph", "ph2", "ru", "sg", "sg2", "tencent", "th", "th2", "tr", "tw", "tw2", "vn", "vn2" };

            IEnumerable<string> sanitizerPaths = Enumerable.Range(0, 5).SelectMany(index =>
                new[] { "filter", "unfilter", "whitelist" }.SelectMany(action =>
                    new[] { $"{index}.{action}.csv" }
                        .Concat(locales.Select(locale =>
                        {
                            string[] parts = locale.Split('_');
                            return $"{index}.{action}.language.{parts[0]}.csv";
                        }))
                        .Concat(locales.Select(locale =>
                        {
                            string[] parts = locale.Split('_');
                            return $"{index}.{action}.country.{parts[1]}.csv";
                        }))
                        .Concat(regions
                            .Select(region => $"{index}.{action}.region.{region}.csv"))
                        .Concat(locales.Select(locale => $"{index}.{action}.locale.{locale}.csv"))));

            IEnumerable<string> sanitizerNames = new[]
            {
                "allowedchars", "breakingchars", "projectedchars", "projectedchars1337",
                "punctuationchars", "variantaliases"
            }.SelectMany(name =>
                locales.Select(locale => $"{name}.locale.{locale}.txt")
                    .Concat(locales.Select(locale => $"{name}.language.{locale.Split('_')[0]}.txt")));

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
                .Select(p => PathUtils.NormalizeSeparators(Path.GetDirectoryName(p)))
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
                if ((checkedCount & 0x1fff) == 0)
                {
                    progress?.Invoke(checkedCount);
                }
            }

            progress?.Invoke(checkedCount);
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

            string[] locales = { "ar_ae", "ar_eg", "cs_cz", "de_de", "el_gr", "en_au", "en_gb", "en_ph", "en_pl", "en_sg", "en_us", "es_ar", "es_es", "es_mx", "fr_fr", "hu_hu", "id_id", "it_it", "ja_jp", "ko_kr", "ms_my", "pl_pl", "pt_br", "ro_ro", "ru_ru", "th_th", "tr_tr", "vi_vn", "vn_vn", "zh_cn", "zh_my", "zh_tw" };
            string[] regions = { "br", "cn", "eun", "eune", "euw", "garena2", "garena3", "id", "jp", "kr", "la", "la1", "la2", "lan", "las", "me1", "na", "oc", "oc1", "oce", "pbe", "ph", "ph2", "ru", "sg", "sg2", "tencent", "th", "th2", "tr", "tw", "tw2", "vn", "vn2", "global" };

            IReadOnlyList<string> known = KnownPaths.ToList();
            IReadOnlyList<string> languages = locales.Append("default").ToList();
            var regionLanguages = regions
                .SelectMany(region => languages, (region, language) => (Region: region, Language: language))
                .ToList();

            const string source = "Generated region or locale variant";
            var regionLangRegex = new Regex(@"^plugins/([^/]+)/[^/]+/[^/]+/");
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
                        regionLangRegex.Replace(path, replacement),
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
            return _SubstituteBasenameWords(
                engine, paths, words ?? BuildWordlist(), oldWordCount, newWordCount,
                cancellationToken, candidateBudget, "LCU basename word substitution", progress);
        }


        internal int AddBasenameWord(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            Action<int> progress = null) =>
            _AddBasenameWord(
                engine,
                KnownPaths,
                BuildWordlist(),
                cancellationToken,
                candidateBudget: 150_000_000,
                source: "LCU basename word addition",
                progress: progress);


        internal int SubstituteNumbers(
            HashGuessEngine engine,
            CancellationToken cancellationToken,
            int maximum = 10000,
            int? digits = null,
            Action<int> progress = null) =>
            base._SubstituteNumbers(
                engine,
                KnownPaths.Where(IncludeNumberPath),
                maximum,
                digits,
                inferDigits: false,
                cancellationToken: cancellationToken,
                source: "Generated numeric variant",
                progress: progress);

    }
}
