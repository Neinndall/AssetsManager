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
    /// `dynamic-driver-census`: every driver class the dynamic materials of the installed skins use, how many
    /// parameters and skins rely on each, which the preview cannot evaluate yet, and the authored values of the
    /// time drivers (sine frequency, time loop, comparison operator) that decide how to evaluate them. Reads the
    /// skin BINs only, without meshes or textures.
    /// </summary>
    internal static class DynamicDriverCensusDiagnostic
    {
        private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
        {
            "Float4LiteralMaterialDriver", "FloatLiteralMaterialDriver", "LerpMaterialDriver", "LerpVec4LogicDriver",
            "SwitchMaterialDriver", "MaxMaterialDriver", "MinMaterialDriver", "FloatGraphMaterialDriver",
            "HasGearDynamicMaterialBoolDriver", "HasBuffDynamicMaterialBoolDriver", "IsDeadDynamicMaterialBoolDriver",
            "IsAnimationPlayingDynamicMaterialBoolDriver", "AllTrueMaterialDriver", "DelayedBoolMaterialDriver",
            "NotMaterialDriver", "SwitchMaterialDriverElement", "VfxAnimatedFloatVariableData"
        };

        private static Dictionary<uint, string> _names;
        // "op A B" of every FloatComparisonMaterialDriver, to read what each mOperator value means from its operands.
        private static readonly Dictionary<string, int> Comparisons = new(StringComparer.Ordinal);

        public static void Run(string[] args)
        {
            string install = InstalledSkins.FindInstall();
            if (install == null)
            {
                Console.WriteLine("[Census] No League install was found.");
                return;
            }

            _names = LoadNames();
            string hashes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes", "hashes.game.txt");
            var skinBins = new Dictionary<ulong, string>();
            var pattern = new System.Text.RegularExpressions.Regex(@"^data/characters/[a-z0-9_]+/skins/skin\d+\.bin$");
            foreach (string line in File.ReadLines(hashes))
            {
                int space = line.IndexOf(' ');
                if (space > 0 && pattern.IsMatch(line[(space + 1)..]) &&
                    ulong.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    skinBins[hash] = line[(space + 1)..];
            }

            var parameters = new Dictionary<string, int>(StringComparer.Ordinal);   // class -> parameters using it
            var skinsUsing = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var unresolvedParameters = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var values = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            int skins = 0, withDynamic = 0, totalParameters = 0, blockedParameters = 0, submeshSkins = 0;
            var submeshConditionKinds = new Dictionary<string, int>(StringComparer.Ordinal);
            var restingExamples = new List<string>();

            foreach (string wadPath in Directory.GetFiles(Path.Combine(install, @"Game\DATA\FINAL\Champions"), "*.wad.client")
                         .Where(path => !Path.GetFileName(path)[..^".wad.client".Length].Contains('.')))
            {
                using var wad = new WadFile(wadPath);
                foreach ((ulong hash, WadChunk chunk) in wad.Chunks)
                {
                    if (!skinBins.TryGetValue(hash, out string skin))
                        continue;
                    BinTree tree;
                    try
                    {
                        using var data = wad.LoadChunkDecompressed(chunk);
                        using var stream = new MemoryStream(data.Span.ToArray(), writable: false);
                        tree = new BinTree(stream);
                    }
                    catch
                    {
                        continue;
                    }

                    skins++;
                    // Persistent submesh conditions: how many skins author them, and which hold at rest.
                    var context = AssetsManager.Services.Viewer.Vfx.Parsing.VfxAnimationParser.ExtractOwnerSceneContext(tree);
                    if (context?.SubmeshConditions is { Count: > 0 } submeshConditions)
                    {
                        submeshSkins++;
                        foreach (var condition in submeshConditions)
                        {
                            bool? atRest = condition.Condition.Evaluate(AssetsManager.Views.Models.Viewer.GameMaterialState.Resting with { Gear = -1 });
                            string key = $"{condition.Condition.Kind}:{(atRest?.ToString() ?? "unknown")}";
                            submeshConditionKinds[key] = submeshConditionKinds.GetValueOrDefault(key) + 1;
                            if (atRest == true && restingExamples.Count < 12)
                                restingExamples.Add($"{skin.Split('/')[2]}/{Path.GetFileNameWithoutExtension(skin)} {condition.Condition.Kind}");
                        }
                    }
                    bool dynamic = false;
                    foreach (BinTreeObject material in tree.Objects.Values.Where(item => item.ClassHash == Fnv1a.HashLower("StaticMaterialDef")))
                    {
                        if (!material.Properties.TryGetValue(Fnv1a.HashLower("dynamicMaterial"), out BinTreeProperty dynamicProperty) ||
                            dynamicProperty is not BinTreeStruct dynamicMaterial ||
                            !dynamicMaterial.Properties.TryGetValue(Fnv1a.HashLower("parameters"), out BinTreeProperty list) ||
                            list is not BinTreeContainer container)
                            continue;
                        foreach (BinTreeStruct parameter in container.Elements.OfType<BinTreeStruct>())
                        {
                            if (!parameter.Properties.TryGetValue(Fnv1a.HashLower("driver"), out BinTreeProperty driver))
                                continue;
                            dynamic = true;
                            totalParameters++;
                            var classes = new HashSet<string>(StringComparer.Ordinal);
                            Walk(driver, classes, values);
                            foreach (string name in classes)
                            {
                                parameters[name] = parameters.GetValueOrDefault(name) + 1;
                                if (!skinsUsing.TryGetValue(name, out HashSet<string> owners))
                                    skinsUsing[name] = owners = new HashSet<string>(StringComparer.Ordinal);
                                owners.Add(skin);
                            }
                            string[] missing = classes.Where(name => !Supported.Contains(name)).ToArray();
                            if (missing.Length > 0)
                            {
                                blockedParameters++;
                                string parameterName = parameter.Properties.TryGetValue(Fnv1a.HashLower("name"), out BinTreeProperty nameProperty) && nameProperty is BinTreeString text ? text.Value : "?";
                                foreach (string name in missing)
                                {
                                    if (!unresolvedParameters.TryGetValue(name, out List<string> examples))
                                        unresolvedParameters[name] = examples = new List<string>();
                                    if (examples.Count < 6)
                                        examples.Add($"{skin.Split('/')[2]}/{Path.GetFileNameWithoutExtension(skin)}:{parameterName}");
                                }
                            }
                        }
                    }
                    if (dynamic)
                        withDynamic++;
                }
            }

            Console.WriteLine($"[Census] skins={skins} withDynamicMaterials={withDynamic} parameters={totalParameters} blockedByUnsupported={blockedParameters}");
            Console.WriteLine($"[CensusSubmesh] skins={submeshSkins} conditions={string.Join(" ", submeshConditionKinds.Select(pair => $"{pair.Key}x{pair.Value}"))}");
            foreach (string example in restingExamples)
                Console.WriteLine($"[CensusSubmesh] holds at rest: {example}");
            foreach ((string name, int count) in parameters.OrderByDescending(pair => pair.Value))
                Console.WriteLine($"[Census] {(Supported.Contains(name) ? "ok " : "NEW")} {name} parameters={count} skins={skinsUsing[name].Count}" +
                                  (unresolvedParameters.TryGetValue(name, out List<string> examples) ? $" e.g. {string.Join(", ", examples)}" : ""));
            foreach ((string field, Dictionary<string, int> histogram) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                Console.WriteLine($"[CensusValue] {field}: {string.Join(" ", histogram.OrderByDescending(pair => pair.Value).Take(15).Select(pair => $"{pair.Key}x{pair.Value}"))}");
            foreach ((string comparison, int count) in Comparisons.OrderByDescending(pair => pair.Value).Take(40))
                Console.WriteLine($"[CensusCompare] x{count} {comparison}");
        }

        // Collects every class under a driver, and the fields that decide how time drivers evaluate.
        private static void Walk(BinTreeProperty property, HashSet<string> classes, Dictionary<string, Dictionary<string, int>> values)
        {
            switch (property)
            {
                case BinTreeStruct structure:
                    string name = Name(structure.ClassHash);
                    classes.Add(name);
                    if (name == "FloatComparisonMaterialDriver")
                    {
                        string op = structure.Properties.TryGetValue(Fnv1a.HashLower("mOperator"), out BinTreeProperty opProperty) && opProperty is BinTreeU32 opValue ? opValue.Value.ToString(CultureInfo.InvariantCulture) : "0";
                        string key = $"op={op} A={Operand(structure, "mValueA")} B={Operand(structure, "mValueB")}";
                        Comparisons[key] = Comparisons.GetValueOrDefault(key) + 1;
                    }
                    foreach ((uint fieldHash, BinTreeProperty field) in structure.Properties)
                    {
                        string fieldName = Name(fieldHash);
                        string value = field switch
                        {
                            BinTreeF32 number => number.Value.ToString("0.###", CultureInfo.InvariantCulture),
                            BinTreeU32 number => number.Value.ToString(CultureInfo.InvariantCulture),
                            BinTreeBool flag => flag.Value.ToString(),
                            BinTreeOptional { Value: BinTreeF32 number } => number.Value.ToString("0.###", CultureInfo.InvariantCulture),
                            _ => null
                        };
                        if (value != null && !name.StartsWith("Lerp", StringComparison.Ordinal) && !Supported.Contains(name))
                        {
                            string key = $"{name}.{fieldName}";
                            if (!values.TryGetValue(key, out Dictionary<string, int> histogram))
                                values[key] = histogram = new Dictionary<string, int>(StringComparer.Ordinal);
                            histogram[value] = histogram.GetValueOrDefault(value) + 1;
                        }
                        Walk(field, classes, values);
                    }
                    break;
                case BinTreeContainer container:
                    foreach (BinTreeProperty element in container.Elements)
                        Walk(element, classes, values);
                    break;
                case BinTreeOptional { Value: BinTreeProperty inner }:
                    Walk(inner, classes, values);
                    break;
            }
        }

        private static string Operand(BinTreeStruct comparison, string field)
        {
            if (!comparison.Properties.TryGetValue(Fnv1a.HashLower(field), out BinTreeProperty property) || property is not BinTreeStruct operand)
                return "none";
            string name = Name(operand.ClassHash).Replace("MaterialDriver", "").Replace("DynamicMaterialFloatDriver", "");
            var details = operand.Properties.Values.Select(value => value switch
            {
                BinTreeF32 number => number.Value.ToString("0.###", CultureInfo.InvariantCulture),
                BinTreeString text => text.Value,
                BinTreeStruct inner => Name(inner.ClassHash).Replace("MaterialDriver", ""),
                _ => null
            }).Where(value => value != null);
            return $"{name}({string.Join(",", details)})";
        }

        private static string Name(uint hash) => _names.TryGetValue(hash, out string name) ? name : $"0x{hash:x8}";

        private static Dictionary<uint, string> LoadNames()
        {
            var names = new Dictionary<uint, string>();
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
            foreach (string file in new[] { "hashes.binfields.txt", "hashes.bintypes.txt" })
            {
                string path = Path.Combine(folder, file);
                if (!File.Exists(path)) continue;
                foreach (string line in File.ReadLines(path))
                {
                    int split = line.IndexOf(' ');
                    if (split > 0 && uint.TryParse(line.AsSpan(0, split), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
                        names.TryAdd(hash, line[(split + 1)..].Trim());
                }
            }
            return names;
        }
    }
}
