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

            var dirGroups = pathList.GroupBy(p => PathUtils.NormalizeSeparators(Path.GetDirectoryName(p)))
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
                    .GroupBy(p => PathUtils.NormalizeSeparators(Path.GetDirectoryName(p)))
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
                        string dir = PathUtils.NormalizeSeparators(Path.GetDirectoryName(path));
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
                        int subCount1 = _SubstituteBasenameWords(
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

    }
}
