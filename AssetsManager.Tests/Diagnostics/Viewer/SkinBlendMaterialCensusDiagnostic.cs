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
    /// `skin-blend-material-census`: counts the champion StaticMaterialDefs whose first technique pass enables blending,
    /// grouped by the shader that pass links, with example materials. The no-shader preview treats their base texture
    /// alpha as coverage.
    /// </summary>
    internal static class SkinBlendMaterialCensusDiagnostic
    {
        private static readonly uint MaterialClass = Fnv1a.HashLower("StaticMaterialDef");
        private static readonly uint Techniques = Fnv1a.HashLower("techniques");
        private static readonly uint Passes = Fnv1a.HashLower("passes");
        private static readonly uint Shader = Fnv1a.HashLower("shader");
        private static readonly uint BlendEnable = Fnv1a.HashLower("blendEnable");
        private static readonly uint Name = Fnv1a.HashLower("name");

        public static void Run(string[] args)
        {
            string hashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
            var binPaths = new Dictionary<ulong, string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.game.txt")))
            {
                int space = line.IndexOf(' ');
                if (space <= 0) continue;
                string path = line[(space + 1)..];
                if (path.StartsWith("data/characters/", StringComparison.Ordinal) && path.EndsWith(".bin", StringComparison.Ordinal) &&
                    !path.Contains("/animations/", StringComparison.Ordinal) &&
                    ulong.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    binPaths[hash] = path;
            }
            var entryNames = new Dictionary<uint, string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.binentries.txt")))
            {
                int space = line.IndexOf(' ');
                if (space > 0 && uint.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
                    entryNames[hash] = line[(space + 1)..];
            }

            var seenMaterials = new HashSet<uint>();
            var byShader = new Dictionary<string, List<string>>();
            int materials = 0;
            string final = Path.Combine(InstalledSkins.FindInstall(), @"Game\DATA\FINAL\Champions");
            var seenBins = new HashSet<ulong>();
            foreach (string wadPath in Directory.GetFiles(final, "*.wad.client")
                         .Where(path => !Path.GetFileName(path)[..^".wad.client".Length].Contains('.')))
            {
                using var wad = new WadFile(wadPath);
                foreach (ulong chunk in wad.Chunks.Keys)
                {
                    if (!binPaths.ContainsKey(chunk) || !seenBins.Add(chunk)) continue;
                    BinTree tree;
                    try
                    {
                        using var data = wad.LoadChunkDecompressed(chunk);
                        using var stream = new MemoryStream(data.Span.ToArray(), writable: false);
                        tree = new BinTree(stream);
                    }
                    catch { continue; }
                    foreach (BinTreeObject material in tree.Objects.Values.Where(item => item.ClassHash == MaterialClass))
                    {
                        if (!seenMaterials.Add(material.PathHash)) continue;
                        materials++;
                        if (!material.Properties.TryGetValue(Techniques, out BinTreeProperty techniques) ||
                            techniques is not BinTreeContainer techniqueList ||
                            techniqueList.Elements.FirstOrDefault() is not BinTreeStruct technique ||
                            !technique.Properties.TryGetValue(Passes, out BinTreeProperty passes) ||
                            passes is not BinTreeContainer passList ||
                            passList.Elements.FirstOrDefault() is not BinTreeStruct pass ||
                            !pass.Properties.TryGetValue(BlendEnable, out BinTreeProperty blend) ||
                            blend is not BinTreeBool { Value: true })
                            continue;
                        uint shaderHash = pass.Properties.TryGetValue(Shader, out BinTreeProperty link) && link is BinTreeObjectLink objectLink ? objectLink.Value : 0;
                        string shader = entryNames.GetValueOrDefault(shaderHash, $"0x{shaderHash:x8}");
                        string name = material.Properties.TryGetValue(Name, out BinTreeProperty named) && named is BinTreeString text
                            ? text.Value
                            : entryNames.GetValueOrDefault(material.PathHash, $"0x{material.PathHash:x8}");
                        if (!byShader.TryGetValue(shader, out var list)) byShader[shader] = list = new List<string>();
                        list.Add(name);
                    }
                }
            }

            Console.WriteLine($"[BlendMaterials] champion materials={materials} blended first pass={byShader.Values.Sum(list => list.Count)}");
            foreach ((string shader, List<string> list) in byShader.OrderByDescending(pair => pair.Value.Count).Take(40))
                Console.WriteLine($"[BlendMaterials]   {list.Count,5} {shader} e.g. {string.Join(", ", list.Take(2))}");
        }
    }
}
