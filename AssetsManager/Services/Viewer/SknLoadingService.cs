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
                var materialTextureOverrides = LoadMaterialTextureOverrides(filePath, loadedTextures.Keys, textureDirectoryPath);

                _logService.LogDebug($"Loaded model (with custom textures): {Path.GetFileNameWithoutExtension(filePath)}");
                return await CreateSceneModel(skinnedMesh, loadedTextures, Path.GetFileNameWithoutExtension(filePath), materialTextureOverrides, filePath);
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
                var materialTextureOverrides = LoadMaterialTextureOverrides(filePath, loadedTextures.Keys, null);

                _logService.LogDebug($"Loaded model: {Path.GetFileNameWithoutExtension(filePath)}");
                return await CreateSceneModel(skinnedMesh, loadedTextures, Path.GetFileNameWithoutExtension(filePath), materialTextureOverrides, filePath);
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
                        BitmapSource loadedTex = TextureUtils.LoadViewerTexture(fileStream, Path.GetExtension(texPath), _logService, texPath);
                        if (loadedTex != null)
                        {
                            string textureKey = PathUtils.TruncateAtDot(Path.GetFileNameWithoutExtension(texPath));
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
            IReadOnlyDictionary<string, string> materialTextureOverrides,
            string filePath = "")
        {
            var availableTextureNames = new ObservableRangeCollection<string>(loadedTextures.Keys);
            string skinName = modelName.Split('.')[0];
            var colorTextureKeys = TextureUtils.GetColorTextureCandidates(loadedTextures.Keys);

            string defaultTextureKey = colorTextureKeys
                .Where(k => k.IndexOf(skinName, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(k =>
                {
                    int dotIndex = k.IndexOf('.');
                    string baseName = dotIndex > 0 ? k.Substring(0, dotIndex) : k;
                    return baseName.EndsWith("_tx_cm", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(k =>
                {
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
                var sceneModel = new SceneModel { Name = modelName, SkinnedMesh = skinnedMesh, FilePath = filePath };
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

                    var modelPart = new ModelPart(
                        string.IsNullOrEmpty(data.MaterialName) ? "Default" : data.MaterialName,
                        geometryModel)
                    {
                        SourceVertexIndices = data.SourceVertexIndices,
                        AllTextures = loadedTextures,
                        AvailableTextureNames = availableTextureNames,
                        SelectedTextureName = data.TexturePath
                    };

                    TextureUtils.UpdateMaterial(modelPart);

                    parts.Add(modelPart);
                }

                sceneModel.AddParts(parts);
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

        private Dictionary<string, string> LoadMaterialTextureOverrides(string sknPath, IEnumerable<string> loadedTextureKeys, string textureDirPath)
        {
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string skinBinPath = TryResolveSkinBinPath(sknPath, textureDirPath);
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
            string fileName = PathUtils.TruncateAtDot(Path.GetFileNameWithoutExtension(
                texPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
            return availableKeys.FirstOrDefault(k => k.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        }

        private static string TryResolveSkinBinPath(string sknPath, string textureDirPath = null)
        {
            string pathForResolution = !string.IsNullOrEmpty(textureDirPath) ? textureDirPath : sknPath;
            if (string.IsNullOrWhiteSpace(pathForResolution))
            {
                return null;
            }

            string fullPath = Path.GetFullPath(pathForResolution);
            string normalizedPath = fullPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string marker = $"{Path.DirectorySeparatorChar}assets{Path.DirectorySeparatorChar}characters{Path.DirectorySeparatorChar}";
            int markerIndex = normalizedPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                string rootPath = normalizedPath[..markerIndex];
                string relativePath = normalizedPath[(markerIndex + marker.Length)..];
                string[] parts = relativePath.Split(Path.DirectorySeparatorChar);
                if (parts.Length >= 3 && parts[1].Equals("skins", StringComparison.OrdinalIgnoreCase))
                {
                    string championName = parts[0];
                    string skinFolder = parts[2];
                    string skinsDirectory = Path.Combine(rootPath, "data", "characters", championName, "skins");
                    string skinBinName = GetSkinBinName(skinsDirectory, skinFolder, championName);
                    if (!string.IsNullOrWhiteSpace(skinBinName))
                    {
                        if (File.Exists(skinBinName)) return skinBinName;
                        string resolvedPath = Path.Combine(rootPath, "data", "characters", championName, "skins", skinBinName);
                        if (File.Exists(resolvedPath)) return resolvedPath;
                    }
                }
            }

            // Fallback: scan upward for a "data/characters" directory
            string[] pathParts = normalizedPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < pathParts.Length; i++)
            {
                if (!pathParts[i].Equals("characters", StringComparison.OrdinalIgnoreCase)) continue;
                if (i < 2 || i >= pathParts.Length - 1) continue;

                string championName = pathParts[i - 1];
                string foundDataDir = string.Join(Path.DirectorySeparatorChar.ToString(), pathParts.Take(i - 1));
                if (i - 1 > 0) foundDataDir = Path.DirectorySeparatorChar + foundDataDir;

                // Look for "skins" dir in path after "characters/<champ>"
                for (int j = i + 1; j < pathParts.Length; j++)
                {
                    if (!pathParts[j].Equals("skins", StringComparison.OrdinalIgnoreCase)) continue;
                    if (j + 1 >= pathParts.Length) continue;

                    string skinFolder = pathParts[j + 1];
                    string skinsDirectory = Path.Combine(foundDataDir, "characters", championName, "skins");
                    string skinBinName = GetSkinBinName(skinsDirectory, skinFolder, championName);
                    if (string.IsNullOrWhiteSpace(skinBinName)) continue;

                    if (File.Exists(skinBinName)) return skinBinName;
                    string candidate = Path.Combine(foundDataDir, "characters", championName, "skins", skinBinName);
                    if (File.Exists(candidate)) return candidate;
                    break;
                }
            }

            return null;
        }

        private static string GetSkinBinName(string skinsDirectory, string skinFolder, string championName)
        {
            if (string.IsNullOrWhiteSpace(skinsDirectory) || !Directory.Exists(skinsDirectory))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(skinFolder))
            {
                if (skinFolder.Equals("base", StringComparison.OrdinalIgnoreCase))
                {
                    string baseBin = Path.Combine(skinsDirectory, "skin0.bin");
                    if (File.Exists(baseBin)) return baseBin;
                }

                Match match = Regex.Match(skinFolder, @"^skin0*(\d+)$", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    string skinBin = Path.Combine(skinsDirectory, $"skin{int.Parse(match.Groups[1].Value)}.bin");
                    if (File.Exists(skinBin)) return skinBin;
                }
            }

            // Fallbacks for packed bins: <champ>_multi_skins_*.bin, <champ>*_skins.bin, then any *.bin.
            try
            {
                var bins = Directory.GetFiles(skinsDirectory, "*.bin", SearchOption.TopDirectoryOnly);
                string champLower = championName?.ToLowerInvariant() ?? string.Empty;

                string multi = bins.FirstOrDefault(b =>
                    Path.GetFileName(b).IndexOf("multi_skins", StringComparison.OrdinalIgnoreCase) >= 0);
                if (multi != null) return multi;

                string champSkins = bins.FirstOrDefault(b =>
                    champLower.Length > 0 &&
                    Path.GetFileName(b).StartsWith(champLower, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(b).IndexOf("_skins", StringComparison.OrdinalIgnoreCase) >= 0);
                if (champSkins != null) return champSkins;

                if (bins.Length > 0) return bins[0];
            }
            catch { }

            return null;
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
