using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class LcuExportUnknownsDiagnostic
    {
        public static void Run(string[] args)
        {
            string pbeRoot = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                ?? @"C:\Riot Games\League of Legends";
            string outDir = args.FirstOrDefault(arg => arg.StartsWith("--out=", StringComparison.Ordinal))
                ?[6..] ?? Path.Combine(Path.GetTempPath(), "opencode", "lcu_unknowns");

            string unknownsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AssetsManager", "hash_lab", "unknowns.lcu.txt");
            if (!File.Exists(unknownsPath)) { Console.WriteLine("unknowns.lcu.txt not found."); return; }

            var unknownHashes = new HashSet<ulong>();
            foreach (string line in File.ReadLines(unknownsPath))
                if (ulong.TryParse(line.Trim(), System.Globalization.NumberStyles.HexNumber, null, out ulong hash))
                    unknownHashes.Add(hash);

            string pluginsDir = Directory.Exists(Path.Combine(pbeRoot, "Plugins"))
                ? Path.Combine(pbeRoot, "Plugins")
                : pbeRoot;
            var wads = Directory.EnumerateFiles(pluginsDir, "*.wad", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".wad", StringComparison.OrdinalIgnoreCase)).ToList();

            Directory.CreateDirectory(outDir);
            Console.WriteLine($"Exporting unknown chunks to {outDir}...");

            int exported = 0;
            foreach (string wadPath in wads)
            {
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        if (!unknownHashes.Contains(pair.Key)) continue;
                        try
                        {
                            using var owner = wad.LoadChunkDecompressed(pair.Value);
                            ArraySegment<byte> seg = owner.DangerousGetArray();
                            byte[] data = seg.Array[seg.Offset..(seg.Offset + seg.Count)];
                            string ext = AssetsManager.Utils.FileTypeDetector.GuessExtension(data);
                            if (string.IsNullOrEmpty(ext)) ext = "bin";
                            string target = Path.Combine(outDir, $"{pair.Key:x16}.{ext}");
                            File.WriteAllBytes(target, data);
                            exported++;
                        }
                        catch { }
                    }
                }
                catch { }
            }
            Console.WriteLine($"Exported {exported} files.");
        }
    }
}
