using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetsManager.Utils;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace BenchmarkApp.Diagnostics.Viewer
{
    /// <summary>
    /// Audits real champion skin BINs directly from game WADs and reports the
    /// material graph consumed by the model viewer.
    /// </summary>
    internal static class ChampionSkinBinStructureDiagnostic
    {
        private static readonly uint SkinPropertiesClass = LeagueToolkit.Hashing.Fnv1a.HashLower("SkinCharacterDataProperties");
        private static readonly uint StaticMaterialClass = LeagueToolkit.Hashing.Fnv1a.HashLower("StaticMaterialDef");
        private static readonly uint SkinMeshProperties = LeagueToolkit.Hashing.Fnv1a.HashLower("skinMeshProperties");
        private static readonly uint SimpleSkin = LeagueToolkit.Hashing.Fnv1a.HashLower("simpleSkin");
        private static readonly uint Texture = LeagueToolkit.Hashing.Fnv1a.HashLower("texture");
        private static readonly uint MaterialOverride = LeagueToolkit.Hashing.Fnv1a.HashLower("materialOverride");
        private static readonly uint Submesh = LeagueToolkit.Hashing.Fnv1a.HashLower("submesh");
        private static readonly uint Material = LeagueToolkit.Hashing.Fnv1a.HashLower("Material");
        private static readonly uint SamplerValues = LeagueToolkit.Hashing.Fnv1a.HashLower("samplerValues");
        private static readonly uint TextureName = LeagueToolkit.Hashing.Fnv1a.HashLower("textureName");
        private static readonly uint SamplerName = LeagueToolkit.Hashing.Fnv1a.HashLower("samplerName");
        private static readonly uint TexturePath = LeagueToolkit.Hashing.Fnv1a.HashLower("texturePath");
        private static readonly uint ParamValues = LeagueToolkit.Hashing.Fnv1a.HashLower("paramValues");
        private static readonly uint ParameterName = LeagueToolkit.Hashing.Fnv1a.HashLower("name");
        private static readonly uint ParameterValue = LeagueToolkit.Hashing.Fnv1a.HashLower("value");
        private static readonly uint Techniques = LeagueToolkit.Hashing.Fnv1a.HashLower("techniques");
        private static readonly uint Passes = LeagueToolkit.Hashing.Fnv1a.HashLower("passes");
        private static readonly uint Shader = LeagueToolkit.Hashing.Fnv1a.HashLower("shader");

        private static readonly HashSet<string> KnownMaterialFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "samplerValues", "textureName", "samplerName", "texturePath", "paramValues", "name", "value",
            "techniques", "passes", "shader"
        };

        public static void Run(
            string rootPath,
            string hashesPath,
            string filter = null,
            int maxBins = 12)
        {
            Console.WriteLine("=== CHAMPION SKIN BIN STRUCTURE AUDIT ===");
            Console.WriteLine($"Root: {rootPath}");
            Console.WriteLine($"Hashes: {hashesPath}");
            Console.WriteLine($"Filter: {filter ?? "<all champion skins>"}  MaxBins: {maxBins}");

            string gamePath = Path.Combine(rootPath, "Game");
            if (!Directory.Exists(gamePath))
            {
                Console.WriteLine($"ERROR: game directory not found: {gamePath}");
                return;
            }

            var paths = LoadPaths(Path.Combine(hashesPath, "hashes.game.txt"));
            Console.WriteLine($"Resolved game paths: {paths.Count}");

            string[] wads = Directory.EnumerateFiles(gamePath, "*.wad.client", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Console.WriteLine($"Game WADs: {wads.Length}");

            int discovered = 0;
            int parsed = 0;
            int failed = 0;
            var classCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var samplerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var parameterCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var parameterTypeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var seenLogicalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string wadPath in wads)
            {
                if (parsed >= maxBins) break;

                try
                {
                    using var wad = new LeagueToolkit.Core.Wad.WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        if (parsed >= maxBins) break;
                        if (!paths.TryGetValue(pair.Key, out string logicalPath) ||
                            !IsChampionSkinBin(logicalPath) ||
                            (filter != null && !logicalPath.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        discovered++;
                        if (!seenLogicalPaths.Add(logicalPath))
                        {
                            continue;
                        }

                        try
                        {
                            using var owner = wad.LoadChunkDecompressed(pair.Value);
                            ArraySegment<byte> buffer = owner.DangerousGetArray();
                            using var stream = new MemoryStream(buffer.Array, buffer.Offset, buffer.Count, false);
                            var tree = new BinTree(stream);
                            parsed++;
                            AuditBin(
                                Path.GetFileName(wadPath),
                                logicalPath,
                                tree,
                                wad,
                                paths,
                                classCounts,
                                samplerCounts,
                                parameterCounts,
                                parameterTypeCounts);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            failed++;
                            Console.WriteLine($"  ! PARSE FAILED {logicalPath}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine($"  ! WAD FAILED {Path.GetFileName(wadPath)}: {ex.Message}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Discovered chunks: {discovered}; unique parsed BINs: {parsed}; failures: {failed}");
            PrintCounts("Material samplers", samplerCounts);
            PrintCounts("Material parameters", parameterCounts);
            PrintCounts("Parameter value types", parameterTypeCounts);
            Console.WriteLine("Class hashes found:");
            foreach ((string name, int count) in classCounts.OrderByDescending(item => item.Value).ThenBy(item => item.Key))
            {
                Console.WriteLine($"  {name}: {count}");
            }
        }

        private static void AuditBin(
            string wadName,
            string logicalPath,
            BinTree tree,
            LeagueToolkit.Core.Wad.WadFile wad,
            IReadOnlyDictionary<ulong, string> paths,
            Dictionary<string, int> classCounts,
            Dictionary<string, int> samplerCounts,
            Dictionary<string, int> parameterCounts,
            Dictionary<string, int> parameterTypeCounts)
        {
            int skinCount = tree.Objects.Values.Count(obj => obj.ClassHash == SkinPropertiesClass);
            int materialCount = tree.Objects.Values.Count(obj => obj.ClassHash == StaticMaterialClass);
            Increment(classCounts, "SkinCharacterDataProperties", skinCount);
            Increment(classCounts, "StaticMaterialDef", materialCount);

            Console.WriteLine();
            Console.WriteLine($"[BIN] {logicalPath}  WAD={wadName}  objects={tree.Objects.Count} dependencies={tree.Dependencies.Count}");
            Console.WriteLine($"  SkinCharacterDataProperties={skinCount} StaticMaterialDef={materialCount}");

            SknMaterialTextureMetadata metadata = SknMaterialTextureResolver.ReadMetadata(tree);
            string[] availableTextureKeys = metadata.ReferencedTexturePaths
                .Select(path => PathUtils.TruncateAtDot(Path.GetFileNameWithoutExtension(path)))
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SknMaterialTextureResolution resolution =
                SknMaterialTextureResolver.Resolve(metadata, availableTextureKeys);
            Console.WriteLine(
                $"  Viewer resolver: default={resolution.DefaultTextureKey ?? "<none>"} " +
                $"overrides={resolution.Overrides.Count} effects={resolution.Effects.Count}");
            foreach ((string submesh, ModelMaterialEffectDefinition effect) in resolution.Effects)
            {
                Console.WriteLine(
                    $"    effect submesh={submesh} kind={effect.Kind} " +
                    $"texture={effect.TextureName ?? "<none>"} mask={effect.MaskTextureName ?? "<none>"} " +
                    $"emissionTexture={effect.EmissionTextureName ?? "<none>"} " +
                    $"emissionMask={effect.EmissionMaskTextureName ?? "<none>"} " +
                    $"emissionChannel={effect.EmissionChannel}");
            }
            foreach ((string submesh, string texture) in resolution.Overrides)
            {
                Console.WriteLine($"    textureOverride submesh={submesh} texture={texture}");
            }

            var materials = tree.Objects
                .Where(pair => pair.Value.ClassHash == StaticMaterialClass)
                .ToDictionary(pair => pair.Key, pair => pair.Value);

            foreach (BinTreeObject skin in tree.Objects.Values.Where(obj => obj.ClassHash == SkinPropertiesClass))
            {
                if (!TryGetStruct(skin.Properties, SkinMeshProperties, out BinTreeStruct mesh))
                {
                    Console.WriteLine("  Skin: skinMeshProperties=<missing>");
                    continue;
                }

                string simpleSkinPath = GetString(mesh.Properties, SimpleSkin);
                Console.WriteLine(
                    $"  Skin simpleSkin={simpleSkinPath ?? "<missing>"} " +
                    $"defaultTexture={GetString(mesh.Properties, Texture) ?? "<missing>"} " +
                    $"material={GetLink(mesh.Properties, Material) ?? "<none>"}");
                IReadOnlyList<string> meshRanges = PrintSimpleSkinRanges(wad, paths, simpleSkinPath);
                PrintEffectiveMeshTextureAssignments(meshRanges, resolution);
                string binSkinToken = GetSkinToken(logicalPath);
                string modelSkinToken = GetSkinToken(simpleSkinPath);
                if (binSkinToken != null && modelSkinToken != null &&
                    !binSkinToken.Equals(modelSkinToken, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(
                        $"  WARNING: BIN skin token {binSkinToken} references model token {modelSkinToken}; " +
                        "resolving material data from the model path alone may select the wrong BIN.");
                }
                if (!TryGetContainer(mesh.Properties, MaterialOverride, out BinTreeContainer overrides))
                {
                    Console.WriteLine("    materialOverride=<missing>");
                    continue;
                }

                Console.WriteLine($"    materialOverride entries={overrides.Elements.Count}");
                foreach (BinTreeStruct entry in overrides.Elements.OfType<BinTreeStruct>())
                {
                    string submesh = GetString(entry.Properties, Submesh) ?? "<missing>";
                    string directTexture = GetString(entry.Properties, Texture) ?? "<none>";
                    string materialLink = GetLink(entry.Properties, Material);
                    Console.WriteLine($"      submesh={submesh} texture={directTexture} material={materialLink ?? "<none>"}");

                    if (entry.Properties.TryGetValue(Material, out BinTreeProperty linkProperty) &&
                        linkProperty is BinTreeObjectLink link &&
                        materials.TryGetValue(link.Value, out BinTreeObject materialObject))
                    {
                        PrintMaterial(materialObject, samplerCounts, parameterCounts, parameterTypeCounts);
                    }
                }
            }

            if (skinCount == 0 || materialCount == 0)
            {
                Console.WriteLine("  NOTE: BIN does not expose the expected skin/material object classes.");
            }
        }

        private static IReadOnlyList<string> PrintSimpleSkinRanges(
            LeagueToolkit.Core.Wad.WadFile wad,
            IReadOnlyDictionary<ulong, string> paths,
            string simpleSkinPath)
        {
            if (string.IsNullOrWhiteSpace(simpleSkinPath)) return Array.Empty<string>();

            string normalizedTarget = simpleSkinPath.Replace('\\', '/');
            (ulong pathHash, string logicalPath) = paths
                .FirstOrDefault(pair => pair.Value.Replace('\\', '/').Equals(
                    normalizedTarget,
                    StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(logicalPath) || !wad.Chunks.TryGetValue(pathHash, out var chunk))
            {
                Console.WriteLine("    simpleSkin ranges=<chunk not found>");
                return Array.Empty<string>();
            }

            try
            {
                using var owner = wad.LoadChunkDecompressed(chunk);
                ArraySegment<byte> buffer = owner.DangerousGetArray();
                using var stream = new MemoryStream(buffer.Array, buffer.Offset, buffer.Count, false);
                using SkinnedMesh skinnedMesh = SkinnedMesh.ReadFromSimpleSkin(stream, leaveOpen: false);
                Console.WriteLine($"    simpleSkin ranges={skinnedMesh.Ranges.Count}");
                string[] ranges = skinnedMesh.Ranges
                    .Select(range => range.Material.TrimEnd('\0'))
                    .ToArray();
                foreach (string range in ranges)
                {
                    Console.WriteLine($"      range submesh={range}");
                }

                return ranges;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"    simpleSkin ranges=<parse failed: {ex.Message}>");
                return Array.Empty<string>();
            }
        }

        private static void PrintEffectiveMeshTextureAssignments(
            IReadOnlyList<string> meshRanges,
            SknMaterialTextureResolution resolution)
        {
            if (meshRanges == null || meshRanges.Count == 0 || resolution == null)
            {
                return;
            }

            Console.WriteLine("    effective viewer mesh-texture assignments:");
            foreach (string meshRange in meshRanges)
            {
                string materialKey = SknMaterialTextureResolver.NormalizeMaterialKey(meshRange);
                string overrideTexture = null;
                bool hasTextureOverride = resolution.Overrides != null &&
                                          resolution.Overrides.TryGetValue(materialKey, out overrideTexture);
                string texture = hasTextureOverride
                    ? overrideTexture
                    : resolution.DefaultTextureKey;
                string textureSource = hasTextureOverride ? $"override:{materialKey}" : "linked-material/default";
                string effect = resolution.Effects != null &&
                                resolution.Effects.TryGetValue(materialKey, out ModelMaterialEffectDefinition materialEffect)
                    ? $"override:{materialEffect.Kind}"
                    : resolution.DefaultEffect?.Kind != ModelMaterialEffectKind.None &&
                      resolution.MaterialOverrideKeys?.Contains(materialKey) != true
                        ? $"default:{resolution.DefaultEffect.Kind}"
                        : "<none>";

                Console.WriteLine(
                    $"      mesh={meshRange} binding={(hasTextureOverride ? materialKey : "<default-material>")} " +
                    $"texture={texture ?? "<none>"} source={textureSource} effect={effect}");
            }
        }

        private static void PrintMaterial(
            BinTreeObject material,
            Dictionary<string, int> samplerCounts,
            Dictionary<string, int> parameterCounts,
            Dictionary<string, int> parameterTypeCounts)
        {
            Console.WriteLine($"        StaticMaterialDef pathHash=0x{material.PathHash:x8} shader={ReadShaderHash(material.Properties)}");
            if (TryGetContainer(material.Properties, SamplerValues, out BinTreeContainer samplers))
            {
                Console.WriteLine($"          samplerValues={samplers.Elements.Count}");
                foreach (BinTreeStruct sampler in samplers.Elements.OfType<BinTreeStruct>())
                {
                    string textureName = GetString(sampler.Properties, TextureName) ?? "<missing>";
                    string samplerName = GetString(sampler.Properties, SamplerName) ?? "<missing>";
                    string texturePath = GetString(sampler.Properties, TexturePath) ?? "<missing>";
                    Console.WriteLine($"            textureName={textureName} samplerName={samplerName} texturePath={texturePath}");
                    Increment(samplerCounts, textureName);
                }
            }
            else
            {
                Console.WriteLine("          samplerValues=<missing>");
            }

            if (TryGetContainer(material.Properties, ParamValues, out BinTreeContainer parameters))
            {
                Console.WriteLine($"          paramValues={parameters.Elements.Count}");
                foreach (BinTreeStruct parameter in parameters.Elements.OfType<BinTreeStruct>())
                {
                    string name = GetString(parameter.Properties, ParameterName) ?? "<missing>";
                    string value = parameter.Properties.TryGetValue(ParameterValue, out BinTreeProperty property)
                        ? Describe(property)
                        : "<missing>";
                    Console.WriteLine($"            {name} [{property?.Type.ToString() ?? "missing"}] = {value}");
                    Increment(parameterCounts, name);
                    Increment(parameterTypeCounts, property?.Type.ToString() ?? "missing");
                }
            }
            else
            {
                Console.WriteLine("          paramValues=<missing>");
            }

            var unknownFields = material.Properties.Keys
                .Where(hash => !KnownMaterialFields.Contains(DescribeHash(hash)))
                .Select(hash => $"0x{hash:x8}")
                .ToArray();
            if (unknownFields.Length > 0)
            {
                Console.WriteLine($"          top-level fields not decoded by this audit: {string.Join(", ", unknownFields)}");
            }
        }

        private static string Describe(BinTreeProperty property) => property switch
        {
            BinTreeVector2 value => $"<{Format(value.Value.X)},{Format(value.Value.Y)}>",
            BinTreeVector3 value => $"<{Format(value.Value.X)},{Format(value.Value.Y)},{Format(value.Value.Z)}>",
            BinTreeVector4 value => $"<{Format(value.Value.X)},{Format(value.Value.Y)},{Format(value.Value.Z)},{Format(value.Value.W)}>",
            BinTreeString value => $"\"{value.Value}\"",
            BinTreeObjectLink value => $"link(0x{value.Value:x8})",
            BinTreeHash value => $"hash(0x{value.Value:x8})",
            BinTreeContainer value => $"container[{value.Elements.Count}]",
            BinTreeStruct value => $"struct(0x{value.ClassHash:x8}, fields={value.Properties.Count})",
            BinTreeMap value => $"map[{value.Count}]",
            BinTreeOptional value => value.Value == null ? "optional(null)" : $"optional({Describe(value.Value)})",
            _ => Convert.ToString(property.GetType().GetProperty("Value")?.GetValue(property), CultureInfo.InvariantCulture) ?? property.Type.ToString()
        };

        private static string DescribeHash(uint hash) => hash switch
        {
            _ when hash == SamplerValues => "samplerValues",
            _ when hash == TextureName => "textureName",
            _ when hash == SamplerName => "samplerName",
            _ when hash == TexturePath => "texturePath",
            _ when hash == ParamValues => "paramValues",
            _ when hash == ParameterName => "name",
            _ when hash == ParameterValue => "value",
            _ when hash == Techniques => "techniques",
            _ when hash == Passes => "passes",
            _ when hash == Shader => "shader",
            _ => $"0x{hash:x8}"
        };

        private static string ReadShaderHash(IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            if (!TryGetContainer(properties, Techniques, out BinTreeContainer techniques)) return "<missing>";
            BinTreeStruct technique = techniques.Elements.OfType<BinTreeStruct>().FirstOrDefault();
            if (technique == null || !TryGetContainer(technique.Properties, Passes, out BinTreeContainer passes))
            {
                return "<missing>";
            }

            BinTreeStruct pass = passes.Elements.OfType<BinTreeStruct>().FirstOrDefault();
            return pass != null && pass.Properties.TryGetValue(Shader, out BinTreeProperty property) &&
                   property is BinTreeObjectLink link
                ? $"0x{link.Value:x8}"
                : "<missing>";
        }

        private static bool IsChampionSkinBin(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string normalized = path.Replace('\\', '/');
            return normalized.StartsWith("data/characters/", StringComparison.OrdinalIgnoreCase) &&
                   normalized.Contains("/skins/skin", StringComparison.OrdinalIgnoreCase) &&
                   normalized.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetSkinToken(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string normalized = path.Replace('\\', '/');
            string marker = "/skins/skin";
            int markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) return null;
            int start = markerIndex + marker.Length;
            int end = start;
            while (end < normalized.Length && char.IsDigit(normalized[end])) end++;
            return end > start ? normalized[start..end] : null;
        }

        private static Dictionary<ulong, string> LoadPaths(string path)
        {
            var result = new Dictionary<ulong, string>();
            if (!File.Exists(path)) return result;

            foreach (string line in File.ReadLines(path))
            {
                if (line.Length <= 17 || line[16] != ' ' ||
                    !ulong.TryParse(line.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                {
                    continue;
                }

                result.TryAdd(hash, line[17..].Trim());
            }

            return result;
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
                ? $"0x{value.Value:x8}"
                : null;

        private static string Format(float value) => value.ToString("G6", CultureInfo.InvariantCulture);

        private static void Increment(Dictionary<string, int> counts, string key, int amount = 1)
        {
            if (string.IsNullOrWhiteSpace(key)) key = "<missing>";
            counts[key] = counts.GetValueOrDefault(key) + amount;
        }

        private static void PrintCounts(string title, Dictionary<string, int> counts)
        {
            Console.WriteLine($"{title}:");
            foreach ((string key, int count) in counts.OrderByDescending(item => item.Value).ThenBy(item => item.Key))
            {
                Console.WriteLine($"  {key}: {count}");
            }
        }
    }
}
