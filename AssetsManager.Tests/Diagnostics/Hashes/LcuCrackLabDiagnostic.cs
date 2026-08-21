using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class LcuCrackLabDiagnostic
    {
        private static readonly Regex LottieNameRegex = new("\"nm\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);

        public static void Run(string[] args)
        {
            string pbeRoot = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                ?? @"C:\Riot Games\League of Legends";
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string unknownsPath = Path.Combine(localAppData, "AssetsManager", "hash_lab", "unknowns.lcu.txt");
            string catalogPath = Path.Combine(localAppData, "AssetsManager", "hashes", "hashes.lcu.txt");

            var targets = new HashSet<ulong>();
            foreach (string line in File.ReadLines(unknownsPath))
                if (ulong.TryParse(line.Trim(), System.Globalization.NumberStyles.HexNumber, null, out ulong hash))
                    targets.Add(hash);

            var solved = new Dictionary<ulong, string>();
            var stats = new List<(string Technique, int Candidates, int Cracked)>();

            string pluginsDir = Directory.Exists(Path.Combine(pbeRoot, "Plugins"))
                ? Path.Combine(pbeRoot, "Plugins")
                : pbeRoot;
            var wads = Directory.EnumerateFiles(pluginsDir, "*.wad", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".wad", StringComparison.OrdinalIgnoreCase)).ToList();

            var lottieDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadLines(catalogPath))
            {
                int space = line.IndexOf(' ');
                if (space < 1) continue;
                string path = line[(space + 1)..];
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                int idx = path.LastIndexOf('/');
                if (idx > 0 && path.Contains("lottie", StringComparison.OrdinalIgnoreCase))
                    lottieDirs.Add(path[..idx]);
            }
            Console.WriteLine($"Known lottie directories: {lottieDirs.Count}");

            var namePayloads = new List<(ulong Hash, string Name, List<string> AssetNames, string Plugin)>();
            foreach (string wadPath in wads)
            {
                string plugin = ExtractPluginName(wadPath);
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        if (!targets.Contains(pair.Key) || pair.Value.UncompressedSize is < 16 or > 4_000_000)
                            continue;
                        try
                        {
                            using var owner = wad.LoadChunkDecompressed(pair.Value);
                            ArraySegment<byte> seg = owner.DangerousGetArray();
                            byte[] data = seg.Array[seg.Offset..(seg.Offset + seg.Count)];
                            if (data[0] != (byte)'{' || data[^1] != (byte)'}') continue;
                            string text = System.Text.Encoding.UTF8.GetString(data);
                            Match match = LottieNameRegex.Match(text);
                            if (!match.Success) continue;

                            var assetNames = Regex.Matches(text, "\"p\"\\s*:\\s*\"([^\"]+\\.(?:png|jpg|webm))\"")
                                .Cast<Match>().Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToList();
                            namePayloads.Add((pair.Key, match.Groups[1].Value, assetNames, plugin));
                        }
                        catch { }
                    }
                }
                catch { }
            }
            Console.WriteLine($"Self-named lottie payloads found: {namePayloads.Count}");
            foreach (var item in namePayloads)
                Console.WriteLine($"  {item.Hash:x16} nm=\"{item.Name}\" plugin={item.Plugin} assets=[{string.Join(", ", item.AssetNames.Take(4))}]");

            var pluginDirs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadLines(catalogPath))
            {
                int space = line.IndexOf(' ');
                if (space < 1) continue;
                string path = line[(space + 1)..];
                if (!path.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)) continue;
                int pluginEnd = path.IndexOf('/', "plugins/".Length);
                if (pluginEnd < 0) continue;
                string plugin = path["plugins/".Length..pluginEnd];
                int lastSlash = path.LastIndexOf('/');
                if (lastSlash <= pluginEnd) continue;
                if (!pluginDirs.TryGetValue(plugin, out var set))
                    pluginDirs[plugin] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(path[..lastSlash]);
            }

            int candidates = 0;
            foreach (var item in namePayloads)
            {
                var dirs = new HashSet<string>(lottieDirs, StringComparer.OrdinalIgnoreCase);
                if (pluginDirs.TryGetValue(item.Plugin, out var ownDirs))
                    dirs.UnionWith(ownDirs);

                foreach (string dir in dirs)
                foreach (string variant in BuildDirVariants(dir))
                foreach (string nameVariant in BuildNameVariants(item.Name))
                {
                    string candidate = $"{variant}/{nameVariant}.json".ToLowerInvariant();
                    candidates++;
                    TryCrack(candidate, targets, solved);
                }

                foreach (string asset in item.AssetNames)
                foreach (string dir in dirs)
                foreach (string variant in BuildDirVariants(dir))
                {
                    string candidate = $"{variant}/{asset}".ToLowerInvariant();
                    candidates++;
                    TryCrack(candidate, targets, solved);
                }
            }
            stats.Add(("Lottie self-name x known dirs", candidates, solved.Count));

            Console.WriteLine("\nTechnique B: literal name references inside plugin chunks (no size ceiling)...");
            var referenceContexts = new List<string>();
            foreach (string wadPath in wads)
            {
                string plugin = ExtractPluginName(wadPath);
                if (!plugin.Equals("rcp-fe-lol-clash", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        if (pair.Value.UncompressedSize < 64) continue;
                        byte[] data;
                        try
                        {
                            using var owner = wad.LoadChunkDecompressed(pair.Value);
                            ArraySegment<byte> seg = owner.DangerousGetArray();
                            data = seg.Array[seg.Offset..(seg.Offset + seg.Count)];
                        }
                        catch { continue; }

                        bool printable = data.Take(Math.Min(data.Length, 256)).Count(b => b == 0x09 || b == 0x0A || b == 0x0D || (b >= 0x20 && b <= 0x7E))
                            > Math.Min(data.Length, 256) * 0.7;
                        if (!printable) continue;

                        string text = System.Text.Encoding.UTF8.GetString(data);
                        foreach (string needle in new[] { "opponentFound", "buttonSheen" })
                        {
                            foreach (Match match in Regex.Matches(text, Regex.Escape(needle)))
                            {
                                int start = Math.Max(0, match.Index - 400);
                                int length = Math.Min(text.Length - start, 800);
                                string context = text[start..(start + length)].Replace("\n", " ").Replace("\r", "");
                                referenceContexts.Add($"[{pair.Value.UncompressedSize}B @{match.Index}] {context}");
                            }
                        }
                    }
                }
                catch { }
            }

            Console.WriteLine($"Reference contexts found: {referenceContexts.Count}");
            foreach (string context in referenceContexts.Distinct().Take(20))
                Console.WriteLine($"  {context}");

            Console.WriteLine("\nTechnique C: {lottie-dir}/{anim-name}/data.json construction...");
            var lottieBaseDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string wadPath in wads)
            {
                string plugin = ExtractPluginName(wadPath);
                if (!plugin.Equals("rcp-fe-lol-clash", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        if (pair.Value.UncompressedSize < 64) continue;
                        byte[] data;
                        try
                        {
                            using var owner = wad.LoadChunkDecompressed(pair.Value);
                            ArraySegment<byte> seg = owner.DangerousGetArray();
                            data = seg.Array[seg.Offset..(seg.Offset + seg.Count)];
                        }
                        catch { continue; }

                        bool printable = data.Take(Math.Min(data.Length, 256)).Count(b => b == 0x09 || b == 0x0A || b == 0x0D || (b >= 0x20 && b <= 0x7E))
                            > Math.Min(data.Length, 256) * 0.7;
                        if (!printable) continue;

                        string text = System.Text.Encoding.UTF8.GetString(data);
                        foreach (Match match in Regex.Matches(text, "[\"'`]([^\"'`]*assets/animations/lottie[^\"'`]*)[\"'`]"))
                        {
                            string literal = match.Groups[1].Value.ToLowerInvariant();
                            int clashIdx = literal.IndexOf("lol-clash", StringComparison.Ordinal);
                            if (clashIdx < 0) continue;
                            string suffix = literal[(clashIdx + "lol-clash".Length)..].TrimEnd('/');
                            if (suffix.Length == 0) continue;
                            if (suffix.EndsWith("/images", StringComparison.OrdinalIgnoreCase))
                                suffix = suffix[..^"/images".Length];
                            lottieBaseDirs.Add("plugins/rcp-fe-lol-clash/global/default" + suffix);
                        }
                    }
                }
                catch { }
            }
            Console.WriteLine($"Lottie base dirs harvested from JS: {lottieBaseDirs.Count}");
            foreach (string dir in lottieBaseDirs.Take(15))
                Console.WriteLine($"  {dir}");

            candidates = 0;
            var animNames = namePayloads.Select(item => item.Name.ToLowerInvariant()).Distinct().ToList();
            var assetPairs = namePayloads.SelectMany(item => item.AssetNames.Select(asset => (item.Name.ToLowerInvariant(), asset))).Distinct().ToList();
            foreach (string dir in lottieBaseDirs)
            foreach (string name in animNames)
            {
                string candidate = $"{dir}/{name}/data.json".ToLowerInvariant();
                candidates++;
                TryCrack(candidate, targets, solved);

                foreach (var (_, asset) in assetPairs.Where(pair => pair.Item1 == name))
                {
                    string imageCandidate = $"{dir}/{name}/images/{asset}".ToLowerInvariant();
                    candidates++;
                    TryCrack(imageCandidate, targets, solved);
                }
            }
            stats.Add(("JS-harvested dirs x name/data.json", candidates, solved.Count));

            Console.WriteLine("\nTechnique D: every *.json literal in clash chunks x lottie dirs...");
            var jsonLiterals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string wadPath in wads)
            {
                string plugin = ExtractPluginName(wadPath);
                if (!plugin.Equals("rcp-fe-lol-clash", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        if (pair.Value.UncompressedSize < 64) continue;
                        byte[] data;
                        try
                        {
                            using var owner = wad.LoadChunkDecompressed(pair.Value);
                            ArraySegment<byte> seg = owner.DangerousGetArray();
                            data = seg.Array[seg.Offset..(seg.Offset + seg.Count)];
                        }
                        catch { continue; }

                        bool printable = data.Take(Math.Min(data.Length, 256)).Count(b => b == 0x09 || b == 0x0A || b == 0x0D || (b >= 0x20 && b <= 0x7E))
                            > Math.Min(data.Length, 256) * 0.7;
                        if (!printable) continue;

                        string text = System.Text.Encoding.UTF8.GetString(data);
                        foreach (Match match in Regex.Matches(text, "[a-z0-9_\\-]{3,60}\\.json", RegexOptions.IgnoreCase))
                            jsonLiterals.Add(match.Value.ToLowerInvariant());
                    }
                }
                catch { }
            }
            Console.WriteLine($"Distinct .json literals: {jsonLiterals.Count}");

            candidates = 0;
            var baseDirs = new HashSet<string>(lottieBaseDirs, StringComparer.OrdinalIgnoreCase);
            baseDirs.Add("plugins/rcp-fe-lol-clash/global/default/assets/animations/lottie");
            foreach (string dir in baseDirs.ToList())
                foreach (string variant in BuildDirVariants(dir))
                    baseDirs.Add(variant);

            foreach (string literal in jsonLiterals)
            foreach (string dir in baseDirs)
            {
                string candidate = $"{dir}/{literal}".ToLowerInvariant();
                candidates++;
                TryCrack(candidate, targets, solved);
            }
            stats.Add(("Clash .json literals x lottie dirs", candidates, solved.Count));

            Console.WriteLine("\nTechnique E: global filename x plugin-directory harvest...");
            var pluginDirVocab = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadLines(catalogPath))
            {
                int space = line.IndexOf(' ');
                if (space < 1) continue;
                string path = line[(space + 1)..];
                if (!path.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)) continue;
                int pluginEnd = path.IndexOf('/', "plugins/".Length);
                if (pluginEnd < 0) continue;
                string plugin = path["plugins/".Length..pluginEnd];
                int lastSlash = path.LastIndexOf('/');
                if (lastSlash <= pluginEnd) continue;
                if (!pluginDirVocab.TryGetValue(plugin, out var set))
                    pluginDirVocab[plugin] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(path[..lastSlash].ToLowerInvariant());
            }

            var filenameVocab = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string wadPath in wads)
            {
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        if (pair.Value.UncompressedSize < 64 || pair.Value.UncompressedSize > 8_000_000) continue;
                        byte[] data;
                        try
                        {
                            using var owner = wad.LoadChunkDecompressed(pair.Value);
                            ArraySegment<byte> seg = owner.DangerousGetArray();
                            data = seg.Array[seg.Offset..(seg.Offset + seg.Count)];
                        }
                        catch { continue; }

                        bool printable = data.Take(Math.Min(data.Length, 256)).Count(b => b == 0x09 || b == 0x0A || b == 0x0D || (b >= 0x20 && b <= 0x7E))
                            > Math.Min(data.Length, 256) * 0.7;
                        if (!printable) continue;

                        string text = System.Text.Encoding.UTF8.GetString(data);
                        foreach (Match match in Regex.Matches(text, "[a-z0-9_][a-z0-9_\\-.]{2,60}\\.(?:png|jpg|jpeg|svg|webm|ogg|mp3|gif|json)", RegexOptions.IgnoreCase))
                            filenameVocab.Add(match.Value.ToLowerInvariant());
                    }
                }
                catch { }
            }
            Console.WriteLine($"Distinct filename literals across ALL plugins: {filenameVocab.Count}");

            candidates = 0;
            foreach (string fileName in filenameVocab)
            foreach (var pluginDirsEntry in pluginDirVocab)
            foreach (string dir in pluginDirsEntry.Value)
            {
                string candidate = $"{dir}/{fileName}".ToLowerInvariant();
                candidates++;
                TryCrack(candidate, targets, solved);
            }
            stats.Add(("Global filename x plugin dirs", candidates, solved.Count));

            Console.WriteLine("\nTechnique F: numeric template expansion beyond known maximums...");
            var templates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadLines(catalogPath))
            {
                int space = line.IndexOf(' ');
                if (space < 1) continue;
                string path = line[(space + 1)..].ToLowerInvariant();
                Match match = Regex.Match(path, @"^(.*/)([a-z0-9_\-]*?)(\d{1,5})\.(png|jpg|svg|webm|ogg|json)$");
                if (!match.Success) continue;
                string key = $"{match.Groups[1].Value}|{match.Groups[2].Value}|{match.Groups[4].Value}";
                int number = int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (!templates.TryGetValue(key, out int max) || number > max)
                    templates[key] = number;
            }
            Console.WriteLine($"Numeric templates found in catalog: {templates.Count}");

            candidates = 0;
            foreach (var entry in templates)
            {
                string[] parts = entry.Key.Split('|');
                string directory = parts[0];
                string stem = parts[1];
                string extension = parts[2];
                for (int number = entry.Value + 1; number <= entry.Value + 1500; number++)
                {
                    string candidate = $"{directory}{stem}{number}.{extension}";
                    candidates++;
                    TryCrack(candidate, targets, solved);
                }
            }
            stats.Add(("Numeric template expansion (+1500)", candidates, solved.Count));

            Console.WriteLine("\nTechnique G: CDTB-exact blanket numeric sweep (0..9999 on every format)...");
            var allFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadLines(catalogPath))
            {
                int space = line.IndexOf(' ');
                if (space < 1) continue;
                string path = line[(space + 1)..].ToLowerInvariant();
                Match match = Regex.Match(path, @"^(.*?)(\d{1,5})(\.(?:png|jpg|svg|webm|ogg|json))$");
                if (!match.Success) continue;
                allFormats.Add($"{match.Groups[1].Value}%s{match.Groups[3].Value}");
            }
            Console.WriteLine($"Formats for blanket sweep: {allFormats.Count}");

            candidates = 0;
            foreach (string format in allFormats)
            {
                string directory = format.Contains('/') ? format[..(format.LastIndexOf('/') + 1)] : string.Empty;
                string localFormat = format[(directory.Length)..];
                for (int number = 0; number < 10000; number++)
                {
                    string candidate = $"{directory}{localFormat.Replace("%s", number.ToString())}";
                    candidates++;
                    TryCrack(candidate, targets, solved);
                }
            }
            stats.Add(("CDTB blanket numbers 0..9999", candidates, solved.Count));

            Console.WriteLine();
            Console.WriteLine("==================================================");
            Console.WriteLine($"    RESULT: {solved.Count} cracked");
            Console.WriteLine("==================================================");
            foreach (var row in stats)
                Console.WriteLine($"  {row.Technique,-40} {row.Candidates,10:N0} cand -> {row.Cracked}");
            foreach (var pair in solved.OrderBy(item => item.Key))
                Console.WriteLine($"  [CRACKED] {pair.Key:x16} = {pair.Value}");
        }

        private static string ExtractPluginName(string wadPath)
        {
            string[] segments = wadPath.Replace('\\', '/').Split('/');
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (segments[i].Equals("Plugins", StringComparison.OrdinalIgnoreCase))
                    return segments[i + 1];
            }
            return Path.GetFileNameWithoutExtension(wadPath);
        }

        private static IEnumerable<string> BuildDirVariants(string dir)
        {
            yield return dir;
            yield return dir + "/images";
            if (dir.Contains("/default/", StringComparison.OrdinalIgnoreCase))
                yield return dir.Replace("/default/", "/default/images/", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> BuildNameVariants(string name)
        {
            yield return name;
            string kebab = Regex.Replace(name, "(?<!^)([A-Z])", "-$1").ToLowerInvariant();
            yield return kebab;
            yield return kebab.Replace('-', '_');
        }

        private static void TryCrack(string candidate, HashSet<ulong> targets, Dictionary<ulong, string> solved)
        {
            ulong hash = XxHash64Ext.Hash(candidate);
            if (targets.Contains(hash) && !solved.ContainsKey(hash))
            {
                solved[hash] = candidate;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  [CRACKED] {hash:x16} = {candidate}");
                Console.ResetColor();
            }
        }
    }
}
