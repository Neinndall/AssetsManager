using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BenchmarkApp.Diagnostics.Hashes
{
    internal static class LcuWadContentProbe
    {
        public static void Run(string pbeRoot, string filter)
        {
            string hashesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AssetsManager", "hashes");
            var lcuPaths = LoadPaths(Path.Combine(hashesPath, "hashes.lcu.txt"));
            var gamePaths = LoadPaths(Path.Combine(hashesPath, "hashes.game.txt"));
            Console.WriteLine($"LCU paths: {lcuPaths.Count}, GAME paths: {gamePaths.Count}");

            string pluginsDir = Path.Combine(pbeRoot, "Plugins");
            string[] wads = Directory.Exists(pluginsDir)
                ? Directory.EnumerateFiles(pluginsDir, "*.wad", SearchOption.AllDirectories)
                    .Where(path => path.EndsWith(".wad", StringComparison.OrdinalIgnoreCase))
                    .Where(path => string.IsNullOrWhiteSpace(filter) ||
                        path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<string>();

            Console.WriteLine($"Probing {wads.Length} LCU WADs\n");
            foreach (string wadPath in wads)
            {
                try
                {
                    using var wad = new LeagueToolkit.Core.Wad.WadFile(wadPath);
                    var sigs = new Dictionary<string, int>(StringComparer.Ordinal);
                    var pathKinds = new Dictionary<string, int>(StringComparer.Ordinal);
                    int readable = 0, total = 0;
                    foreach (var pair in wad.Chunks)
                    {
                        total++;
                        string sig = GetChunkSignature(wad, pair.Value);
                        if (sig.Length > 0) { readable++; sigs[sig] = sigs.GetValueOrDefault(sig) + 1; }
                        string path = lcuPaths.TryGetValue(pair.Key, out string lcu)
                            ? lcu
                            : gamePaths.TryGetValue(pair.Key, out string game) ? game : null;
                        if (path != null)
                        {
                            string kind = path.Contains(".bin", StringComparison.OrdinalIgnoreCase)
                                ? "BIN-path"
                                : path.Contains(".stringtable", StringComparison.OrdinalIgnoreCase)
                                    ? "RST-path"
                                    : Path.GetExtension(path);
                            pathKinds[kind] = pathKinds.GetValueOrDefault(kind) + 1;
                        }
                        else
                        {
                            pathKinds["[unresolved]"] = pathKinds.GetValueOrDefault("[unresolved]") + 1;
                        }
                    }
                    Console.WriteLine($"{Path.GetFileName(wadPath)}: chunks={total} readable-sig={readable}");
                    Console.WriteLine($"   sigs: {string.Join(", ", sigs.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{FormatSig(kv.Key)}={kv.Value}"))}");
                    Console.WriteLine($"   paths: {string.Join(", ", pathKinds.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key}={kv.Value}"))}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{Path.GetFileName(wadPath)}: ERROR {ex.Message}");
                }
            }
        }

        private static string FormatSig(string sig)
        {
            var builder = new StringBuilder();
            foreach (char c in sig)
                builder.Append(char.IsLetterOrDigit(c) ? c : '.');
            return builder.ToString();
        }

        private static Dictionary<ulong, string> LoadPaths(string path)
        {
            var result = new Dictionary<ulong, string>();
            if (!File.Exists(path)) return result;
            foreach (string line in File.ReadLines(path))
            {
                if (line.Length <= 16 || line[16] != ' ') continue;
                if (ulong.TryParse(line.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    result.TryAdd(hash, line[17..]);
            }
            return result;
        }

        private static string GetChunkSignature(LeagueToolkit.Core.Wad.WadFile wad, LeagueToolkit.Core.Wad.WadChunk chunk)
        {
            try
            {
                using Stream stream = wad.OpenChunk(chunk);
                byte[] buffer = new byte[4];
                int read = stream.Read(buffer, 0, 4);
                if (read < 3) return string.Empty;
                if (read == 3) return Encoding.ASCII.GetString(buffer, 0, 3);
                return Encoding.ASCII.GetString(buffer);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
