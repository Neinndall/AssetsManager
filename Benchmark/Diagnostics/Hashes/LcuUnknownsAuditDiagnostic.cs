using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
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

            Console.WriteLine("\n[3] DEEP CHUNK INSPECTION ACROSS ALL UNKNOWNS:");
            Console.WriteLine("--------------------------------------------------");
            var allUnknownChunks = pluginStats.SelectMany(p => p.Value.Select(v => (Plugin: p.Key, Hash: v.Hash, Chunk: v.Chunk, FileType: v.FileType, Sample: v.Sample))).ToList();

            var solvedPaths = new Dictionary<ulong, string>();

            void TestCandidate(string path)
            {
                ulong h = LeagueToolkit.Hashing.XxHash64Ext.Hash(path.ToLowerInvariant());
                if (unknownHashes.Contains(h) && !solvedPaths.ContainsKey(h))
                {
                    solvedPaths[h] = path;
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"  [CRACKED!] 0x{h:x16} -> {path}");
                    Console.ResetColor();
                }
            }

            // Test game data numeric paths
            Console.WriteLine("\n>>> Testing rcp-be-lol-game-data numeric IDs...");
            string[] gameDataFolders = {
                "v1/items/icons2d", "v1/champion-icons", "v1/profile-icons", "v1/companion-species",
                "v1/tft-items", "v1/perk-images/statmods", "v1/ward-skins", "v1/summoner-spells",
                "v1/hextech-items", "v1/perk-images", "v1/emotes", "v1/arenas", "v1/augments"
            };

            for (int id = 0; id <= 200000; id++)
            {
                foreach (var folder in gameDataFolders)
                {
                    TestCandidate($"plugins/rcp-be-lol-game-data/global/default/{folder}/{id}.png");
                }
            }

            foreach (var item in allUnknownChunks)
            {
                string pluginName = Path.GetFileNameWithoutExtension(item.Plugin);
                if (pluginName.EndsWith(".wad", StringComparison.OrdinalIgnoreCase))
                    pluginName = Path.GetFileNameWithoutExtension(pluginName);

                // Try to find the wad file
                string wadPath = wads.FirstOrDefault(w => w.Contains(pluginName, StringComparison.OrdinalIgnoreCase));
                if (wadPath == null) continue;

                try
                {
                    using var wad = new WadFile(wadPath);
                    if (!wad.Chunks.TryGetValue(item.Hash, out var chunk)) continue;
                    using var owner = wad.LoadChunkDecompressed(chunk);
                    var seg = owner.DangerousGetArray();
                    byte[] data = seg.Array[seg.Offset..(seg.Offset + seg.Count)];

                    // Extract all ASCII/UTF8 strings >= 4 chars from the chunk
                    var text = Encoding.ASCII.GetString(data);
                    var strMatches = Regex.Matches(text, @"[a-zA-Z0-9_\-\.\/]{4,}");
                    var extractedWords = strMatches.Cast<Match>().Select(m => m.Value).Distinct().ToList();

                    // Print sample info if it has text or interesting SVG / PNG data
                    if (item.FileType == "SVG")
                    {
                        var utf8Text = Encoding.UTF8.GetString(data);
                        var ids = Regex.Matches(utf8Text, @"id=""([^""]+)""").Cast<Match>().Select(m => m.Groups[1].Value).Take(4);
                        var classes = Regex.Matches(utf8Text, @"class=""([^""]+)""").Cast<Match>().Select(m => m.Groups[1].Value).Take(4);
                        Console.WriteLine($"  0x{item.Hash:x16} | {pluginName.PadRight(30)} | SVG  | ids=[{string.Join(", ", ids)}] classes=[{string.Join(", ", classes)}]");
                    }
                    else if (item.FileType == "PNG" || item.FileType == "JPG" || item.FileType == "WEBM" || item.FileType == "OGG")
                    {
                        // Check for embedded text chunks or metadata
                        var interesting = extractedWords.Where(w => w.Contains('/') || w.Contains('.') || w.Contains('_') || w.Contains('-')).Take(5).ToList();
                        string extra = interesting.Count > 0 ? $" | strings=[{string.Join(", ", interesting)}]" : "";
                        Console.WriteLine($"  0x{item.Hash:x16} | {pluginName.PadRight(30)} | {item.FileType.PadRight(5)} | {item.Sample,-15} | {chunk.UncompressedSize,7} B{extra}");
                    }
                }
                catch { }
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
                        solvedPaths[hash] = path;
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  [MATCH!] 0x{hash:x16} -> {path}");
                        Console.ResetColor();
                        staticUnknownSet.Remove(hash);
                    }
                }

            // Universal JS/JSON/CSS Bundle Harvester across ALL WADs
            Console.WriteLine("\n==================================================");
            Console.WriteLine(">>> Universal JS/JSON/CSS Bundle String Harvester:");
            Console.WriteLine("==================================================");
            var harvestedStrings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allPluginNames = wads.Select(w =>
            {
                string name = Path.GetFileNameWithoutExtension(w);
                if (name.EndsWith(".wad", StringComparison.OrdinalIgnoreCase))
                    name = Path.GetFileNameWithoutExtension(name);
                return name;
            }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var w in wads)
            {
                string pluginName = Path.GetFileNameWithoutExtension(w);
                if (pluginName.EndsWith(".wad", StringComparison.OrdinalIgnoreCase))
                    pluginName = Path.GetFileNameWithoutExtension(pluginName);

                try
                {
                    using var pluginWad = new WadFile(w);
                    foreach (var p in pluginWad.Chunks)
                    {
                        if (p.Value.UncompressedSize > 20 && p.Value.UncompressedSize < 15_000_000)
                        {
                            using var owner = pluginWad.LoadChunkDecompressed(p.Value);
                            var seg = owner.DangerousGetArray();
                            byte[] data = seg.Array[seg.Offset..(seg.Offset + seg.Count)];

                            if (data[0] == 0x89 && data[1] == 0x50) continue; // PNG
                            if (data[0] == 0x1A && data[1] == 0x45) continue; // WEBM
                            if (data[0] == 0x4F && data[1] == 0x67) continue; // OGG
                            if (data[0] == 0xFF && data[1] == 0xD8) continue; // JPG

                            string text;
                            try { text = Encoding.UTF8.GetString(data); } catch { continue; }

                            if (text.Contains("function") || text.Contains("export") || text.Contains("import") ||
                                text.Contains("{") || text.Contains("<") || text.Contains("webpack"))
                            {
                                var matches = Regex.Matches(text, @"[""']([^""'\r\n\t]{3,120})[""']|`([^`\r\n\t]{3,120})`");
                                foreach (Match m in matches)
                                {
                                    string s = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                                    if (s.Contains('.') || s.Contains('/') || s.Contains('_') || s.Contains('-'))
                                    {
                                        harvestedStrings.Add(s);
                                    }
                                }

                                var pathMatches = Regex.Matches(text, @"(?:/fe/|/assets/|/images/|/data/|/v1/)[a-zA-Z0-9_\-\.\/]+");
                                foreach (Match m in pathMatches)
                                {
                                    harvestedStrings.Add(m.Value);
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            Console.WriteLine($"Harvested {harvestedStrings.Count} unique tokens and string literals from all bundles!");

            foreach (var str in harvestedStrings)
            {
                string clean = str.TrimStart('/');
                if (clean.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase))
                {
                    TestCandidate(clean);
                }

                string ext = Path.GetExtension(clean);
                if (!string.IsNullOrEmpty(ext) && ext.Length <= 5)
                {
                    foreach (var plugin in allPluginNames)
                    {
                        TestCandidate($"plugins/{plugin}/global/default/{clean}");
                        TestCandidate($"plugins/{plugin}/global/default/assets/{clean}");
                        TestCandidate($"plugins/{plugin}/global/default/images/{clean}");
                        TestCandidate($"plugins/{plugin}/{clean}");
                    }
                }
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

            // [7] AUDIT: Store, TFT Troves, and Loot
            foreach (string targetPlugin in new[] { "rcp-fe-lol-store", "rcp-fe-lol-tft-troves", "rcp-fe-lol-loot" })
            {
                var targetWad = wads.FirstOrDefault(w => w.Contains(targetPlugin, StringComparison.OrdinalIgnoreCase));
                if (targetWad == null) continue;

                Console.WriteLine($"\n==================================================");
                Console.WriteLine($"  DEEP FORENSIC ANALYSIS: {targetPlugin}");
                Console.WriteLine($"==================================================");

                using var wad = new WadFile(targetWad);
                var knownPaths = new List<string>();
                var unknownChunks = new List<(ulong Hash, WadChunk Chunk)>();

                foreach (var pair in wad.Chunks)
                {
                    if (knownLcu.TryGetValue(pair.Key, out string path))
                        knownPaths.Add(path);
                    else if (unknownHashes.Contains(pair.Key))
                        unknownChunks.Add((pair.Key, pair.Value));
                }

                Console.WriteLine($"Total chunks: {wad.Chunks.Count} | Resolved: {knownPaths.Count} | Unknown: {unknownChunks.Count}");

                Console.WriteLine($"\n>>> Unknown Chunks in {targetPlugin}:");
                foreach (var item in unknownChunks)
                {
                    string fileType = "UNKNOWN";
                    string detail = string.Empty;
                    try
                    {
                        using var owner = wad.LoadChunkDecompressed(item.Chunk);
                        var seg = owner.DangerousGetArray();
                        byte[] data = seg.Array[seg.Offset..(seg.Offset + seg.Count)];
                        fileType = DetectFileType(data, out detail);
                        var matches = Regex.Matches(Encoding.UTF8.GetString(data), @"[a-zA-Z0-9_\-\.\/]{4,}");
                        var sampleStrings = matches.Cast<Match>().Select(m => m.Value).Distinct().Take(6);
                        detail += $" | strings: [{string.Join(", ", sampleStrings)}]";
                    }
                    catch {}
                    Console.WriteLine($"  0x{item.Hash:x16} | {fileType.PadRight(5)} | {item.Chunk.UncompressedSize,7} B | {detail}");
                }

                Console.WriteLine($"\n>>> Known Files in {targetPlugin}:");
                foreach (var k in knownPaths.Take(50))
                {
                    Console.WriteLine($"    {k}");
                }

                Console.WriteLine($"\n>>> Testing Candidate Patterns for {targetPlugin}:");
                var pluginUnknownSet = unknownChunks.Select(u => u.Hash).ToHashSet();
                int pluginMatches = 0;

                void TryPluginCandidate(string path)
                {
                    ulong hash = LeagueToolkit.Hashing.XxHash64Ext.Hash(path.ToLowerInvariant());
                    if (pluginUnknownSet.Contains(hash))
                    {
                        pluginMatches++;
                        solvedPaths[hash] = path;
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  [MATCH!] 0x{hash:x16} -> {path}");
                        Console.ResetColor();
                        pluginUnknownSet.Remove(hash);
                    }
                }

                // Cross-directory / Sibling attack within plugin
                var basenames = knownPaths.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var pluginDirs = knownPaths.Select(p => Path.GetDirectoryName(p)?.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).Where(d => !string.IsNullOrEmpty(d)).ToList();

                foreach (var d in pluginDirs)
                foreach (var b in basenames)
                {
                    TryPluginCandidate($"{d}/{b}");
                }

                // Readme test
                foreach (var d in pluginDirs)
                {
                    TryPluginCandidate($"{d}/README.md");
                    TryPluginCandidate($"{d}/readme.txt");
                    TryPluginCandidate($"{d}/readme.md");
                    TryPluginCandidate($"{d}/README.txt");
                }

                // TFT Troves item / banner variants
                if (targetPlugin == "rcp-fe-lol-tft-troves")
                {
                    string[] tftSubfolders = { "images", "images/rotational-shop", "images/troves", "images/banners", "images/cards", "images/hub", "images/store", "images/home", "images/tokens", "rotational-shop", "troves", "banners", "cards" };
                    string[] prefixes = { "tft_troves_", "tft_trove_", "troves_", "trove_", "tft_banner_", "tft_icon_", "tft_bg_", "tft_card_", "tft_modal_", "tft_splash_", "tft_header_", "tft_preview_", "tft_button_", "tft_holder_", "tft_currency_", "banner_", "bg_", "icon_", "filter-icon-", "tft_filter_", "tft_" };
                    var words = HashGuessEngine.BuildWordlist(knownLcu.Values.Where(p => p.Contains("tft", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileName));

                    foreach (var folder in tftSubfolders)
                    foreach (var p in prefixes)
                    foreach (var w in words)
                    {
                        TryPluginCandidate($"plugins/rcp-fe-lol-tft-troves/global/default/{folder}/{p}{w}.png");
                        TryPluginCandidate($"plugins/rcp-fe-lol-tft-troves/global/default/{folder}/{w}.png");
                    }
                }

                // Loot items, icons, and videos
                if (targetPlugin == "rcp-fe-lol-loot")
                {
                    string[] lootTypes = { "chest", "capsule", "orb", "material", "tournamentlogo", "gem", "key", "token", "egg", "forge", "crate", "badge", "icon", "loot_item", "currency", "border", "shard", "rarity" };
                    string[] lootFolders = { "assets/loot_item_icons", "assets/tray_icons", "assets/category_icons", "assets/tooltips", "assets/border_images", "assets/rarity_icons", "assets/reveal_redeem/rarity", "assets/disenchant_modal", "assets/mass_disenchant" };

                    for (int i = 0; i <= 500; i++)
                    {
                        foreach (var folder in lootFolders)
                        foreach (var type in lootTypes)
                        {
                            TryPluginCandidate($"plugins/rcp-fe-lol-loot/global/default/{folder}/{type}_{i}.png");
                            TryPluginCandidate($"plugins/rcp-fe-lol-loot/global/default/{folder}/{type}_{i}_splash.png");
                            TryPluginCandidate($"plugins/rcp-fe-lol-loot/global/default/{folder}/{type}{i}.png");
                            for (int j = 1; j <= 10; j++)
                            {
                                TryPluginCandidate($"plugins/rcp-fe-lol-loot/global/default/{folder}/{type}_{i}_{j}.png");
                                TryPluginCandidate($"plugins/rcp-fe-lol-loot/global/default/{folder}/{type}_{i}_{j}_splash.png");
                            }
                        }
                    }

                    string[] videoNames = { "open_capsule", "open_chest", "open_orb", "open_honor_capsule", "loot_reroll", "small_rental", "large_rental", "portal_open" };
                    string[] videoSub = { "intro", "loop", "outro", "in", "out", "image" };
                    foreach (var vn in videoNames)
                    foreach (var vs in videoSub)
                    {
                        TryPluginCandidate($"plugins/rcp-fe-lol-loot/global/default/assets/videos/{vn}_{vs}.webm");
                        TryPluginCandidate($"plugins/rcp-fe-lol-loot/global/default/assets/videos/low_spec_images/{vn}_{vs}.png");
                        TryPluginCandidate($"plugins/rcp-fe-lol-loot/global/default/assets/reroll_crafter/{vn}_{vs}.webm");
                    }
                }

                if (targetPlugin == "rcp-fe-lol-store")
                {
                    string[] storeFolders = { "storefront/addon/public/img", "storefront/addon/public/img/sprite-source", "storefront/addon/public/img/composites", "storefront/addon/public/img/content/gift", "storefront/addon/public/img/content/transfer", "storefront/addon/public/img/content/rune_pages", "storefront/addon/public/img/csslib" };
                    string[] storeNames = { "gift", "g-skin", "g-champion", "g-wardskin", "g-mc", "g-icon", "g-chest", "g-pass", "g-bundle", "g-tft", "close", "x-icon", "up-arrow", "down-arrow", "sort-up-arrow", "sort-down-arrow", "sale", "error", "bg-modal", "bg-chroma-card", "hextechmagicbg" };
                    string[] modifiers = { "sm", "lg", "hover", "active", "disabled", "pressed", "selected", "default", "icon", "bg" };

                    foreach (var folder in storeFolders)
                    foreach (var name in storeNames)
                    {
                        TryPluginCandidate($"plugins/rcp-fe-lol-store/global/default/{folder}/{name}.png");
                        TryPluginCandidate($"plugins/rcp-fe-lol-store/global/default/{folder}/{name}.jpg");
                        TryPluginCandidate($"plugins/rcp-fe-lol-store/global/default/{folder}/{name}.svg");
                        foreach (var m in modifiers)
                        {
                            TryPluginCandidate($"plugins/rcp-fe-lol-store/global/default/{folder}/{name}_{m}.png");
                            TryPluginCandidate($"plugins/rcp-fe-lol-store/global/default/{folder}/{name}_{m}.jpg");
                            TryPluginCandidate($"plugins/rcp-fe-lol-store/global/default/{folder}/{name}-{m}.png");
                            TryPluginCandidate($"plugins/rcp-fe-lol-store/global/default/{folder}/{name}-{m}.jpg");
                        }
                    }
                }

                // Done inspecting
            }

            if (solvedPaths.Count > 0)
            {
                Console.WriteLine($"\n==================================================");
                Console.WriteLine($"  TOTAL CRACKED PATHS: {solvedPaths.Count}");
                Console.WriteLine($"==================================================");
                foreach (var p in solvedPaths)
                {
                    Console.WriteLine($"  0x{p.Key:x16} {p.Value}");
                }

                string crackedOutPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AssetsManager", "hash_lab", "cracked_lcu.txt");
                File.WriteAllLines(crackedOutPath, solvedPaths.Select(p => $"{p.Key:x16} {p.Value}"));
                Console.WriteLine($"\n[SUCCESS] Saved {solvedPaths.Count} cracked hashes to: {crackedOutPath}");

                // Auto-append to hashes.lcu.txt if not already present
                if (File.Exists(hashesLcuPath))
                {
                    var existingLines = new HashSet<string>(File.ReadLines(hashesLcuPath).Select(l => l.Trim()), StringComparer.OrdinalIgnoreCase);
                    var toAppend = new List<string>();
                    foreach (var pair in solvedPaths)
                    {
                        string lineWithHash = $"{pair.Key:x16} {pair.Value}";
                        string linePlain = pair.Value;
                        if (!existingLines.Contains(lineWithHash) && !existingLines.Contains(linePlain))
                        {
                            toAppend.Add(lineWithHash);
                        }
                    }

                    if (toAppend.Count > 0)
                    {
                        File.AppendAllLines(hashesLcuPath, toAppend);
                        Console.WriteLine($"[SUCCESS] Appended {toAppend.Count} new entries to: {hashesLcuPath}");
                    }
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
