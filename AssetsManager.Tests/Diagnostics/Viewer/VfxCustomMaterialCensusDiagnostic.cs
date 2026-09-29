using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `vfx-custom-material-census`: for every VFX emitter of the installed champion and map BINs that names a custom
    /// material, whether the BIN declaring the system declares the material too, whether one of the BINs it links
    /// (breadth first, 32 files at most) does, or neither.
    /// </summary>
    internal static class VfxCustomMaterialCensusDiagnostic
    {
        private const int LinkedCap = 32;

        public static void Run(string[] args)
        {
            string install = InstalledSkins.FindInstall();
            string hashes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes", "hashes.game.txt");
            var paths = new Dictionary<ulong, string>();
            foreach (string line in File.ReadLines(hashes))
            {
                int space = line.IndexOf(' ');
                if (space > 0 && line.EndsWith(".bin", StringComparison.Ordinal) &&
                    ulong.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    paths[hash] = line[(space + 1)..];
            }

            // Every BIN chunk of the champion, map and common WADs, loaded on demand.
            string final = Path.Combine(install, @"Game\DATA\FINAL");
            var wads = new List<WadFile>();
            var where = new Dictionary<ulong, WadFile>();
            foreach (string wadPath in Directory.GetFiles(final, "*.wad.client", SearchOption.AllDirectories)
                         .Where(path => path.Contains(@"\Champions\") || path.Contains(@"\Maps\") || Path.GetFileName(path).StartsWith("Common", StringComparison.OrdinalIgnoreCase))
                         .Where(path => !Path.GetFileName(path)[..^".wad.client".Length].Contains('.')))
            {
                var wad = new WadFile(wadPath);
                wads.Add(wad);
                foreach (ulong hash in wad.Chunks.Keys)
                    if (paths.ContainsKey(hash)) where.TryAdd(hash, wad);
            }

            var trees = new Dictionary<ulong, BinTree>();
            BinTree Tree(ulong hash)
            {
                if (trees.TryGetValue(hash, out BinTree cached)) return cached;
                BinTree tree = null;
                if (where.TryGetValue(hash, out WadFile wad))
                {
                    try
                    {
                        using var data = wad.LoadChunkDecompressed(hash);
                        using var stream = new MemoryStream(data.Span.ToArray(), writable: false);
                        tree = new BinTree(stream);
                    }
                    catch { }
                }
                return trees[hash] = tree;
            }

            int emitters = 0, own = 0, linked = 0, nowhere = 0, fixedByLinked = 0, noProgram = 0, recoveredByShaders = 0;
            var recoveredExamples = new List<string>();
            BinTree shaders = Tree(XxHash64Ext.Hash("data/shaders/shaders.bin"));
            var linkedExamples = new List<string>();
            var nowhereExamples = new List<string>();
            var linkedByKind = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach ((ulong hash, string path) in paths.Where(pair => where.ContainsKey(pair.Key) &&
                         (System.Text.RegularExpressions.Regex.IsMatch(pair.Value, @"^data/characters/[^/]+/skins/skin\d+\.bin$") ||
                          pair.Value.EndsWith(".materials.bin", StringComparison.Ordinal))))
            {
                BinTree tree = Tree(hash);
                if (tree == null) continue;
                IReadOnlyDictionary<uint, VfxSystemDefinition> systems;
                try { systems = VfxSystemParser.ExtractAll(tree); }
                catch { continue; }

                foreach ((uint systemHash, VfxSystemDefinition system) in systems)
                {
                    foreach (VfxEmitterDefinition emitter in system.Emitters ?? Array.Empty<VfxEmitterDefinition>())
                    {
                        if (emitter.CustomMaterialPathHash == 0) continue;
                        emitters++;
                        if (tree.Objects.ContainsKey(emitter.CustomMaterialPathHash))
                        {
                            own++;
                            // Whether the material finds its program without, and with, the global shader BIN.
                            VfxSystemDefinition single = system with { Emitters = new[] { emitter } };
                            bool bare = VfxGraphParser.ResolveCustomMaterials(single, tree).Emitters[0].CustomMaterial?.Program != null;
                            bool withShaders = bare || VfxGraphParser.ResolveCustomMaterials(single, tree, shaderTrees: shaders == null ? null : new[] { shaders })
                                .Emitters[0].CustomMaterial?.Program != null;
                            if (!bare) noProgram++;
                            if (!bare && withShaders)
                            {
                                recoveredByShaders++;
                                if (recoveredExamples.Count < 8) recoveredExamples.Add($"{path} {system.Name}/{emitter.Name}");
                            }
                            continue;
                        }

                        string found = FindLinked(tree, emitter.CustomMaterialPathHash, Tree);
                        if (found != null)
                        {
                            linked++;
                            BinTree declaring = Tree(XxHash64Ext.Hash(found.ToLowerInvariant()));
                            VfxSystemDefinition resolved = VfxGraphParser.ResolveLinkedCustomMaterials(
                                system with { Emitters = new[] { emitter } }, new[] { declaring });
                            if (resolved.Emitters[0].HasResolvedCustomMaterial) fixedByLinked++;
                            string kind = path.EndsWith(".materials.bin", StringComparison.Ordinal) ? "map" : "skin";
                            linkedByKind[kind] = linkedByKind.GetValueOrDefault(kind) + 1;
                            if (linkedExamples.Count < 12) linkedExamples.Add($"{path} {emitter.Name} -> {found}");
                        }
                        else
                        {
                            nowhere++;
                            if (nowhereExamples.Count < 8) nowhereExamples.Add($"{path} {emitter.Name} 0x{emitter.CustomMaterialPathHash:x8}");
                        }
                    }
                }
            }

            Console.WriteLine($"[MaterialCensus] custom-material emitters={emitters} ownBin={own} linkedBin={linked} ({string.Join(", ", linkedByKind.Select(p => $"{p.Key}={p.Value}"))}) nowhere={nowhere} resolvedByLinkedStep={fixedByLinked}");
            Console.WriteLine($"[MaterialCensus] own-BIN materials without a program={noProgram}, found with data/shaders/shaders.bin={recoveredByShaders} (shaders.bin loaded={shaders != null})");
            foreach (string line in recoveredExamples) Console.WriteLine($"[MaterialCensus] recovered {line}");
            foreach (string line in linkedExamples) Console.WriteLine($"[MaterialCensus] linked {line}");
            foreach (string line in nowhereExamples) Console.WriteLine($"[MaterialCensus] nowhere {line}");
            foreach (WadFile wad in wads) wad.Dispose();
        }

        private static string FindLinked(BinTree start, uint material, Func<ulong, BinTree> tree)
        {
            var seen = new HashSet<ulong>();
            var queue = new Queue<string>(start.Dependencies);
            int opened = 0;
            while (queue.Count > 0 && opened < LinkedCap)
            {
                string dependency = queue.Dequeue();
                ulong hash = XxHash64Ext.Hash(dependency.ToLowerInvariant());
                if (!seen.Add(hash)) continue;
                opened++;
                BinTree linked = tree(hash);
                if (linked == null) continue;
                if (linked.Objects.ContainsKey(material)) return dependency;
                foreach (string next in linked.Dependencies) queue.Enqueue(next);
            }
            return null;
        }
    }
}
