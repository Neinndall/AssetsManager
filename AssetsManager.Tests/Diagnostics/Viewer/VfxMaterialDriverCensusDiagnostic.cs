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
    /// `vfx-material-driver-census`: counts the `CustomMaterial.materialDrivers` of every installed VFX emitter by driver
    /// class, `frequency` and whether the emitter draws a single particle, with examples.
    /// </summary>
    internal static class VfxMaterialDriverCensusDiagnostic
    {
        private static readonly uint EmitterClass = Fnv1a.HashLower("VfxEmitterDefinitionData");
        private static readonly uint CustomMaterial = Fnv1a.HashLower("CustomMaterial");
        private static readonly uint MaterialDrivers = Fnv1a.HashLower("materialDrivers");
        private static readonly uint Frequency = Fnv1a.HashLower("frequency");
        private static readonly uint SingleParticle = Fnv1a.HashLower("isSingleParticle");
        private static readonly uint EmitterName = Fnv1a.HashLower("emitterName");

        public static void Run(string[] args)
        {
            string hashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
            var binPaths = new Dictionary<ulong, string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.game.txt")))
            {
                int space = line.IndexOf(' ');
                if (space <= 0) continue;
                string path = line[(space + 1)..];
                if (path.StartsWith("data/", StringComparison.Ordinal) && path.EndsWith(".bin", StringComparison.Ordinal) &&
                    ulong.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    binPaths[hash] = path;
            }
            var classNames = new Dictionary<uint, string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.bintypes.txt")))
            {
                int space = line.IndexOf(' ');
                if (space > 0 && uint.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
                    classNames[hash] = line[(space + 1)..];
            }

            var counts = new Dictionary<string, int>();
            var examples = new Dictionary<string, List<string>>();
            int emitters = 0;
            var seen = new HashSet<ulong>();
            string final = Path.Combine(InstalledSkins.FindInstall(), @"Game\DATA\FINAL");
            foreach (string wadPath in Directory.GetFiles(final, "*.wad.client", SearchOption.AllDirectories)
                         .Where(path => !Path.GetFileName(path)[..^".wad.client".Length].Contains('.')))
            {
                using var wad = new WadFile(wadPath);
                foreach (ulong chunk in wad.Chunks.Keys)
                {
                    if (!binPaths.TryGetValue(chunk, out string binPath) || !seen.Add(chunk)) continue;
                    BinTree tree;
                    try
                    {
                        using var data = wad.LoadChunkDecompressed(chunk);
                        using var stream = new MemoryStream(data.Span.ToArray(), writable: false);
                        tree = new BinTree(stream);
                    }
                    catch { continue; }
                    foreach (BinTreeObject item in tree.Objects.Values)
                        foreach (BinTreeProperty property in item.Properties.Values)
                            Walk(property, binPath);
                }
            }

            Console.WriteLine($"[MaterialDrivers] emitters with drivers={emitters}");
            foreach ((string key, int count) in counts.OrderByDescending(pair => pair.Value))
            {
                Console.WriteLine($"[MaterialDrivers]   {key}: {count}");
                foreach (string example in examples[key].Take(3))
                    Console.WriteLine($"[MaterialDrivers]     e.g. {example}");
            }

            void Walk(BinTreeProperty property, string binPath)
            {
                switch (property)
                {
                    case BinTreeStruct structure:
                        if (structure.ClassHash == EmitterClass) Count(structure, binPath);
                        foreach (BinTreeProperty child in structure.Properties.Values) Walk(child, binPath);
                        break;
                    case BinTreeContainer container:
                        foreach (BinTreeProperty child in container.Elements) Walk(child, binPath);
                        break;
                    case BinTreeMap map:
                        foreach (KeyValuePair<BinTreeProperty, BinTreeProperty> pair in map) Walk(pair.Value, binPath);
                        break;
                }
            }

            void Count(BinTreeStruct emitter, string binPath)
            {
                if (!emitter.Properties.TryGetValue(CustomMaterial, out BinTreeProperty material) || material is not BinTreeStruct custom ||
                    !custom.Properties.TryGetValue(MaterialDrivers, out BinTreeProperty drivers) || drivers is not BinTreeMap map || map.Count == 0)
                    return;
                emitters++;
                bool single = emitter.Properties.TryGetValue(SingleParticle, out BinTreeProperty flag) && flag is BinTreeBool { Value: true };
                string name = emitter.Properties.TryGetValue(EmitterName, out BinTreeProperty named) && named is BinTreeString text ? text.Value : "?";
                foreach (KeyValuePair<BinTreeProperty, BinTreeProperty> pair in map)
                {
                    if (pair.Value is not BinTreeStruct driver) continue;
                    string cls = classNames.GetValueOrDefault(driver.ClassHash, $"0x{driver.ClassHash:x8}");
                    string frequency = driver.Properties.TryGetValue(Frequency, out BinTreeProperty f) && f is BinTreeU8 u8 ? u8.Value.ToString(CultureInfo.InvariantCulture) : "-";
                    string key = $"{cls} frequency={frequency} {(single ? "single" : "multi")}";
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                    if (!examples.TryGetValue(key, out var list)) examples[key] = list = new List<string>();
                    string parameter = pair.Key is BinTreeString s ? s.Value : "?";
                    if (list.Count < 3) list.Add($"{binPath} {name} {parameter}");
                }
            }
        }
    }
}
