using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetsManager.Tests.Support;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `vfx-linked-bin-census`: walks the linked BINs of every installed champion skin breadth first, as 3D Studio does,
    /// and reports how many files each skin reaches and how many VFX systems sit beyond the 32-file cap.
    /// </summary>
    internal static class VfxLinkedBinCensusDiagnostic
    {
        private const int Cap = 32;
        private static readonly uint SystemClass = Fnv1a.HashLower("VfxSystemDefinitionData");

        public static void Run(string[] args)
        {
            string final = Path.Combine(InstalledSkins.FindInstall(), @"Game\DATA\FINAL");
            var wadOf = new Dictionary<ulong, string>();
            foreach (string wadPath in Directory.GetFiles(final, "*.wad.client", SearchOption.AllDirectories)
                         .Where(path => !Path.GetFileName(path)[..^".wad.client".Length].Contains('.')))
            {
                using var wad = new WadFile(wadPath);
                foreach (ulong chunk in wad.Chunks.Keys) wadOf.TryAdd(chunk, wadPath);
            }
            string hashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
            var skins = File.ReadLines(Path.Combine(hashDir, "hashes.game.txt"))
                .Select(line => line[(line.IndexOf(' ') + 1)..])
                .Where(path => path.StartsWith("data/characters/", StringComparison.Ordinal) && path.Contains("/skins/skin", StringComparison.Ordinal) &&
                               path.EndsWith(".bin", StringComparison.Ordinal) && wadOf.ContainsKey(XxHash64Ext.Hash(path)))
                .ToList();

            var wads = new Dictionary<string, WadFile>();
            var cache = new Dictionary<ulong, BinTree>();
            BinTree Load(string path)
            {
                ulong hash = XxHash64Ext.Hash(path.ToLowerInvariant());
                if (cache.TryGetValue(hash, out BinTree cached)) return cached;
                BinTree tree = null;
                if (wadOf.TryGetValue(hash, out string wadPath))
                {
                    if (!wads.TryGetValue(wadPath, out WadFile wad)) wads[wadPath] = wad = new WadFile(wadPath);
                    try
                    {
                        using var data = wad.LoadChunkDecompressed(hash);
                        using var stream = new MemoryStream(data.Span.ToArray(), writable: false);
                        tree = new BinTree(stream);
                    }
                    catch { tree = null; }
                }
                cache[hash] = tree;
                return tree;
            }

            int over = 0, systemsBeyond = 0, maxReach = 0;
            var examples = new List<string>();
            foreach (string skin in skins)
            {
                BinTree primary = Load(skin);
                if (primary == null) continue;
                var queue = new Queue<string>(primary.Dependencies);
                var seen = new HashSet<string>(primary.Dependencies, StringComparer.OrdinalIgnoreCase);
                int reached = 0, beyond = 0;
                while (queue.Count > 0)
                {
                    string next = queue.Dequeue();
                    BinTree tree = Load(next);
                    reached++;
                    if (tree == null) continue;
                    if (reached > Cap)
                        beyond += tree.Objects.Values.Count(item => item.ClassHash == SystemClass);
                    foreach (string dependency in tree.Dependencies)
                        if (seen.Add(dependency)) queue.Enqueue(dependency);
                }
                maxReach = Math.Max(maxReach, reached);
                if (reached <= Cap) continue;
                over++;
                systemsBeyond += beyond;
                if (examples.Count < 12) examples.Add($"{skin} reaches={reached} systemsBeyondCap={beyond}");
            }
            foreach (WadFile wad in wads.Values) wad.Dispose();

            Console.WriteLine($"[LinkedBins] skins={skins.Count} over {Cap} linked files={over} max reach={maxReach} VFX systems beyond the cap={systemsBeyond}");
            foreach (string example in examples) Console.WriteLine($"[LinkedBins]   {example}");
        }
    }
}
