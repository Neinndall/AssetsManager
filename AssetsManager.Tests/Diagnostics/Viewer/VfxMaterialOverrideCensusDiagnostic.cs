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
    /// `vfx-material-override-census`: for every emitter of the installed champion and map BINs with
    /// materialOverrideDefinitions, its primitive and how each override relates to the emitter's own texture
    /// (same, different, emitter has none), whether it names a submesh, a Material link or a blend mode.
    /// </summary>
    internal static class VfxMaterialOverrideCensusDiagnostic
    {
        public static void Run(string[] args)
        {
            string hashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
            var binPaths = new Dictionary<ulong, string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.game.txt")))
            {
                int space = line.IndexOf(' ');
                if (space > 0 && line.EndsWith(".bin", StringComparison.Ordinal) && !line.Contains("/animations/") &&
                    ulong.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    binPaths[hash] = line[(space + 1)..];
            }
            var types = new Dictionary<uint, string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.bintypes.txt")))
            {
                int space = line.IndexOf(' ');
                if (space > 0 && uint.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
                    types.TryAdd(hash, line[(space + 1)..]);
            }

            var fieldNames = new Dictionary<uint, string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.binfields.txt")))
            {
                int space = line.IndexOf(' ');
                if (space > 0 && uint.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
                    fieldNames.TryAdd(hash, line[(space + 1)..]);
            }
            uint systemClass = Fnv1a.HashLower("VfxSystemDefinitionData"), emitterClass = Fnv1a.HashLower("VfxEmitterDefinitionData");
            uint overrides = Fnv1a.HashLower("materialOverrideDefinitions"), primitive = Fnv1a.HashLower("primitive");
            uint texture = Fnv1a.HashLower("texture"), baseTexture = Fnv1a.HashLower("baseTexture"), subMesh = Fnv1a.HashLower("subMeshName");
            uint material = Fnv1a.HashLower("Material"), blend = Fnv1a.HashLower("overrideBlendMode"), transition = Fnv1a.HashLower("transitionTexture");
            uint emitterName = Fnv1a.HashLower("emitterName"), particleName = Fnv1a.HashLower("particleName");

            var tally = new Dictionary<string, int>(StringComparer.Ordinal);
            var examples = new Dictionary<string, string>(StringComparer.Ordinal);
            void Add(string key, string example)
            {
                tally[key] = tally.GetValueOrDefault(key) + 1;
                examples.TryAdd(key, example);
            }
            var seen = new HashSet<uint>();
            string final = Path.Combine(InstalledSkins.FindInstall(), @"Game\DATA\FINAL");
            foreach (string wadPath in Directory.GetFiles(final, "*.wad.client", SearchOption.AllDirectories)
                         .Where(path => path.Contains(@"\Champions\") || path.Contains(@"\Maps\"))
                         .Where(path => !Path.GetFileName(path)[..^".wad.client".Length].Contains('.')))
            {
                using var wad = new WadFile(wadPath);
                foreach (ulong chunk in wad.Chunks.Keys)
                {
                    if (!binPaths.TryGetValue(chunk, out string binPath)) continue;
                    BinTree tree;
                    try
                    {
                        using var data = wad.LoadChunkDecompressed(chunk);
                        using var stream = new MemoryStream(data.Span.ToArray(), writable: false);
                        tree = new BinTree(stream);
                    }
                    catch { continue; }
                    foreach (BinTreeObject system in tree.Objects.Values.Where(item => item.ClassHash == systemClass && seen.Add(item.PathHash)))
                    {
                        string systemName = (system.Properties.GetValueOrDefault(particleName) as BinTreeString)?.Value ?? $"0x{system.PathHash:x8}";
                        foreach (BinTreeStruct emitter in system.Properties.Values.OfType<BinTreeContainer>()
                                     .SelectMany(list => list.Elements).OfType<BinTreeStruct>().Where(item => item.ClassHash == emitterClass))
                        {
                            if (emitter.Properties.GetValueOrDefault(overrides) is not BinTreeContainer list || list.Elements.Count == 0) continue;
                            string prim = emitter.Properties.GetValueOrDefault(primitive) is BinTreeStruct p
                                ? types.GetValueOrDefault(p.ClassHash, $"0x{p.ClassHash:x8}") : "(quad)";
                            string own = (emitter.Properties.GetValueOrDefault(texture) as BinTreeString)?.Value;
                            string label = $"{binPath} {systemName}/{(emitter.Properties.GetValueOrDefault(emitterName) as BinTreeString)?.Value}";
                            Add($"emitter prim={prim}", label);
                            Add($"emitter overrides={Math.Min(list.Elements.Count, 4)}", label);
                            foreach (BinTreeStruct item in list.Elements.OfType<BinTreeStruct>())
                            {
                                ulong? baseTex = item.Properties.GetValueOrDefault(baseTexture) switch
                                {
                                    BinTreeWadChunkLink link => link.Value,
                                    BinTreeString text => XxHash64Ext.Hash(text.Value.ToLowerInvariant()),
                                    _ => null
                                };
                                string relation = baseTex == null ? "noBase"
                                    : own == null ? "emitterHasNoTexture"
                                    : baseTex == XxHash64Ext.Hash(own.ToLowerInvariant()) ? "sameAsEmitter" : "differs";
                                foreach ((uint field, BinTreeProperty value) in item.Properties)
                                {
                                    string fieldName = field == baseTexture ? "baseTexture" : field == subMesh ? "subMeshName" : field == material ? "Material"
                                        : field == blend ? "overrideBlendMode" : field == transition ? "transitionTexture" : fieldNames.GetValueOrDefault(field, $"0x{field:x8}");
                                    string text = Describe(value);
                                    if (field != baseTexture && field != transition && field != material && field != subMesh)
                                        Add($"value {fieldName}={text}", label);
                                    else
                                        Add($"value {fieldName}=<{value.GetType().Name}>", label);
                                }
                                Add($"override base={relation} submesh={item.Properties.ContainsKey(subMesh)} material={item.Properties.ContainsKey(material)} " +
                                    $"blend={item.Properties.ContainsKey(blend)} transition={item.Properties.ContainsKey(transition)}", label);
                            }
                        }
                    }
                }
            }

            string Describe(BinTreeProperty value) => value switch
            {
                BinTreeString text => text.Value,
                BinTreeU8 number => number.Value.ToString(CultureInfo.InvariantCulture),
                BinTreeU32 number => number.Value.ToString(CultureInfo.InvariantCulture),
                BinTreeI32 number => number.Value.ToString(CultureInfo.InvariantCulture),
                BinTreeF32 number => number.Value.ToString(CultureInfo.InvariantCulture),
                BinTreeBool flag => flag.Value.ToString(),
                BinTreeStruct nested => types.GetValueOrDefault(nested.ClassHash, $"0x{nested.ClassHash:x8}") + "{" +
                    string.Join(",", nested.Properties.Keys.Select(key => fieldNames.GetValueOrDefault(key, $"0x{key:x8}"))) + "}",
                _ => value.GetType().Name
            };
            foreach ((string key, int count) in tally.OrderBy(pair => pair.Key))
                Console.WriteLine($"[OverrideCensus] {count,6} {key}  e.g. {examples[key]}");
        }
    }
}
