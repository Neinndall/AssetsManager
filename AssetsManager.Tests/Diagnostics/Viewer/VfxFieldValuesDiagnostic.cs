using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Tests.Support;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `vfx-field-values &lt;fieldName&gt; [--with otherField,...]`: every value one field takes inside the VFX emitters
    /// of the installed skin and map BINs, searched in the emitter and the structs nested in it, tallied by the
    /// emitter's primitive and by where the field sits. `--with` adds the values of other emitter fields beside it.
    /// </summary>
    internal static class VfxFieldValuesDiagnostic
    {
        private static readonly string[] PrimitiveNames =
        {
            "VfxPrimitiveCameraQuad", "VfxPrimitiveArbitraryQuad", "VfxPrimitiveCameraUnitQuad", "VfxPrimitiveMesh",
            "VfxPrimitiveAttachedMesh", "VfxPrimitiveCameraTrail", "VfxPrimitiveArbitraryTrail", "VfxPrimitiveBeam",
            "VfxPrimitiveCameraSegmentBeam", "VfxPrimitiveRay", "VfxPrimitivePlanarProjection", "VfxPrimitiveArbitraryTrail"
        };

        public static void Run(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: vfx-field-values <fieldName> [--with otherField,...]");
                return;
            }

            uint target = VfxParsingHash.Fnv1a(args[0]);
            string[] with = args.SkipWhile(arg => arg != "--with").Skip(1).FirstOrDefault()?.Split(',') ?? Array.Empty<string>();
            var names = new Dictionary<uint, string>();
            foreach (string name in PrimitiveNames.Concat(new[] { "VfxMeshDefinitionData", "VfxEmitterDefinitionData" }))
                names[VfxParsingHash.Fnv1a(name)] = name;
            uint systemClass = VfxParsingHash.Fnv1a("VfxSystemDefinitionData");
            uint[] lists = { VfxParsingHash.Fnv1a("complexEmitterDefinitionData"), VfxParsingHash.Fnv1a("simpleEmitterDefinitionData") };
            uint primitiveField = VfxParsingHash.Fnv1a("primitive");
            uint nameField = VfxParsingHash.Fnv1a("emitterName");

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

            string final = Path.Combine(install, @"Game\DATA\FINAL");
            var wads = new List<WadFile>();
            var where = new Dictionary<ulong, WadFile>();
            foreach (string wadPath in Directory.GetFiles(final, "*.wad.client", SearchOption.AllDirectories)
                         .Where(path => path.Contains(@"\Champions\") || path.Contains(@"\Maps\"))
                         .Where(path => !Path.GetFileName(path)[..^".wad.client".Length].Contains('.')))
            {
                var wad = new WadFile(wadPath);
                wads.Add(wad);
                foreach (ulong hash in wad.Chunks.Keys)
                    if (paths.ContainsKey(hash)) where.TryAdd(hash, wad);
            }

            var tally = new Dictionary<string, (int Emitters, HashSet<uint> Systems, string Example)>(StringComparer.Ordinal);
            int emittersSeen = 0;
            foreach ((ulong hash, string path) in paths.Where(pair => where.ContainsKey(pair.Key) &&
                         (System.Text.RegularExpressions.Regex.IsMatch(pair.Value, @"^data/characters/[^/]+/skins/skin\d+\.bin$") ||
                          pair.Value.EndsWith(".materials.bin", StringComparison.Ordinal))))
            {
                BinTree tree;
                try
                {
                    using var data = where[hash].LoadChunkDecompressed(hash);
                    using var stream = new MemoryStream(data.Span.ToArray(), writable: false);
                    tree = new BinTree(stream);
                }
                catch { continue; }

                foreach (BinTreeObject system in tree.Objects.Values.Where(item => item.ClassHash == systemClass))
                {
                    foreach (uint list in lists)
                    {
                        if (!system.Properties.TryGetValue(list, out BinTreeProperty held) || held is not BinTreeContainer container) continue;
                        foreach (BinTreeStruct emitter in container.Elements.OfType<BinTreeStruct>())
                        {
                            emittersSeen++;
                            string primitive = emitter.Properties.TryGetValue(primitiveField, out BinTreeProperty prim) && prim is BinTreeStruct ps && ps.ClassHash != 0
                                ? names.GetValueOrDefault(ps.ClassHash, $"0x{ps.ClassHash:x8}")
                                : "VfxPrimitiveCameraQuad (none)";
                            var found = new List<(string Where, string Value)>();
                            Find(emitter, "emitter", target, names, found, 0);
                            if (found.Count == 0) continue;
                            string extra = string.Join(" ", with.Select(field =>
                                $"{field}={(emitter.Properties.TryGetValue(VfxParsingHash.Fnv1a(field), out BinTreeProperty other) ? Format(other) : "-")}"));
                            string label = emitter.Properties.TryGetValue(nameField, out BinTreeProperty nameProperty) ? Format(nameProperty) : "?";
                            foreach ((string location, string value) in found)
                            {
                                string key = $"{primitive} | {location} | {value}{(extra.Length > 0 ? " | " + extra : string.Empty)}";
                                if (!tally.TryGetValue(key, out var entry))
                                    entry = (0, new HashSet<uint>(), $"{path} 0x{system.PathHash:x8} {label}");
                                entry.Emitters++;
                                entry.Systems.Add(system.PathHash);
                                tally[key] = entry;
                            }
                        }
                    }
                }
            }

            Console.WriteLine($"[FieldValues] {args[0]} (0x{target:x8}) over {emittersSeen} emitters: found in {tally.Values.Sum(entry => entry.Emitters)}");
            foreach ((string key, var entry) in tally.OrderByDescending(pair => pair.Value.Emitters).Take(60))
                Console.WriteLine($"[FieldValues] {entry.Emitters,6} emitters {entry.Systems.Count,6} systems  {key}  e.g. {entry.Example}");
            if (args.SkipWhile(arg => arg != "--below").Skip(1).FirstOrDefault() is { } belowText &&
                float.TryParse(belowText, NumberStyles.Float, CultureInfo.InvariantCulture, out float below))
            {
                // A scalar on the emitter itself below the threshold, whatever its primitive.
                var low = tally.Where(pair =>
                {
                    string[] parts = pair.Key.Split(" | ");
                    return parts.Length >= 3 && parts[1] == "emitter" &&
                           float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && value < below;
                }).ToArray();
                Console.WriteLine($"[FieldValues] below {below}: {low.Sum(pair => pair.Value.Emitters)} emitters in {low.SelectMany(pair => pair.Value.Systems).Distinct().Count()} systems");
                foreach ((string key, var entry) in low.OrderByDescending(pair => pair.Value.Emitters).Take(15))
                    Console.WriteLine($"[FieldValues]   {entry.Emitters,6} emitters  {key}  e.g. {entry.Example}");
            }
            foreach (WadFile wad in wads) wad.Dispose();
        }

        private static void Find(BinTreeStruct node, string location, uint target, Dictionary<uint, string> names, List<(string, string)> found, int depth)
        {
            foreach ((uint key, BinTreeProperty property) in node.Properties)
            {
                if (key == target)
                    found.Add((location, Format(property)));
                if (depth < 3 && property is BinTreeStruct child)
                    Find(child, location + "/" + names.GetValueOrDefault(child.ClassHash, $"0x{child.ClassHash:x8}"), target, names, found, depth + 1);
            }
        }

        private static string Format(BinTreeProperty property)
        {
            if (property is BinTreeOptional optional) return optional.Value is null ? "none" : Format(optional.Value);
            if (property is BinTreeStruct structure) return $"struct 0x{structure.ClassHash:x8}";
            if (property is BinTreeContainer container) return $"list[{container.Elements.Count}]";
            object value = property.GetType().GetProperty("Value")?.GetValue(property);
            return value switch
            {
                null => property.GetType().Name,
                float number => number.ToString("0.###", CultureInfo.InvariantCulture),
                System.Numerics.Vector2 v => $"({v.X.ToString(CultureInfo.InvariantCulture)},{v.Y.ToString(CultureInfo.InvariantCulture)})",
                System.Numerics.Vector3 v => $"({v.X.ToString(CultureInfo.InvariantCulture)},{v.Y.ToString(CultureInfo.InvariantCulture)},{v.Z.ToString(CultureInfo.InvariantCulture)})",
                _ => value.ToString()
            };
        }
    }
}
