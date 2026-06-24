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
using System.Collections.Generic;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;


namespace AssetsManager.Services.Viewer
{
    public class SknLoadingService
    {
        private readonly LogService _logService;


        public SknLoadingService(LogService logService)
        {
            _logService = logService;
        }

        // Este método carga un modelo SKN y sus texturas desde una ruta de directorio de texturas personalizada (para chromas).
        public async Task<SceneModel> LoadModel(string filePath, string textureDirectoryPath)
            => await LoadModelInternal(filePath, textureDirectoryPath, "Failed to load model with custom textures");

        // Este método carga un modelo SKN y sus texturas desde el mismo directorio del archivo SKN (comportamiento estándar).
        public async Task<SceneModel> LoadModel(string filePath)
            => await LoadModelInternal(filePath, Path.GetDirectoryName(filePath), "Failed to load model");

        private async Task<SceneModel> LoadModelInternal(string filePath, string textureDirectoryPath, string failureMessage)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(textureDirectoryPath) || !Directory.Exists(textureDirectoryPath))
                {
                    _logService.LogError("Could not determine a valid texture directory for the model.");
                    return null;
                }

                SkinnedMesh skinnedMesh = SkinnedMesh.ReadFromSimpleSkin(filePath);
                string modelName = Path.GetFileNameWithoutExtension(filePath);
                var loadedTextures = LoadTexturesFromDirectory(textureDirectoryPath);
                var materialTextureOverrides = LoadMaterialTextureOverrides(filePath, loadedTextures.Keys);

