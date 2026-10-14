using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class ShaderSharedDataProbe
    {
        private const string BinPath = "assets/shaders/shareddata.bin";

        private static readonly Dictionary<string, string> SpecialBufferLeaves = new(StringComparer.Ordinal)
        {
            ["CharacterPerDrawVertexCB"] = "CharacterPerDrawVS",
            ["PostEffectPixelCB"] = "PostEffects",
            ["FontVertexCB"] = "FontRendering",
            ["VFXDynamicPerParticleInstanceCBVS"] = "VFXDynamicPerParticleVS",
            ["VFXDynamicPerParticleInstanceCBPS"] = "VFXDynamicPerParticlePS",
        };

        public static void Run(string pbeRoot, string hashesPath)
        {
            Console.WriteLine("=== SHADER SHAREDDATA PROBE (PR #40 verification) ===");

            Dictionary<ulong, string> wadPaths = LoadWadPaths(Path.Combine(hashesPath, "hashes.game.txt"));
            var keyForPath = wadPaths.FirstOrDefault(pair => pair.Value.Equals(BinPath, StringComparison.OrdinalIgnoreCase));
            if (keyForPath.Key == 0)
            {
                Console.WriteLine($"ERROR: '{BinPath}' no esta en hashes.game.txt");
                return;
            }
            Console.WriteLine($"Chunk key de '{BinPath}': 0x{keyForPath.Key:x16}");

            string gameDir = Path.Combine(pbeRoot, "Game");
            var wadsWithBin = new List<string>();
            foreach (string wadPath in Directory.EnumerateFiles(gameDir, "*.wad.client", SearchOption.AllDirectories))
            {
                try
                {
                    using var wad = new LeagueToolkit.Core.Wad.WadFile(wadPath);
                    if (wad.Chunks.ContainsKey(keyForPath.Key))
                        wadsWithBin.Add(wadPath);
                }
                catch { }
            }
            if (wadsWithBin.Count == 0)
            {
                Console.WriteLine($"ERROR: chunk no encontrado en ningun WAD de Game");
                return;
            }
            Console.WriteLine($"Encontrado en {wadsWithBin.Count} WADs:");
            foreach (string w in wadsWithBin)
            {
                Console.WriteLine($"  - {w}");
                try
                {
                    using var wad = new LeagueToolkit.Core.Wad.WadFile(w);
                    var chunk = wad.Chunks[keyForPath.Key];
                    using var data = wad.LoadChunkDecompressed(chunk);
                    ArraySegment<byte> buffer = data.DangerousGetArray();
                    Console.WriteLine($"      comprimido={chunk.CompressedSize} descomprimido={buffer.Count}");
                }
                catch { }
            }
            string wadWithBin = wadsWithBin.FirstOrDefault(w => w.Contains("Shaders.wad.client", StringComparison.OrdinalIgnoreCase)) ?? wadsWithBin[0];
            Console.WriteLine($"Usando: {Path.GetFileName(wadWithBin)}");

            uint cbClass = Fnv1a.HashLower("X3DSharedConstantBufferDef");
            uint samplerClass = Fnv1a.HashLower("X3DSharedSamplerDef");

            using (var wad = new LeagueToolkit.Core.Wad.WadFile(wadWithBin))
            {
                var chunk = wad.Chunks[keyForPath.Key];
                using var data = wad.LoadChunkDecompressed(chunk);
                ArraySegment<byte> buffer = data.DangerousGetArray();
                using var stream = new MemoryStream(buffer.Array, buffer.Offset, buffer.Count, false);
                var tree = new BinTree(stream);

                int total = 0, oldScheme = 0, newScheme = 0, special = 0, unresolved = 0;
                var sharedEntryHashes = new List<uint>();
                var allBinStrings = new List<string>();
                foreach (var obj in tree.Objects.Values)
                    CollectStrings(obj.Properties.Values, allBinStrings);
                Console.WriteLine();
                Console.WriteLine($"Strings totales en el bin: {allBinStrings.Count}");
                Console.WriteLine("  key        clase       name -> candidate (scheme)");
                foreach (var pair in tree.Objects)
                {
                    BinTreeObject obj = pair.Value;
                    bool isCb = obj.ClassHash == cbClass;
                    bool isSampler = obj.ClassHash == samplerClass;
                    if (!isCb && !isSampler) continue;
                    total++;
                    sharedEntryHashes.Add(pair.Key);
                    string kind = isCb ? "CB  " : "SMPL";
                    if (!TryGetString(obj.Properties, "name", out string name))
                    {
                        Console.WriteLine($"  {pair.Key:x8} {kind} (sin campo name)");
                        unresolved++;
                        continue;
                    }

                    string resolvedBy = null;
                    if (isCb)
                    {
                        if (SpecialBufferLeaves.TryGetValue(name, out string specialLeaf))
                        {
                            resolvedBy = Check(pair.Key, $"Shaders/SharedData/ConstantBuffers/{specialLeaf}");
                            if (resolvedBy != null) special++;
                        }
                        if (resolvedBy == null)
                        {
                            string leaf = StripSharedSuffix(name);
                            resolvedBy = Check(pair.Key, $"Shaders/SharedData/ConstantBuffers/{leaf}");
                            if (resolvedBy != null) newScheme++;
                        }
                        if (resolvedBy == null)
                        {
                            string leaf = StripPassSuffix(name);
                            resolvedBy = Check(pair.Key, $"Shaders/SharedData/ConstantBuffers/{leaf}");
                            if (resolvedBy != null) special++;
                        }
                        if (resolvedBy == null)
                        {
                            resolvedBy = Check(pair.Key, $"Shaders/SharedData/{name}");
                            if (resolvedBy != null) oldScheme++;
                        }
                    }
                    else
                    {
                        resolvedBy = Check(pair.Key, $"Shaders/SharedData/SharedSamplers/{name}");
                        if (resolvedBy != null) newScheme++;
                        if (resolvedBy == null)
                        {
                            resolvedBy = Check(pair.Key, $"Shaders/SharedData/{name}");
                            if (resolvedBy != null) oldScheme++;
                        }
                    }

                    if (resolvedBy == null)
                    {
                        foreach (string otherString in allBinStrings)
                        {
                            resolvedBy = Check(pair.Key, $"Shaders/SharedData/ConstantBuffers/{otherString}");
                            if (resolvedBy != null)
                            {
                                newScheme++;
                                break;
                            }
                        }
                    }
                    if (resolvedBy == null)
                    {
                        Console.WriteLine($"  {pair.Key:x8} {kind} {name}  <-- SIN RESOLVER, propiedades:");
                        foreach (var prop in obj.Properties.Values)
                            Console.WriteLine($"       namehash={prop.NameHash:x8} type={prop.Type} value={Describe(prop)}");
                        unresolved++;
                    }
                    else
                    {
                        Console.WriteLine($"  {pair.Key:x8} {kind} {name} -> {resolvedBy}");
                    }
                }

                Console.WriteLine();
                Console.WriteLine($"Total defs: {total} | nuevos esquemas: {newScheme} | esquema stale: {oldScheme} | especiales: {special} | sin resolver: {unresolved}");

                var targets = new Dictionary<InternalHashKind, HashSet<ulong>>
                {
                    [InternalHashKind.BinEntries] = new(),
                    [InternalHashKind.BinFields] = new(),
                    [InternalHashKind.BinTypes] = new(),
                    [InternalHashKind.BinHashes] = new(),
                    [InternalHashKind.RstXxh3] = new(),
                    [InternalHashKind.RstXxh64] = new()
                };
                var targetCount = sharedEntryHashes.Count;
                foreach (uint entryHash in sharedEntryHashes)
                    targets[InternalHashKind.BinEntries].Add(entryHash);
                var matcher = new InternalHashEvidenceMatcher(targets);
                stream.Position = 0;
                BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, BinPath, Path.GetFileName(wadWithBin));
                Console.WriteLine();
                Console.WriteLine($"APP REAL (MatchBinContentEvidence): targets BinEntries = {targetCount} | resueltos y verificados = {matcher.Matches.Count} | restantes = {targetCount - matcher.Matches.Count}");
            }
        }

        private static string Check(uint entryHash, string candidate)
        {
            return Fnv1a.HashLower(candidate) == entryHash ? candidate : null;
        }

        private static string StripSharedSuffix(string name)
        {
            if (name.EndsWith("_BUFFER", StringComparison.Ordinal))
                return name[..^"_BUFFER".Length];
            if (name.EndsWith("CB", StringComparison.Ordinal) && name.Length > 2)
                return name[..^2];
            return name;
        }

        private static string StripPassSuffix(string name)
        {
            if (name.EndsWith("PixelCB", StringComparison.Ordinal))
                return name[..^"PixelCB".Length] + "PS";
            if (name.EndsWith("VertexCB", StringComparison.Ordinal))
                return name[..^"VertexCB".Length] + "VS";
            return name;
        }

        private static void CollectStrings(IEnumerable<BinTreeProperty> properties, List<string> result)
        {
            foreach (var property in properties)
            {
                switch (property)
                {
                    case BinTreeString s: result.Add(s.Value); break;
                    case BinTreeContainer c: CollectStrings(c.Elements, result); break;
                    case BinTreeStruct s: CollectStrings(s.Properties.Values, result); break;
                    case BinTreeMap m:
                        foreach (var kv in m)
                        {
                            if (kv.Key is BinTreeProperty k) CollectStrings(new[] { k }, result);
                            if (kv.Value is BinTreeProperty v) CollectStrings(new[] { v }, result);
                        }
                        break;
                    case BinTreeOptional o when o.Value != null: CollectStrings(new[] { o.Value }, result); break;
                }
            }
        }

        private static string DescribeStruct(BinTreeStruct s)
        {
            var parts = new List<string>();
            foreach (var prop in s.Properties.Values)
                parts.Add($"{prop.NameHash:x8}:{Describe(prop)}");
            return string.Join(" | ", parts);
        }

        private static string Describe(BinTreeProperty property)
        {
            switch (property)
            {
                case BinTreeString s: return s.Value;
                case BinTreeHash h: return $"0x{h.Value:x8}";
                case BinTreeU32 u: return u.Value.ToString();
                case BinTreeObjectLink l: return $"link 0x{l.Value:x8}";
                case BinTreeContainer c: return $"container[{c.Elements.Count}]: {string.Join(" | ", c.Elements.Select(Describe))}";
                case BinTreeStruct s: return $"struct 0x{s.ClassHash:x8} ({DescribeStruct(s)})";
                case BinTreeMap m: return $"map[{m.Count}]";
                case BinTreeOptional o: return o.Value == null ? "optional(null)" : Describe(o.Value);
                default: return property.Type.ToString();
            }
        }

        private static bool TryGetString(Dictionary<uint, BinTreeProperty> properties, string field, out string value)
        {
            if (properties.TryGetValue(Fnv1a.HashLower(field), out BinTreeProperty property) && property is BinTreeString text)
            {
                value = text.Value;
                return true;
            }
            value = null;
            return false;
        }

        private static Dictionary<ulong, string> LoadWadPaths(string path)
        {
            var result = new Dictionary<ulong, string>();
            if (!File.Exists(path)) return result;
            foreach (string line in File.ReadLines(path))
            {
                if (line.Length > 17 &&
                    ulong.TryParse(line.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                {
                    result.TryAdd(hash, line[17..]);
                }
            }
            return result;
        }
    }
}
