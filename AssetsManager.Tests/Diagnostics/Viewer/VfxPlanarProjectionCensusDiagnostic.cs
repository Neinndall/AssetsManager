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
    /// `vfx-planar-projection-census [out.csv]`: every emitter of the installed champion, map and common BINs whose primitive is a
    /// VfxPrimitivePlanarProjection, with where it lives, the emitter fields and nested classes it authors, its blend
    /// mode and its projection band.
    /// </summary>
    internal static class VfxPlanarProjectionCensusDiagnostic
    {
        public static void Run(string[] args)
        {
            string hashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
            Dictionary<uint, string> fieldNames = ReadNames(Path.Combine(hashDir, "hashes.binfields.txt"));
            Dictionary<uint, string> typeNames = ReadNames(Path.Combine(hashDir, "hashes.bintypes.txt"));
            string Name(Dictionary<uint, string> names, uint hash) => names.TryGetValue(hash, out string name) ? name : $"0x{hash:x8}";

            var binPaths = new Dictionary<ulong, string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.game.txt")))
            {
                int space = line.IndexOf(' ');
                if (space > 0 && line.EndsWith(".bin", StringComparison.Ordinal) &&
                    ulong.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    binPaths[hash] = line[(space + 1)..];
            }

            uint systemClass = Fnv1a.HashLower("VfxSystemDefinitionData");
            uint emitterClass = Fnv1a.HashLower("VfxEmitterDefinitionData");
            uint primitiveField = Fnv1a.HashLower("primitive");
            uint projectionClass = Fnv1a.HashLower("VfxPrimitivePlanarProjection");
            uint projectionField = Fnv1a.HashLower("mProjection");
            uint yRangeField = Fnv1a.HashLower("mYRange");
            uint fadingField = Fnv1a.HashLower("mFading");
            uint blendModeField = Fnv1a.HashLower("blendMode");
            uint emitterNameField = Fnv1a.HashLower("emitterName");

            var fields = new Dictionary<string, (int Count, string Example)>();
            var values = new Dictionary<string, int>();
            var seenSystems = new HashSet<uint>();
            int champion = 0, map = 0, systems = 0;
            var examples = new List<string>();
            var rows = new List<string> { "side,bin,system,emitter,blend,band,erosion,mult,palette,disabled" };
            uint particleNameField = Fnv1a.HashLower("particleName");

            string final = Path.Combine(InstalledSkins.FindInstall(), @"Game\DATA\FINAL");
            foreach (string wadPath in Directory.GetFiles(final, "*.wad.client", SearchOption.AllDirectories)
                         .Where(path => path.Contains(@"\Champions\") || path.Contains(@"\Maps\") ||
                                        Path.GetFileName(path).StartsWith("Common", StringComparison.OrdinalIgnoreCase))
                         .Where(path => !Path.GetFileName(path)[..^".wad.client".Length].Contains('.')))
            {
                bool isChampion = wadPath.Contains(@"\Champions\");
                using var wad = new WadFile(wadPath);
                foreach (ulong chunk in wad.Chunks.Keys)
                {
                    if (!binPaths.TryGetValue(chunk, out string binPath) || binPath.Contains("/animations/")) continue;
                    BinTree tree;
                    try
                    {
                        using var data = wad.LoadChunkDecompressed(chunk);
                        using var stream = new MemoryStream(data.Span.ToArray(), writable: false);
                        tree = new BinTree(stream);
                    }
                    catch { continue; }

                    foreach (BinTreeObject system in tree.Objects.Values)
                    {
                        if (system.ClassHash != systemClass || !seenSystems.Add(system.PathHash)) continue;

                        bool systemHasProjection = false;
                        foreach (BinTreeStruct emitter in Structs(system.Properties.Values).Where(item => item.ClassHash == emitterClass))
                        {
                            if (!emitter.Properties.TryGetValue(primitiveField, out BinTreeProperty primitive) ||
                                primitive is not BinTreeStruct { } primitiveStruct || primitiveStruct.ClassHash != projectionClass)
                                continue;

                            systemHasProjection = true;
                            if (isChampion) champion++; else map++;
                            string name = emitter.Properties.TryGetValue(emitterNameField, out var nameProperty) && nameProperty is BinTreeString text ? text.Value : "?";
                            string label = $"{binPath} {name}";
                            if (examples.Count < 30) examples.Add(label);

                            foreach ((uint field, BinTreeProperty property) in emitter.Properties)
                            {
                                string key = Name(fieldNames, field);
                                if (property is BinTreeStruct nested) key += $" : {Name(typeNames, nested.ClassHash)}";
                                fields[key] = fields.TryGetValue(key, out var usage) ? (usage.Count + 1, usage.Example) : (1, label);
                            }

                            string blend = emitter.Properties.TryGetValue(blendModeField, out var blendProperty)
                                ? Scalar(blendProperty) : "default";
                            Tally(values, $"blendMode={blend}");

                            var band = primitiveStruct.Properties.TryGetValue(projectionField, out var bandProperty) && bandProperty is BinTreeStruct bandStruct
                                ? $"yRange={(bandStruct.Properties.TryGetValue(yRangeField, out var y) ? Scalar(y) : "default")} fading={(bandStruct.Properties.TryGetValue(fadingField, out var f) ? Scalar(f) : "default")}"
                                : "noProjectionBlock";
                            Tally(values, band);

                            string systemName = system.Properties.TryGetValue(particleNameField, out var systemNameProperty) && systemNameProperty is BinTreeString systemText
                                ? systemText.Value : $"0x{system.PathHash:x8}";
                            bool Has(string field) => emitter.Properties.ContainsKey(Fnv1a.HashLower(field));
                            rows.Add(string.Join(",", isChampion ? "champion" : "map", binPath, systemName, name, blend, band.Replace(' ', ';'),
                                Has("alphaErosionDefinition"), Has("textureMult"), Has("paletteDefinition"),
                                emitter.Properties.TryGetValue(Fnv1a.HashLower("disabled"), out var disabledProperty) && disabledProperty is BinTreeBool { Value: true }));
                        }
                        if (systemHasProjection) systems++;
                    }
                }
            }

            string csv = args.FirstOrDefault(arg => arg.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
            if (csv != null) File.WriteAllLines(csv, rows);
            Console.WriteLine($"[PlanarProjection] emitters={champion + map} champion={champion} mapOrCommon={map} systems={systems}");
            foreach (var pair in fields.OrderByDescending(pair => pair.Value.Count))
                Console.WriteLine($"[PlanarProjection] field {pair.Key} = {pair.Value.Count} e.g. {pair.Value.Example}");
            foreach (var pair in values.OrderByDescending(pair => pair.Value))
                Console.WriteLine($"[PlanarProjection] value {pair.Key} = {pair.Value}");
            foreach (string example in examples)
                Console.WriteLine($"[PlanarProjection] example {example}");
        }

        private static IEnumerable<BinTreeStruct> Structs(IEnumerable<BinTreeProperty> properties)
        {
            foreach (BinTreeProperty property in properties)
            {
                switch (property)
                {
                    case BinTreeStruct nested:
                        yield return nested;
                        foreach (BinTreeStruct inner in Structs(nested.Properties.Values)) yield return inner;
                        break;
                    case BinTreeContainer container:
                        foreach (BinTreeStruct inner in Structs(container.Elements)) yield return inner;
                        break;
                    case BinTreeOptional { Value: not null } optional:
                        foreach (BinTreeStruct inner in Structs(new[] { optional.Value })) yield return inner;
                        break;
                }
            }
        }

        private static string Scalar(BinTreeProperty property) => property switch
        {
            BinTreeF32 value => value.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeU8 value => value.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeU32 value => value.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeI32 value => value.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeBool value => value.Value.ToString(),
            _ => property.GetType().Name
        };

        private static void Tally(Dictionary<string, int> table, string key) =>
            table[key] = table.TryGetValue(key, out int count) ? count + 1 : 1;

        private static Dictionary<uint, string> ReadNames(string path)
        {
            var names = new Dictionary<uint, string>();
            foreach (string line in File.ReadLines(path))
            {
                int space = line.IndexOf(' ');
                if (space > 0 && uint.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
                    names.TryAdd(hash, line[(space + 1)..]);
            }
            return names;
        }
    }
}