                _logService.LogDebug($"Loaded model: {modelName}");
                return await CreateSceneModel(skinnedMesh, loadedTextures, modelName, materialTextureOverrides);
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, failureMessage);
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
            var availableTextureNames = new ObservableRangeCollection<string>(loadedTextures.Keys.Select(k => PathUtils.TruncateAtDot(k)));
            string skinName = modelName.Split('.')[0];
            var colorTextureKeys = TextureUtils.GetColorTextureCandidates(loadedTextures.Keys);

            string defaultTextureKey = colorTextureKeys
                .Where(k => k.IndexOf(skinName, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(k => {
                    int dotIndex = k.IndexOf('.');
                    string baseName = dotIndex > 0 ? k.Substring(0, dotIndex) : k;
                    return baseName.EndsWith("_tx_cm", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(k => {
                    int dotIndex = k.IndexOf('.');
                    return dotIndex > 0 ? dotIndex : k.Length;
                })
                .FirstOrDefault()
                ?? colorTextureKeys.FirstOrDefault();

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

                    bool needsAlpha =
                        modelPart.Geometry.Material is DiffuseMaterial dm &&
                        dm.Brush is ImageBrush ib &&
                        ib.ImageSource is BitmapSource bs &&
                        (bs.Format == PixelFormats.Bgra32 || bs.Format == PixelFormats.Pbgra32);

                    parts.Add(modelPart);

                    if (needsAlpha)
                        sceneModel.TransparentVisual.Children.Add(modelPart.Visual);
                    else
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

                var materialTextures = BuildMaterialTextureMap(binTree, loadedTextureKeys);
                if (materialTextures.Count == 0)
                {
                    _logService.LogDebug($"Skin material bin has no texture override objects, will use heuristics.");
                    return overrides;
                }

                foreach (var obj in binTree.Objects.Values)
                {
                    if (obj.ClassHash != 0x9B67E9F6) continue;
                    if (!obj.Properties.TryGetValue(0x45FF5904, out var rootProp) || rootProp is not BinTreeStruct rootStruct) continue;
                    if (!rootStruct.Properties.TryGetValue(0x24725910, out var groupsProp) || groupsProp is not BinTreeContainer groups) continue;

                    foreach (var element in groups.Elements)
                    {
                        if (element is not BinTreeStruct groupEntry) continue;

                        uint objLink = 0;
                        string groupName = null;
                        foreach (var prop in groupEntry.Properties)
                        {
                            if (prop.Value is BinTreeObjectLink link) objLink = link.Value;
                            else if (prop.Value is BinTreeString str) groupName = str.Value;
                        }

                        if (objLink == 0 || string.IsNullOrWhiteSpace(groupName)) continue;
                        if (!materialTextures.TryGetValue(objLink, out string textureKey)) continue;

                        string submeshKey = NormalizeMaterialKey(groupName);
                        overrides[submeshKey] = textureKey;
                        _logService.LogDebug($"Skin material bin maps material group '{groupName}' to texture '{textureKey}'.");
                    }
                }

                _logService.LogDebug($"Loaded {overrides.Count} submesh texture override(s) from '{Path.GetFileName(skinBinPath)}'.");
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to read skin material bin: {skinBinPath}");
            }

            return overrides;
        }

        private Dictionary<uint, string> BuildMaterialTextureMap(BinTree binTree, IEnumerable<string> loadedTextureKeys)
        {
            var result = new Dictionary<uint, string>();
            var textureKeyList = loadedTextureKeys.ToList();

            foreach (var kv in binTree.Objects)
            {
                if (kv.Value.ClassHash != 0xFF9D3409) continue;

                if (!kv.Value.Properties.TryGetValue(0x0A6F0EB5, out var texProp) || texProp is not BinTreeContainer texContainer)
                    continue;

                string diffusePath = null;
                string fallbackPath = null;

                foreach (var element in texContainer.Elements)
                {
                    if (element is not BinTreeStruct texEntry) continue;

                    string slotName = null;
                    string texPath = null;
                    foreach (var prop in texEntry.Properties)
                    {
                        if (prop.Value is BinTreeString str)
                        {
                            if (slotName == null) slotName = str.Value;
                            else texPath = str.Value;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(slotName) || string.IsNullOrWhiteSpace(texPath)) continue;

                    string lowerSlot = slotName.ToLowerInvariant();
                    if (lowerSlot.Contains("diffuse") || lowerSlot.Contains("color") || lowerSlot.Contains("base"))
                    {
                        diffusePath = texPath;
                        break;
                    }
                    fallbackPath ??= texPath;
                }

                string texPathToUse = diffusePath ?? fallbackPath;
                if (string.IsNullOrWhiteSpace(texPathToUse)) continue;

                string normalized = NormalizeAndMatchKey(texPathToUse, textureKeyList);
                if (normalized != null)
                    result[kv.Key] = normalized;
            }

            return result;
        }

        private static string NormalizeAndMatchKey(string texPath, List<string> availableKeys)
        {
            string fileName = Path.GetFileNameWithoutExtension(
                texPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
            return availableKeys.FirstOrDefault(k => k.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        }

        private static string TryResolveSkinBinPath(string sknPath)
        {
            if (string.IsNullOrWhiteSpace(sknPath))
            {
                return null;
            }

            if (!TryGetSkinFolderContext(sknPath, out string championName, out string skinFolder))
            {
                return null;
            }

            string skinBinName = GetSkinBinName(skinFolder);
            if (string.IsNullOrWhiteSpace(skinBinName))
            {
                return null;
            }

            foreach (string root in EnumerateCandidateRoots(sknPath))
            {
                string candidate = Path.Combine(root, "data", "characters", championName, "skins", skinBinName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static bool TryGetSkinFolderContext(string sknPath, out string championName, out string skinFolder)
        {
            championName = null;
            skinFolder = null;

            DirectoryInfo current = new FileInfo(Path.GetFullPath(sknPath)).Directory;
            while (current != null)
            {
                if (current.Parent?.Name.Equals("skins", StringComparison.OrdinalIgnoreCase) == true)
                {
                    skinFolder = current.Name;
                    championName = current.Parent.Parent?.Name;
                    return !string.IsNullOrWhiteSpace(championName);
                }

                current = current.Parent;
            }

            return false;
        }

        private static IEnumerable<string> EnumerateCandidateRoots(string sknPath)
        {
            DirectoryInfo current = new FileInfo(Path.GetFullPath(sknPath)).Directory;
            while (current != null)
            {
                yield return current.FullName;
                current = current.Parent;
            }
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

    }
}
