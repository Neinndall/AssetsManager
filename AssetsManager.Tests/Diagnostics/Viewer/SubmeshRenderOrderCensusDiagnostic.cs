using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetsManager.Tests.Support;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `submesh-render-order-census`: every installed champion and map skin BIN whose skinMeshProperties author a
    /// submeshRenderOrder, with the names it lists, to pick the skins the viewer's authored draw order reorders.
    /// </summary>
    internal static class SubmeshRenderOrderCensusDiagnostic
    {
        public static void Run(string[] args)
        {
            string hashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
            var binPaths = new Dictionary<ulong, string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.game.txt")))
            {
                int space = line.IndexOf(' ');
                if (space > 0 && line.Contains("/skins/skin", StringComparison.Ordinal) && line.EndsWith(".bin", StringComparison.Ordinal) &&
                    ulong.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    binPaths[hash] = line[(space + 1)..];
            }

            uint skinClass = Fnv1a.HashLower("SkinCharacterDataProperties");
            uint meshField = Fnv1a.HashLower("skinMeshProperties");
            uint orderField = Fnv1a.HashLower("submeshRenderOrder");
            int skins = 0;
            var withOrder = new List<string>();
            string final = Path.Combine(InstalledSkins.FindInstall(), @"Game\DATA\FINAL");
            foreach (string wadPath in Directory.GetFiles(final, "*.wad.client", SearchOption.AllDirectories)
                         .Where(path => path.Contains(@"\Champions\") || path.Contains(@"\Maps\"))
                         .Where(path => !Path.GetFileName(path)[..^".wad.client".Length].Contains('.')))
            {
                using var wad = new WadFile(wadPath);
                foreach (ulong chunk in wad.Chunks.Keys.Where(binPaths.ContainsKey))
                {
                    BinTree tree;
                    try
                    {
                        using var data = wad.LoadChunkDecompressed(chunk);
                        using var stream = new MemoryStream(data.Span.ToArray(), writable: false);
                        tree = new BinTree(stream);
                    }
                    catch { continue; }

                    foreach (BinTreeObject skin in tree.Objects.Values.Where(item => item.ClassHash == skinClass))
                    {
                        skins++;
                        if (skin.Properties.TryGetValue(meshField, out BinTreeProperty mesh) && mesh is BinTreeStruct meshStruct &&
                            meshStruct.Properties.TryGetValue(orderField, out BinTreeProperty order) && order is BinTreeString text)
                            withOrder.Add($"{binPaths[chunk]} | {text.Value}");
                    }
                }
            }

            Console.WriteLine($"[RenderOrder] skins={skins} withSubmeshRenderOrder={withOrder.Count}");
            foreach (string line in withOrder)
                Console.WriteLine($"[RenderOrder] {line}");
        }
    }
}
