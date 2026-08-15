using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LeagueToolkit.Core.Wad;

namespace BenchmarkApp.Diagnostics.Hashes
{
    internal static class LcuUnknownsAuditDiagnostic
    {
        public static void Run(string pbeRoot)
        {
            string unknownsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AssetsManager", "hash_lab", "unknowns.lcu.txt");

            if (!File.Exists(unknownsPath))
            {
                Console.WriteLine($"Unknowns file not found at: {unknownsPath}");
                return;
            }

            var unknownHashes = new HashSet<ulong>();
            foreach (string line in File.ReadLines(unknownsPath))
            {
                string trimmed = line.Trim();
                if (ulong.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                {
                    unknownHashes.Add(hash);
                }
            }

            Console.WriteLine("==================================================");
            Console.WriteLine($"    LCU UNKNOWNS FORENSIC AUDIT ({unknownHashes.Count} hashes)");
            Console.WriteLine("==================================================");

            string pluginsDir = Path.Combine(pbeRoot, "Plugins");
            if (!Directory.Exists(pluginsDir))
            {
                pluginsDir = Path.Combine(pbeRoot, "LeagueClient", "Plugins");
            }
            if (!Directory.Exists(pluginsDir))
            {
                var found = Directory.GetDirectories(pbeRoot, "*Plugins*", SearchOption.AllDirectories);
                if (found.Length > 0) pluginsDir = found[0];
            }

            if (!Directory.Exists(pluginsDir))
            {
                Console.WriteLine($"Error: Plugins directory not found in {pbeRoot}");
                return;
            }

            var wads = Directory.EnumerateFiles(pluginsDir, "*.wad", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".wad", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine($"Scanning {wads.Count} plugin WADs...\n");

            var hashToWad = new Dictionary<ulong, (string WadName, string WadPath, WadChunk Chunk)>();
            var pluginStats = new Dictionary<string, List<(ulong Hash, WadChunk Chunk, string FileType, string Sample)>>(StringComparer.OrdinalIgnoreCase);

            foreach (string wadPath in wads)
            {
                string relWadPath = Path.GetRelativePath(pluginsDir, wadPath).Replace('\\', '/');
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        if (unknownHashes.Contains(pair.Key))
                        {
                            hashToWad[pair.Key] = (relWadPath, wadPath, pair.Value);

                            string fileType = "UNKNOWN";
                            string sample = string.Empty;

                            try
                            {
                                using var owner = wad.LoadChunkDecompressed(pair.Value);
                                var seg = owner.DangerousGetArray();
                                byte[] data = seg.Array[seg.Offset..(seg.Offset + seg.Count)];
                                fileType = DetectFileType(data, out sample);
                            }
                            catch (Exception ex)
                            {
                                fileType = $"ERR({ex.Message})";
                            }

                            if (!pluginStats.TryGetValue(relWadPath, out var list))
                            {
                                list = new List<(ulong, WadChunk, string, string)>();
                                pluginStats[relWadPath] = list;
                            }
                            list.Add((pair.Key, pair.Value, fileType, sample));
                        }
                    }
                }
                catch
                {
                }
            }

            Console.WriteLine("\n[1] UNKNOWNS DISTRIBUTION BY PLUGIN:");
            Console.WriteLine("--------------------------------------------------");
            foreach (var kv in pluginStats.OrderByDescending(p => p.Value.Count))
            {
                var typeCounts = kv.Value.GroupBy(item => item.FileType)
                    .Select(g => $"{g.Key}: {g.Count()}");
                Console.WriteLine($"  {kv.Key.PadRight(42)} -> {kv.Value.Count,3} hashes ({string.Join(", ", typeCounts)})");
            }

            Console.WriteLine("\n[2] OVERALL FORMAT BREAKDOWN:");
            Console.WriteLine("--------------------------------------------------");
            var allByFormat = pluginStats.SelectMany(p => p.Value)
                .GroupBy(item => item.FileType)
                .OrderByDescending(g => g.Count());
            foreach (var group in allByFormat)
            {
                Console.WriteLine($"  {group.Key.PadRight(20)}: {group.Count(),3} files");
            }

            string hashesLcuPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AssetsManager", "hashes", "hashes.lcu.txt");
            var knownLcu = new Dictionary<ulong, string>();
            if (File.Exists(hashesLcuPath))
            {
                foreach (string line in File.ReadLines(hashesLcuPath))
                {
                    string trimmed = line.Trim();
                    int space = trimmed.IndexOf(' ');
                    if (space > 0 && ulong.TryParse(trimmed.Substring(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    {
                        knownLcu[hash] = trimmed.Substring(space + 1);
                    }
                    else if (trimmed.Length > 0 && !trimmed.Contains(' '))
                    {
                        knownLcu[LeagueToolkit.Hashing.XxHash64Ext.Hash(trimmed.ToLowerInvariant())] = trimmed;
                    }
                }
            }

            var staticAssetsWad = wads.FirstOrDefault(w => w.Contains("rcp-fe-lol-static-assets", StringComparison.OrdinalIgnoreCase));
            if (staticAssetsWad != null)
            {
                using var wad = new WadFile(staticAssetsWad);
                var knownPathsInStatic = new List<string>();
                var unknownChunksInStatic = new List<(ulong Hash, WadChunk Chunk)>();

                foreach (var pair in wad.Chunks)
                {
                    if (knownLcu.TryGetValue(pair.Key, out string path))
                    {
                        knownPathsInStatic.Add(path);
                    }
                    else if (unknownHashes.Contains(pair.Key))
                    {
                        unknownChunksInStatic.Add((pair.Key, pair.Value));
                    }
                }

                Console.WriteLine("\n[3] EXPERIMENTAL ATTACK ON static-assets (99 UNKNOWNS):");
                Console.WriteLine("--------------------------------------------------");
                var staticUnknownSet = unknownChunksInStatic.Select(u => u.Hash).ToHashSet();
                int matched = 0;

                void TryCandidate(string path)
                {
                    ulong hash = LeagueToolkit.Hashing.XxHash64Ext.Hash(path.ToLowerInvariant());
                    if (staticUnknownSet.Contains(hash))
                    {
                        matched++;
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  [MATCH!] 0x{hash:x16} -> {path}");
                        Console.ResetColor();
                        staticUnknownSet.Remove(hash);
                    }
                }

                // Test 6: Figma SVG structure search across all plugins
                Console.WriteLine("\n>>> Inspecting Known SVGs across all plugins for matching SVG patterns...");
                foreach (var w in wads)
                {
                    try
                    {
                        using var pluginWad = new WadFile(w);
                        foreach (var p in pluginWad.Chunks)
                        {
                            if (knownLcu.TryGetValue(p.Key, out string kPath) && kPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                            {
                                using var owner = pluginWad.LoadChunkDecompressed(p.Value);
                                var seg = owner.DangerousGetArray();
                                string text = Encoding.UTF8.GetString(seg.Array, seg.Offset, seg.Count);
                                if (text.Contains("path-1-outside-1") || text.Contains("3419_8358") || text.Contains("5045_77102") || text.Contains("4003_3667") || text.Contains("5519_4182"))
                                {
                                    Console.WriteLine($"  [SVG PATTERN MATCH] {kPath}");
                                }
                            }
                        }
                    }
                    catch { }
                }
                var allStaticBasenames = knownPathsInStatic.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var allStaticDirs = knownPathsInStatic.Select(p => Path.GetDirectoryName(p)?.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).Where(d => !string.IsNullOrEmpty(d)).ToList();

                // Add mirrored directories (with and without images/ prefix)
                var expandedDirs = new HashSet<string>(allStaticDirs, StringComparer.OrdinalIgnoreCase);
                foreach (var d in allStaticDirs)
                {
                    if (d.Contains("/global/default/images/"))
                        expandedDirs.Add(d.Replace("/global/default/images/", "/global/default/"));
                    else if (d.Contains("/global/default/"))
                        expandedDirs.Add(d.Replace("/global/default/", "/global/default/images/"));
                }

                foreach (var dir in expandedDirs)
                foreach (var name in allStaticBasenames)
                {
                    TryCandidate($"{dir}/{name}");
                }
                Console.WriteLine($"  After full static directory/basename cross-product: {matched} matches found!");

                // Test 7: Exalted, Transcendent, Sanctum, Gacha, and Rarity Variants
                string[] tiersText = { "tierone", "tiertwo", "tierthree", "tierfour", "tierfive", "tier-one", "tier-two", "tier-three", "tier-four", "tier-five", "tier1", "tier2", "tier3", "tier4", "tier5", "1", "2", "3", "4", "5" };
                string[] sides = { "", "-back", "-front", "_back", "_front", "-bg", "_bg", "-frame", "_frame", "-glow", "-border", "-card", "-base", "-active", "-hover" };
                string[] rarities = { "exalted", "transcendent", "mythic", "ultimate", "legendary", "epic", "norarity", "kexalted", "ktranscendent", "kmythic", "kultimate", "klegendary", "kepic", "knorarity", "rare", "common", "deluxe", "prestige" };
                string[] gachaFolders = { "exalted", "images/exalted", "transcendent", "images/transcendent", "sanctum", "images/sanctum", "sanctuary", "images/sanctuary", "gacha", "images/gacha", "rarity", "images/rarity", "rarity/gem-borders", "images/rarity/gem-borders", "mythic-shop", "images/mythic-shop", "sparks", "images/sparks", "ancient-sparks", "images/ancient-sparks" };
                string[] gachaNames = { "card-frame", "card", "frame", "banner", "gem", "border", "badge", "bg", "background", "spark", "icon", "particle", "glow", "vignette", "burst", "modal-bg", "hub-bg", "pedestal", "portal", "pillar", "curtain", "button", "button-bg", "button-claim", "button-draw", "draw-1", "draw-10", "draw-single", "draw-multi", "roll", "chest" };

                foreach (var folder in gachaFolders)
                {
                    foreach (var rarity in rarities)
                    {
                        foreach (var ext in new[] { "svg", "png", "webm", "jpg", "ogg" })
                        {
                            TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{folder}/{rarity}.{ext}");
                            TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{folder}/gem-{rarity}.{ext}");
                            TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{folder}/border-{rarity}.{ext}");
                            TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{folder}/card-{rarity}.{ext}");
                        }
                    }

                    foreach (var name in gachaNames)
                    foreach (var t in tiersText)
                    foreach (var s in sides)
                    foreach (var ext in new[] { "svg", "png", "webm", "jpg", "ogg" })
                    {
                        TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{folder}/{name}-{t}{s}.{ext}");
                        TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{folder}/{name}_{t}{s}.{ext}");
                        TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{folder}/{name}{s}.{ext}");
                    }
                }
                // Test 8: reward-tracker exhaustive patterns
                string[] trackerFolders = { "reward-tracker", "images/reward-tracker", "milestone-tracker", "images/milestone-tracker", "pass-tracker", "images/pass-tracker" };
                string[] trackerStates = { "future", "completed", "current", "locked", "claimed", "unlocked", "active", "pending", "in-progress", "inprogress", "done", "upcoming", "past", "initial", "final", "selected", "hover", "disabled", "next", "prev", "previous", "first", "last" };
                string[] trackerPositions = { "left", "right", "center", "middle", "mid", "top", "bottom", "end", "start", "line", "bar", "track", "fill", "bg", "node", "dot", "arrow", "cap", "edge", "segment", "connector" };

                foreach (var folder in trackerFolders)
                {
                    foreach (var state in trackerStates)
                    foreach (var pos in trackerPositions)
                    foreach (var ext in new[] { "svg", "png", "webm", "jpg" })
                    {
                        TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{folder}/{state}-{pos}.{ext}");
                        TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{folder}/{state}_{pos}.{ext}");
                        TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{folder}/{pos}-{state}.{ext}");
                        TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{folder}/{pos}_{state}.{ext}");
                        TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{folder}/{state}.{ext}");
                        TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{folder}/{pos}.{ext}");
                    }
                }
                Console.WriteLine($"  After reward-tracker attack: {matched} matches found!");
                string[] modes = { "cherry", "strawberry", "arena", "swarm", "tft", "troves", "milestones", "mastery", "challenges", "honor", "clash", "aram", "kiwi", "loot", "store", "event-pass", "pass", "sanctuary", "hub", "radial" };
                string[] subTypes = { "icons", "images", "badges", "borders", "cards", "tokens", "backgrounds", "crests", "emblems", "rewards", "tiers", "hud", "wheel" };
                string[] qualifiers = { "active", "hover", "idle", "disabled", "selected", "hovered", "locked", "unlocked", "completed", "claimed", "gold", "silver", "bronze", "small", "large", "medium", "mini", "bg", "icon", "border", "v2", "v3", "glow", "glow-loop", "intro", "outro", "loop" };

                foreach (var mode in modes)
                foreach (var sub in subTypes)
                foreach (var ext in new[] { "png", "svg", "webm", "ogg", "jpg" })
                {
                    TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{mode}/{mode}.{ext}");
                    TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{mode}/{sub}.{ext}");
                    TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/images/{mode}/{sub}.{ext}");
                    TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{mode}/{sub}/{mode}.{ext}");
                    TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/images/{mode}/{sub}/{mode}.{ext}");
                    foreach (var q in qualifiers)
                    {
                        TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{mode}/{q}.{ext}");
                        TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/images/{mode}/{q}.{ext}");
                        TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{mode}/{sub}_{q}.{ext}");
                        TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{mode}/{sub}-{q}.{ext}");
                        TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/images/{mode}/{sub}_{q}.{ext}");
                        TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/images/{mode}/{sub}-{q}.{ext}");
                        TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{mode}/{sub}/{q}.{ext}");
                        TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/images/{mode}/{sub}/{q}.{ext}");
                    }
                }
                Console.WriteLine($"  After Mode/Event keywords attack: {matched} matches found!");

                // Test 2: Ranked tiers, splits, and crests variants
                string[] tiers = { "iron", "bronze", "silver", "gold", "platinum", "emerald", "diamond", "master", "grandmaster", "challenger", "unranked", "unranked-tft" };
                string[] ranks = { "i", "ii", "iii", "iv", "1", "2", "3", "4" };
                string[] crestFolders = { "ranked-mini-crests", "images/ranked-mini-crests", "ranked-crests", "images/ranked-crests", "ranked-emblems", "images/ranked-emblems" };
                string[] exts = { "png", "svg", "webm", "jpg" };

                foreach (var folder in crestFolders)
                {
                    foreach (var tier in tiers)
                    {
                        foreach (var ext in exts)
                        {
                            TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{folder}/{tier}.{ext}");
                            TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{folder}/{tier}_mini.{ext}");
                            TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{folder}/mini_{tier}.{ext}");
                            foreach (var rank in ranks)
                            {
                                TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{folder}/{tier}_{rank}.{ext}");
                                TryCandidate($"plugins/rcp-fe-lol-static-assets/global/default/{folder}/{tier}-{rank}.{ext}");
                            }
                        }
                    }
                }
                Console.WriteLine($"  After ranked crests attack: {matched} matches found!");

                // Test 3: Videos (ranked promotions, splits, checkpoints)
                string[] videoFolders = { "videos/ranked", "videos", "images/videos/ranked", "images/videos" };
                string[] checkpoints = { "1-1", "1-2", "1-3", "2-1", "2-2", "2-3", "3-1", "3-2", "3-3" };

                Console.WriteLine($"\n>>> Remaining {staticUnknownSet.Count} Unresolved in static-assets:");
                foreach (var item in unknownChunksInStatic.Where(u => staticUnknownSet.Contains(u.Hash)))
                {
                    string fileType = "UNKNOWN";
                    string detail = string.Empty;
                    try
                    {
                        using var owner = wad.LoadChunkDecompressed(item.Chunk);
                        var seg = owner.DangerousGetArray();
                        byte[] data = seg.Array[seg.Offset..(seg.Offset + seg.Count)];
                        fileType = DetectFileType(data, out detail);
                        if (fileType == "SVG")
                        {
                            string text = Encoding.UTF8.GetString(data);
                            var ids = Regex.Matches(text, @"id=""([^""]+)""").Cast<Match>().Select(m => m.Groups[1].Value).Take(3);
                            detail = $"ids=[{string.Join(", ", ids)}]";
                        }
                    }
                    catch { }
                    Console.WriteLine($"  0x{item.Hash:x16} | {fileType.PadRight(5)} | {item.Chunk.UncompressedSize,7} B | {detail}");
                }
            }
        }

        private static string DetectFileType(byte[] data, out string sample)
        {
            sample = string.Empty;
            if (data == null || data.Length == 0) return "EMPTY";

            if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            {
                if (data.Length >= 24)
                {
                    int width = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
                    int height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
                    sample = $"{width}x{height} px";
                }
                return "PNG";
            }

            if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                return "JPG";

            if (data.Length >= 4 && data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
                return "WEBM";

            if (data.Length >= 4 && data[0] == 0x4F && data[1] == 0x67 && data[2] == 0x67 && data[3] == 0x53)
                return "OGG";

            if (data.Length >= 4 && ((data[0] == 0x00 && data[1] == 0x01 && data[2] == 0x00 && data[3] == 0x00) ||
                                     (data[0] == 0x77 && data[1] == 0x4F && data[2] == 0x46 && data[3] == 0x46)))
                return "FONT";

            string text = null;
            try
            {
                text = Encoding.UTF8.GetString(data);
            }
            catch
            {
            }

            if (text != null)
            {
                string trimmed = text.TrimStart();
                if (trimmed.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
                {
                    var matchId = Regex.Match(trimmed, @"id=""([^""]+)""", RegexOptions.IgnoreCase);
                    if (matchId.Success) sample = $"id=\"{matchId.Groups[1].Value}\"";
                    return "SVG";
                }

                if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
                {
                    var keys = Regex.Matches(trimmed.Substring(0, Math.Min(trimmed.Length, 400)), @"""([a-zA-Z0-9_\-\$]+)""\s*:")
                        .Cast<Match>()
                        .Select(m => m.Groups[1].Value)
                        .Distinct()
                        .Take(4);
                    sample = $"keys: [{string.Join(", ", keys)}]";
                    return "JSON";
                }

                if (trimmed.StartsWith("import ") || trimmed.StartsWith("export ") || trimmed.StartsWith("function") ||
                    trimmed.StartsWith("(function") || trimmed.StartsWith("\"use strict\"") || trimmed.StartsWith("'use strict'"))
                {
                    sample = trimmed.Substring(0, Math.Min(trimmed.Length, 60)).Replace("\r", "").Replace("\n", " ");
                    return "JS";
                }

                if (trimmed.Contains("{") && (trimmed.Contains("color:") || trimmed.Contains("margin:") || trimmed.Contains("display:")))
                {
                    sample = trimmed.Substring(0, Math.Min(trimmed.Length, 60)).Replace("\r", "").Replace("\n", " ");
                    return "CSS";
                }

                if (trimmed.StartsWith("<!DOCTYPE html") || trimmed.StartsWith("<html") || trimmed.StartsWith("<template"))
                {
                    sample = trimmed.Substring(0, Math.Min(trimmed.Length, 60)).Replace("\r", "").Replace("\n", " ");
                    return "HTML";
                }

                sample = trimmed.Substring(0, Math.Min(trimmed.Length, 60)).Replace("\r", "").Replace("\n", " ");
                return "TEXT";
            }

            return "BIN/DATA";
        }
    }
}
