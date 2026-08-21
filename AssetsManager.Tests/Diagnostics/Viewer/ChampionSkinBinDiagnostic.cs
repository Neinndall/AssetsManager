using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Resolvers;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// Opt-in diagnostic for comparing champion skin BIN material layouts.
    /// It is intentionally not an automatic test because it reads user-provided paths.
    /// </summary>
    internal static class ChampionSkinBinDiagnostic
    {
        private static readonly uint SkinPropertiesClass = Fnv1a.HashLower("SkinCharacterDataProperties");
        private static readonly uint StaticMaterialClass = Fnv1a.HashLower("StaticMaterialDef");
        private static readonly uint SkinMeshProperties = Fnv1a.HashLower("skinMeshProperties");
        private static readonly uint SimpleSkin = Fnv1a.HashLower("simpleSkin");
        private static readonly uint Texture = Fnv1a.HashLower("texture");
        private static readonly uint MaterialOverride = Fnv1a.HashLower("materialOverride");
        private static readonly uint Submesh = Fnv1a.HashLower("submesh");
        private static readonly uint Material = Fnv1a.HashLower("Material");
        private static readonly uint SamplerValues = Fnv1a.HashLower("samplerValues");
        private static readonly uint TextureName = Fnv1a.HashLower("textureName");
        private static readonly uint SamplerName = Fnv1a.HashLower("samplerName");
        private static readonly uint TexturePath = Fnv1a.HashLower("texturePath");
        private static readonly uint ParamValues = Fnv1a.HashLower("paramValues");
        private static readonly uint ParameterName = Fnv1a.HashLower("name");
        private static readonly uint ParameterValue = Fnv1a.HashLower("value");
        private static readonly uint Techniques = Fnv1a.HashLower("techniques");
        private static readonly uint Passes = Fnv1a.HashLower("passes");
        private static readonly uint Shader = Fnv1a.HashLower("shader");

        private static readonly Dictionary<uint, string> InterestingFields = new()
        {
            [Fnv1a.HashLower("sample")] = "sample",
            [Fnv1a.HashLower("sampler")] = "sampler",
            [SamplerValues] = "samplerValues",
            [Texture] = "texture",
            [TextureName] = "textureName",
            [TexturePath] = "texturePath",
            [MaterialOverride] = "materialOverride",
        };

        public static void Run(string[] paths)
        {
            if (paths.Length == 0)
            {
                Console.WriteLine("Usage: champion-bin-audit <skin0.bin> [skin0.bin ...]");
                return;
            }

            foreach (string path in paths)
            {
                Audit(Path.GetFullPath(path));
            }
        }

        private static void Audit(string path)
        {
            Console.WriteLine($"\n[ChampionBin] {path}");
            if (!File.Exists(path))
            {
                Console.WriteLine("  FILE NOT FOUND");
                return;
            }

            using var stream = File.OpenRead(path);
            var tree = new BinTree(stream);
            Console.WriteLine($"  Objects={tree.Objects.Count} Dependencies={tree.Dependencies.Count}");

            foreach (BinTreeObject obj in tree.Objects.Values)
            {
                if (obj.ClassHash == SkinPropertiesClass)
                {
                    PrintSkin(obj);
                }
                else if (obj.ClassHash == StaticMaterialClass)
                {
                    PrintMaterial(obj);
                }
            }

            var fieldCounts = new Dictionary<uint, int>();
            foreach (BinTreeObject obj in tree.Objects.Values)
            {
                CollectFieldCounts(obj.Properties.Values, fieldCounts);
            }

            Console.WriteLine("  Fields:");
            foreach ((uint hash, string name) in InterestingFields.OrderBy(item => item.Value))
            {
                fieldCounts.TryGetValue(hash, out int count);
                Console.WriteLine($"    {name}={count}");
            }

            SknMaterialTextureMetadata metadata = SknMaterialTextureResolver.ReadMetadata(tree);
            string[] textureKeys = metadata.ReferencedTexturePaths
                .Select(path => Path.GetFileNameWithoutExtension(path)?.Split('.')[0])
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToArray();
            SknMaterialTextureResolution resolution =
                SknMaterialTextureResolver.Resolve(metadata, textureKeys);
            Console.WriteLine($"  ResolvedEffects={resolution.Effects.Count}");
            foreach (var (submesh, effect) in resolution.Effects)
            {
                Console.WriteLine($"    {submesh}: {effect.Kind}");
            }
        }

        private static void PrintSkin(BinTreeObject obj)
        {
            if (!TryGetStruct(obj.Properties, SkinMeshProperties, out BinTreeStruct mesh))
            {
                Console.WriteLine("  Skin: skinMeshProperties=<missing>");
                return;
            }

            Console.WriteLine("  Skin:");
            Console.WriteLine($"    simpleSkin={GetString(mesh.Properties, SimpleSkin) ?? "<missing>"}");
            Console.WriteLine($"    texture={GetString(mesh.Properties, Texture) ?? "<missing>"}");
            if (!TryGetContainer(mesh.Properties, MaterialOverride, out BinTreeContainer overrides))
            {
                Console.WriteLine("    materialOverride=<missing>");
                return;
            }

            Console.WriteLine($"    materialOverride={overrides.Elements.Count}");
            foreach (BinTreeStruct entry in overrides.Elements.OfType<BinTreeStruct>())
            {
                Console.WriteLine(
                    $"      {GetString(entry.Properties, Submesh) ?? "<missing>"}: " +
                    $"texture={GetString(entry.Properties, Texture) ?? "<none>"}, " +
                    $"Material={GetLink(entry.Properties, Material) ?? "<none>"}");
            }
        }

        private static void PrintMaterial(BinTreeObject obj)
        {
            if (!TryGetContainer(obj.Properties, SamplerValues, out BinTreeContainer samplers))
            {
                Console.WriteLine("  Material: samplerValues=<missing>");
                return;
            }

            Console.WriteLine($"  Material: samplerValues={samplers.Elements.Count}");
            foreach (BinTreeStruct sampler in samplers.Elements.OfType<BinTreeStruct>())
            {
                Console.WriteLine(
                    $"    {GetString(sampler.Properties, TextureName) ?? "<missing>"}" +
                    $" ({GetString(sampler.Properties, SamplerName) ?? "<no samplerName"}) => " +
                    $"{GetString(sampler.Properties, TexturePath) ?? "<missing>"}");
            }

            if (TryGetContainer(obj.Properties, ParamValues, out BinTreeContainer parameters))
            {
                Console.WriteLine($"    paramValues={parameters.Elements.Count}");
                foreach (BinTreeStruct parameter in parameters.Elements.OfType<BinTreeStruct>())
                {
                    Console.WriteLine(
                        $"      {GetString(parameter.Properties, ParameterName) ?? "<missing>"}=" +
                        $"{GetValue(parameter.Properties, ParameterValue)}");
                }
            }

            uint shaderHash = ReadShaderHash(obj.Properties);
            if (shaderHash != 0)
            {
                Console.WriteLine($"    shader=0x{shaderHash:x8}");
            }
        }

        private static bool TryGetStruct(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint hash,
            out BinTreeStruct value)
        {
            if (properties.TryGetValue(hash, out BinTreeProperty property) && property is BinTreeStruct result)
            {
                value = result;
                return true;
            }

            value = null;
            return false;
        }

        private static bool TryGetContainer(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint hash,
            out BinTreeContainer value)
        {
            if (properties.TryGetValue(hash, out BinTreeProperty property) && property is BinTreeContainer result)
            {
                value = result;
                return true;
            }

            value = null;
            return false;
        }

        private static string GetString(IReadOnlyDictionary<uint, BinTreeProperty> properties, uint hash) =>
            properties.TryGetValue(hash, out BinTreeProperty property) && property is BinTreeString value
                ? value.Value
                : null;

        private static string GetLink(IReadOnlyDictionary<uint, BinTreeProperty> properties, uint hash) =>
            properties.TryGetValue(hash, out BinTreeProperty property) && property is BinTreeObjectLink value
                ? $"0x{value.Value:x16}"
                : null;

        private static string GetValue(IReadOnlyDictionary<uint, BinTreeProperty> properties, uint hash)
        {
            if (!properties.TryGetValue(hash, out BinTreeProperty property)) return "<missing>";

            return property switch
            {
                BinTreeVector2 value => $"<{Format(value.Value.X)},{Format(value.Value.Y)}>",
                BinTreeVector3 value => $"<{Format(value.Value.X)},{Format(value.Value.Y)},{Format(value.Value.Z)}>",
                BinTreeVector4 value => $"<{Format(value.Value.X)},{Format(value.Value.Y)},{Format(value.Value.Z)},{Format(value.Value.W)}>",
                BinTreeString value => $"\"{value.Value}\"",
                _ => Convert.ToString(property.GetType().GetProperty("Value")?.GetValue(property), CultureInfo.InvariantCulture)
                     ?? property.Type.ToString()
            };
        }

        private static string Format(float value) =>
            value.ToString("G6", CultureInfo.InvariantCulture);

        private static uint ReadShaderHash(IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            if (!TryGetContainer(properties, Techniques, out BinTreeContainer techniques)) return 0;
            BinTreeStruct technique = techniques.Elements.OfType<BinTreeStruct>().FirstOrDefault();
            if (technique == null || !TryGetContainer(technique.Properties, Passes, out BinTreeContainer passes)) return 0;
            BinTreeStruct pass = passes.Elements.OfType<BinTreeStruct>().FirstOrDefault();
            return pass != null && pass.Properties.TryGetValue(Shader, out BinTreeProperty property) &&
                   property is BinTreeObjectLink link
                ? link.Value
                : 0;
        }

        private static void CollectFieldCounts(
            IEnumerable<BinTreeProperty> properties,
            Dictionary<uint, int> counts)
        {
            foreach (BinTreeProperty property in properties)
            {
                counts[property.NameHash] = counts.TryGetValue(property.NameHash, out int count)
                    ? count + 1
                    : 1;

                switch (property)
                {
                    case BinTreeStruct structure:
                        CollectFieldCounts(structure.Properties.Values, counts);
                        break;
                    case BinTreeContainer container:
                        CollectFieldCounts(container.Elements, counts);
                        break;
                    case BinTreeMap map:
                        foreach (var pair in map)
                        {
                            if (pair.Key is BinTreeProperty key) CollectFieldCounts(new[] { key }, counts);
                            if (pair.Value is BinTreeProperty value) CollectFieldCounts(new[] { value }, counts);
                        }
                        break;
                    case BinTreeOptional optional when optional.Value is not null:
                        CollectFieldCounts(new[] { optional.Value }, counts);
                        break;
                }
            }
        }
    }
}
