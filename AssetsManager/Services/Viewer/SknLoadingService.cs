using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Core.Renderer;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Toolkit;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer
{
    public class SknLoadingService
    {
        private readonly LogService _logService;
        private static readonly uint MaterialOverrideSubmeshHash = Fnv1a.HashLower("submesh");
        private static readonly uint MaterialOverrideTextureHash = Fnv1a.HashLower("texture");
        private static readonly uint MaterialOverrideMaterialHash = Fnv1a.HashLower("material");
        private static readonly uint SamplerValuesHash = Fnv1a.HashLower("samplerValues");
        private static readonly uint TextureNameHash = Fnv1a.HashLower("textureName");
        private static readonly uint SamplerNameHash = Fnv1a.HashLower("samplerName");
        private static readonly uint TexturePathHash = Fnv1a.HashLower("texturePath");
        private static readonly uint StaticMaterialDefHash = Fnv1a.HashLower("StaticMaterialDef");

        public SknLoadingService(LogService logService)
        {
            _logService = logService;
        }

        // Este método carga un modelo SKN y sus texturas desde una ruta de directorio de texturas personalizada (para chromas).
        public async Task<SceneModel> LoadModel(string filePath, string textureDirectoryPath)
        {
            try
            {
                SkinnedMesh skinnedMesh = SkinnedMesh.ReadFromSimpleSkin(filePath);
                if (string.IsNullOrEmpty(textureDirectoryPath) || !Directory.Exists(textureDirectoryPath))
                {
                    _logService.LogError("Invalid texture directory provided for chroma model.");
                    return null;
                }

                var loadedTextures = LoadTexturesFromDirectory(textureDirectoryPath);
                var materialTextureOverrides = LoadMaterialTextureOverrides(filePath, loadedTextures.Keys);

                _logService.LogDebug($"Loaded model (with custom textures): {Path.GetFileNameWithoutExtension(filePath)}");
                return await CreateSceneModel(skinnedMesh, loadedTextures, Path.GetFileNameWithoutExtension(filePath), materialTextureOverrides);
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to load model with custom textures");
                return null;
            }
        }

        // Este método carga un modelo SKN y sus texturas desde el mismo directorio del archivo SKN (comportamiento estándar).
        public async Task<SceneModel> LoadModel(string filePath)
        {
            try
            {
                SkinnedMesh skinnedMesh = SkinnedMesh.ReadFromSimpleSkin(filePath);
                string modelDirectory = Path.GetDirectoryName(filePath);

                if (string.IsNullOrEmpty(modelDirectory))
                {
                    _logService.LogError("Could not determine the model directory.");
                    return null;
                }

                var loadedTextures = LoadTexturesFromDirectory(modelDirectory);
                var materialTextureOverrides = LoadMaterialTextureOverrides(filePath, loadedTextures.Keys);

                _logService.LogDebug($"Loaded model: {Path.GetFileNameWithoutExtension(filePath)}");
                return await CreateSceneModel(skinnedMesh, loadedTextures, Path.GetFileNameWithoutExtension(filePath), materialTextureOverrides);
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to load model");
                return null;
            }
        }

        private Dictionary<string, BitmapSource> LoadTexturesFromDirectory(string directoryPath)
        {
            var loadedTextures = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
            var textureFiles = Directory.GetFiles(directoryPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

            foreach (string texPath in textureFiles)
            {
                try
                {
                    using (Stream fileStream = File.OpenRead(texPath))
                    {
                        BitmapSource loadedTex = TextureUtils.LoadViewerTexture(fileStream, Path.GetExtension(texPath));
                        if (loadedTex != null)
                        {
                            string textureKey = Path.GetFileNameWithoutExtension(texPath);
                            loadedTextures[textureKey] = loadedTex;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Failed to load texture file: {texPath}");
                }
            }
            return loadedTextures;
        }

        private async Task<SceneModel> CreateSceneModel(
            SkinnedMesh skinnedMesh,
            Dictionary<string, BitmapSource> loadedTextures,
            string modelName,
            IReadOnlyDictionary<string, string> materialTextureOverrides)
        {
            var availableTextureNames = new ObservableRangeCollection<string>(loadedTextures.Keys);
            var colorTextureKeys = TextureUtils.GetColorTextureCandidates(loadedTextures.Keys);

            string defaultTextureKey = colorTextureKeys
                .Where(k => k.EndsWith("_tx_cm", StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k.Length)
                .FirstOrDefault()
                ?? colorTextureKeys.FirstOrDefault();

            string skinName = modelName.Split('.')[0];

            // Move geometry processing to background thread
            var dataList = await Task.Run(() =>
            {
                var list = new List<SubmeshData>();
                var vertexAccessor = skinnedMesh.VerticesView.GetAccessor(VertexElement.POSITION.Name);
                var positions = vertexAccessor.AsVector3Array().ToArray();
                var texCoordAccessor = skinnedMesh.VerticesView.GetAccessor(VertexElement.TEXCOORD_0.Name);
                var texCoords = texCoordAccessor.AsVector2Array().ToArray();
                var indices = skinnedMesh.Indices;

                foreach (var rangeObj in skinnedMesh.Ranges)
                {
                    string materialName = rangeObj.Material.TrimEnd('\0');

                    var subIndices = indices.Slice(rangeObj.StartIndex, rangeObj.IndexCount);
                    var vertexMap = new Dictionary<int, int>();
                    var subPositions = new List<Point3D>();
                    var subTexCoords = new List<System.Windows.Point>();
                    var sourceVertexIndices = new List<int>();
                    var triangleIndices = new int[rangeObj.IndexCount];

                    for (int i = 0; i < rangeObj.IndexCount; i++)
                    {
                        int sourceIndex = (int)subIndices[i];
                        if (!vertexMap.TryGetValue(sourceIndex, out int localIndex))
                        {
                            var p = positions[sourceIndex];
                            var uv = texCoords[sourceIndex];

                            localIndex = subPositions.Count;
                            vertexMap[sourceIndex] = localIndex;
                            subPositions.Add(new Point3D(p.X, p.Y, p.Z));
                            subTexCoords.Add(new System.Windows.Point(uv.X, uv.Y));
                            sourceVertexIndices.Add(sourceIndex);
                        }

                        triangleIndices[i] = localIndex;
                    }

                    string initialMatchingKey = ResolveMaterialTexture(
                        materialName,
                        skinName,
                        colorTextureKeys,
                        defaultTextureKey,
                        materialTextureOverrides,
                        loadedTextures);

                    list.Add(new SubmeshData(
                        materialName,
                        subPositions.ToArray(),
                        triangleIndices,
                        subTexCoords.ToArray(),
                        sourceVertexIndices.ToArray(),
                        initialMatchingKey));
                }
                return list;
            });

            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var sceneModel = new SceneModel { Name = modelName, SkinnedMesh = skinnedMesh };
                _logService.LogDebug("--- Displaying Model ---");
                var parts = new List<ModelPart>();

                foreach (var data in dataList)
                {
                    var positionsCol = new Point3DCollection(data.Positions);
                    var indicesCol = new Int32Collection(data.TriangleIndices);
                    var texCoordsCol = new PointCollection(data.TextureCoordinates);

                    if (positionsCol.CanFreeze) positionsCol.Freeze();
                    if (indicesCol.CanFreeze) indicesCol.Freeze();
                    if (texCoordsCol.CanFreeze) texCoordsCol.Freeze();

                    MeshGeometry3D meshGeometry = new MeshGeometry3D
                    {
                        Positions = positionsCol,
                        TriangleIndices = indicesCol,
                        TextureCoordinates = texCoordsCol
                    };

                    var geometryModel = new GeometryModel3D(meshGeometry, new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Colors.Black)));

                    var modelPart = new ModelPart
                    {
                        Name = string.IsNullOrEmpty(data.MaterialName) ? "Default" : data.MaterialName,
                        Visual = new ModelVisual3D(),
                        SourceVertexIndices = data.SourceVertexIndices,
                        AllTextures = loadedTextures,
                        AvailableTextureNames = availableTextureNames,
                        SelectedTextureName = data.TexturePath,
                        Geometry = geometryModel
                    };

                    modelPart.Visual.Content = geometryModel;
                    TextureUtils.UpdateMaterial(modelPart);

                    parts.Add(modelPart);
                    sceneModel.RootVisual.Children.Add(modelPart.Visual);
                }

                sceneModel.Parts.AddRange(parts);
                _logService.LogDebug("--- Finished displaying model ---");
                return sceneModel;
            });
        }

        private string ResolveMaterialTexture(
            string materialName,
            string skinName,
            IReadOnlyList<string> colorTextureKeys,
            string defaultTextureKey,
            IReadOnlyDictionary<string, string> materialTextureOverrides,
            Dictionary<string, BitmapSource> loadedTextures)
        {
            string normalizedMaterialName = NormalizeMaterialKey(materialName);
            if (!string.IsNullOrEmpty(normalizedMaterialName) &&
                materialTextureOverrides != null &&
                materialTextureOverrides.TryGetValue(normalizedMaterialName, out string overrideTextureKey) &&
                loadedTextures.ContainsKey(overrideTextureKey))
            {
                _logService.LogDebug($"Found material-bin texture '{overrideTextureKey}' for submesh '{materialName}'.");
                return overrideTextureKey;
            }

            return TextureUtils.FindBestTextureMatch(materialName, skinName, colorTextureKeys, defaultTextureKey, _logService);
        }

        private Dictionary<string, string> LoadMaterialTextureOverrides(string sknPath, IEnumerable<string> loadedTextureKeys)
        {
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string skinBinPath = TryResolveSkinBinPath(sknPath);
            if (string.IsNullOrEmpty(skinBinPath) || !File.Exists(skinBinPath))
            {
                _logService.LogDebug($"No skin material bin found for '{Path.GetFileName(sknPath)}'. Texture matching will use heuristics.");
                return overrides;
            }

            try
            {
                using var stream = File.OpenRead(skinBinPath);
                var binTree = new BinTree(stream);
                var loadedTextureLookup = loadedTextureKeys
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(NormalizeTextureKey, key => key, StringComparer.OrdinalIgnoreCase);

                var materialTextureKeys = ResolveStaticMaterialTextures(binTree, loadedTextureLookup);
                foreach (var materialOverride in EnumerateMaterialOverrides(binTree))
                {
                    string textureKey = null;
                    if (!string.IsNullOrWhiteSpace(materialOverride.TexturePath))
                    {
                        loadedTextureLookup.TryGetValue(NormalizeTextureKey(materialOverride.TexturePath), out textureKey);
                    }

                    if (textureKey == null &&
                        materialOverride.MaterialHash != 0 &&
                        materialTextureKeys.TryGetValue(materialOverride.MaterialHash, out string materialTextureKey))
                    {
                        textureKey = materialTextureKey;
                    }

                    if (string.IsNullOrWhiteSpace(materialOverride.Submesh) || string.IsNullOrWhiteSpace(textureKey))
                    {
                        continue;
                    }

                    string submeshKey = NormalizeMaterialKey(materialOverride.Submesh);
                    overrides[submeshKey] = textureKey;
                    _logService.LogDebug($"Skin material bin maps submesh '{materialOverride.Submesh}' to texture '{textureKey}'.");
                }

                _logService.LogDebug($"Loaded {overrides.Count} submesh texture override(s) from '{Path.GetFileName(skinBinPath)}'.");
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to read skin material bin: {skinBinPath}");
            }

            return overrides;
        }

        private Dictionary<uint, string> ResolveStaticMaterialTextures(
            BinTree binTree,
            IReadOnlyDictionary<string, string> loadedTextureLookup)
        {
            var materialTextureKeys = new Dictionary<uint, string>();

            foreach (var pair in binTree.Objects)
            {
                BinTreeObject treeObject = pair.Value;
                if (treeObject.ClassHash != StaticMaterialDefHash)
                {
                    continue;
                }

                string textureKey = ResolveStaticMaterialTexture(treeObject, loadedTextureLookup);
                if (!string.IsNullOrWhiteSpace(textureKey))
                {
                    materialTextureKeys[treeObject.PathHash] = textureKey;
                }
            }

            return materialTextureKeys;
        }

        private string ResolveStaticMaterialTexture(
            BinTreeObject materialObject,
            IReadOnlyDictionary<string, string> loadedTextureLookup)
        {
            if (!materialObject.Properties.TryGetValue(SamplerValuesHash, out BinTreeProperty samplerValuesProperty) ||
                samplerValuesProperty is not BinTreeContainer samplerValues)
            {
                return null;
            }

            string fallbackTextureKey = null;
            foreach (BinTreeProperty samplerProperty in samplerValues.Elements)
            {
                if (samplerProperty is not BinTreeStruct sampler)
                {
                    continue;
                }

                string texturePath = GetStringProperty(sampler, TexturePathHash);
                if (string.IsNullOrWhiteSpace(texturePath))
                {
                    continue;
                }

                if (!loadedTextureLookup.TryGetValue(NormalizeTextureKey(texturePath), out string textureKey))
                {
                    continue;
                }

                string textureName = GetStringProperty(sampler, TextureNameHash);
                string samplerName = GetStringProperty(sampler, SamplerNameHash);
                if (IsDiffuseSampler(textureName) || IsDiffuseSampler(samplerName))
                {
                    return textureKey;
                }

                fallbackTextureKey ??= textureKey;
            }

            return fallbackTextureKey;
        }

        private IEnumerable<MaterialOverrideEntry> EnumerateMaterialOverrides(BinTree binTree)
        {
            foreach (BinTreeObject treeObject in binTree.Objects.Values)
            {
                foreach (BinTreeProperty property in treeObject.Properties.Values)
                {
                    foreach (MaterialOverrideEntry entry in EnumerateMaterialOverrides(property))
                    {
                        yield return entry;
                    }
                }
            }
        }

        private IEnumerable<MaterialOverrideEntry> EnumerateMaterialOverrides(BinTreeProperty property)
        {
            switch (property)
            {
                case BinTreeStruct structure:
                    MaterialOverrideEntry entry = TryReadMaterialOverride(structure);
                    if (entry != null)
                    {
                        yield return entry;
                    }

                    foreach (BinTreeProperty child in structure.Properties.Values)
                    {
                        foreach (MaterialOverrideEntry childEntry in EnumerateMaterialOverrides(child))
                        {
                            yield return childEntry;
                        }
                    }
                    break;

                case BinTreeContainer container:
                    foreach (BinTreeProperty child in container.Elements)
                    {
                        foreach (MaterialOverrideEntry childEntry in EnumerateMaterialOverrides(child))
                        {
                            yield return childEntry;
                        }
                    }
                    break;

                case BinTreeMap map:
                    foreach (var pair in map)
                    {
                        foreach (MaterialOverrideEntry childEntry in EnumerateMaterialOverrides(pair.Key))
                        {
                            yield return childEntry;
                        }

                        foreach (MaterialOverrideEntry childEntry in EnumerateMaterialOverrides(pair.Value))
                        {
                            yield return childEntry;
                        }
                    }
                    break;

                case BinTreeOptional optional when optional.Value != null:
                    foreach (MaterialOverrideEntry childEntry in EnumerateMaterialOverrides(optional.Value))
                    {
                        yield return childEntry;
                    }
                    break;
            }
        }

        private MaterialOverrideEntry TryReadMaterialOverride(BinTreeStruct structure)
        {
            string submesh = GetStringProperty(structure, MaterialOverrideSubmeshHash);
            if (string.IsNullOrWhiteSpace(submesh))
            {
                return null;
            }

            string texturePath = GetStringProperty(structure, MaterialOverrideTextureHash);
            uint materialHash = 0;
            if (structure.Properties.TryGetValue(MaterialOverrideMaterialHash, out BinTreeProperty materialProperty) &&
                materialProperty is BinTreeObjectLink materialLink)
            {
                materialHash = materialLink.Value;
            }

            return new MaterialOverrideEntry(submesh, texturePath, materialHash);
        }

        private static string GetStringProperty(BinTreeStruct structure, uint nameHash)
        {
            if (structure.Properties.TryGetValue(nameHash, out BinTreeProperty property) &&
                property is BinTreeString stringProperty)
            {
                return stringProperty.Value;
            }

            return null;
        }

        private static bool IsDiffuseSampler(string samplerName)
        {
            if (string.IsNullOrWhiteSpace(samplerName))
            {
                return false;
            }

            string lower = samplerName.ToLowerInvariant();
            if (lower.Contains("mask") ||
                lower.Contains("normal") ||
                lower.Contains("rough") ||
                lower.Contains("metal") ||
                lower.Contains("ao") ||
                lower.Contains("orm") ||
                lower.Contains("emissive") ||
                lower.Contains("emission") ||
                lower.Contains("glow"))
            {
                return false;
            }

            return lower.Contains("diffuse") ||
                   lower.Contains("color") ||
                   lower.Contains("base") ||
                   lower.Contains("albedo");
        }

        private static string TryResolveSkinBinPath(string sknPath)
        {
            if (string.IsNullOrWhiteSpace(sknPath))
            {
                return null;
            }

            string fullPath = Path.GetFullPath(sknPath);
            string normalizedPath = fullPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string marker = $"{Path.DirectorySeparatorChar}assets{Path.DirectorySeparatorChar}characters{Path.DirectorySeparatorChar}";
            int markerIndex = normalizedPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return null;
            }

            string rootPath = normalizedPath[..markerIndex];
            string relativePath = normalizedPath[(markerIndex + marker.Length)..];
            string[] parts = relativePath.Split(Path.DirectorySeparatorChar);
            if (parts.Length < 4 || !parts[1].Equals("skins", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string championName = parts[0];
            string skinFolder = parts[2];
            string skinBinName = GetSkinBinName(skinFolder);
            if (string.IsNullOrWhiteSpace(skinBinName))
            {
                return null;
            }

            return Path.Combine(rootPath, "data", "characters", championName, "skins", skinBinName);
        }

        private static string GetSkinBinName(string skinFolder)
        {
            if (string.IsNullOrWhiteSpace(skinFolder))
            {
                return null;
            }

            if (skinFolder.Equals("base", StringComparison.OrdinalIgnoreCase))
            {
                return "skin0.bin";
            }

            Match match = Regex.Match(skinFolder, @"^skin0*(\d+)$", RegexOptions.IgnoreCase);
            return match.Success ? $"skin{int.Parse(match.Groups[1].Value)}.bin" : null;
        }

        private static string NormalizeTextureKey(string texturePath)
        {
            if (string.IsNullOrWhiteSpace(texturePath))
            {
                return string.Empty;
            }

            return Path.GetFileNameWithoutExtension(texturePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar))
                .ToLowerInvariant();
        }

        private static string NormalizeMaterialKey(string materialName)
        {
            if (string.IsNullOrWhiteSpace(materialName))
            {
                return string.Empty;
            }

            string key = materialName.TrimEnd('\0').ToLowerInvariant();
            key = Regex.Replace(key, @"_?skn$", string.Empty, RegexOptions.IgnoreCase);
            return Regex.Replace(key, @"[^a-z0-9]", string.Empty);
        }

        private record SubmeshData(
            string MaterialName,
            Point3D[] Positions,
            int[] TriangleIndices,
            System.Windows.Point[] TextureCoordinates,
            int[] SourceVertexIndices,
            string TexturePath);

        private sealed record MaterialOverrideEntry(string Submesh, string TexturePath, uint MaterialHash);
    }
}
