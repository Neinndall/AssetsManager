using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Core.Meta;
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


namespace AssetsManager.Services.Viewer.Models
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
                var materialTextures = LoadMaterialTextures(textureDirectoryPath, loadedTextures, false);

                _logService.LogDebug($"Loaded model (with custom textures): {Path.GetFileNameWithoutExtension(filePath)}");
                return await CreateSceneModel(skinnedMesh, loadedTextures, Path.GetFileNameWithoutExtension(filePath), materialTextures, filePath);
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
                string modelDirectory = ResolveTextureDirectory(filePath);

                if (string.IsNullOrEmpty(modelDirectory))
                {
                    _logService.LogError("Could not determine the model directory.");
                    return null;
                }

                var loadedTextures = LoadTexturesFromDirectory(modelDirectory);
                var materialTextures = LoadMaterialTextures(filePath, loadedTextures, true);

                _logService.LogDebug($"Loaded model: {Path.GetFileNameWithoutExtension(filePath)}");
                return await CreateSceneModel(skinnedMesh, loadedTextures, Path.GetFileNameWithoutExtension(filePath), materialTextures, filePath);
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
                LoadTextureFile(texPath, loadedTextures);
            }
            return loadedTextures;
        }

        private void LoadTextureFile(string texturePath, Dictionary<string, BitmapSource> loadedTextures)
        {
            try
            {
                using Stream fileStream = File.OpenRead(texturePath);
                BitmapSource loadedTexture = TextureUtils.LoadViewerTexture(
                    fileStream,
                    Path.GetExtension(texturePath),
                    _logService,
                    texturePath);
                if (loadedTexture != null)
                {
                    string textureKey = PathUtils.TruncateAtDot(Path.GetFileNameWithoutExtension(texturePath));
                    loadedTextures[textureKey] = loadedTexture;
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to load texture file: {texturePath}");
            }
        }

        internal static string ResolveTextureDirectory(string modelPath)
        {
            string modelDirectory = Path.GetDirectoryName(modelPath);
            if (string.IsNullOrEmpty(modelDirectory))
            {
                return modelDirectory;
            }

            var directory = new DirectoryInfo(modelDirectory);
            DirectoryInfo themeDirectory = directory.Parent;
            if (themeDirectory?.Parent != null &&
                themeDirectory.Parent.Name.Equals("themes", StringComparison.OrdinalIgnoreCase) &&
                ContainsTextures(themeDirectory.FullName))
            {
                return themeDirectory.FullName;
            }

            return modelDirectory;
        }

        private static bool ContainsTextures(string directoryPath) =>
            Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.TopDirectoryOnly)
                .Any(path => path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
                             path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase));

        private async Task<SceneModel> CreateSceneModel(
            SkinnedMesh skinnedMesh,
            Dictionary<string, BitmapSource> loadedTextures,
            string modelName,
            SknMaterialTextureResolution materialTextures,
            string filePath = "")
        {
            var availableTextureNames = new ObservableRangeCollection<string>(loadedTextures.Keys);
            string defaultTextureKey = materialTextures?.DefaultTextureKey ??
                SknMaterialTextureResolver.FindUnambiguousFallback(loadedTextures.Keys);

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

                    // Riot SKNs may store submesh indices globally or relative to the range's first vertex.
                    bool usesGlobalIndices = true;
                    bool usesLocalIndices = rangeObj.StartVertex > 0;
                    for (int i = 0; i < rangeObj.IndexCount; i++)
                    {
                        int index = (int)subIndices[i];
                        usesGlobalIndices &= index >= rangeObj.StartVertex &&
                                             index < rangeObj.StartVertex + rangeObj.VertexCount;
                        usesLocalIndices &= index >= 0 && index < rangeObj.VertexCount;
                    }

                    if (!usesGlobalIndices && !usesLocalIndices)
                    {
                        throw new InvalidDataException(
                            $"Submesh '{materialName}' contains indices outside its declared vertex range.");
                    }

                    int vertexOffset = usesLocalIndices && !usesGlobalIndices
                        ? rangeObj.StartVertex
                        : 0;

                    for (int i = 0; i < rangeObj.IndexCount; i++)
                    {
                        int sourceIndex = (int)subIndices[i] + vertexOffset;
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
                        defaultTextureKey,
                        materialTextures?.Overrides,
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
            string defaultTextureKey,
            IReadOnlyDictionary<string, string> materialTextureOverrides,
            Dictionary<string, BitmapSource> loadedTextures)
        {
            string normalizedMaterialName = SknMaterialTextureResolver.NormalizeMaterialKey(materialName);
            if (!string.IsNullOrEmpty(normalizedMaterialName) &&
                materialTextureOverrides != null &&
                materialTextureOverrides.TryGetValue(normalizedMaterialName, out string overrideTextureKey) &&
                loadedTextures.ContainsKey(overrideTextureKey))
            {
                _logService.LogDebug($"Found material-bin texture '{overrideTextureKey}' for submesh '{materialName}'.");
                return overrideTextureKey;
            }

            if (defaultTextureKey != null)
            {
                _logService.LogDebug($"Using skin-bin default texture '{defaultTextureKey}' for submesh '{materialName}'.");
            }

            return defaultTextureKey;
        }

        private SknMaterialTextureResolution LoadMaterialTextures(
            string assetPath,
            Dictionary<string, BitmapSource> loadedTextures,
            bool loadReferencedTextures)
        {
            string skinBinPath = SknMaterialTextureResolver.TryResolveBinPath(assetPath);
            if (string.IsNullOrEmpty(skinBinPath) || !File.Exists(skinBinPath))
            {
                _logService.LogDebug($"No exact skin material bin found for '{Path.GetFileName(assetPath)}'.");
                return null;
            }

            try
            {
                using var stream = File.OpenRead(skinBinPath);
                var binTree = new BinTree(stream);
                SknMaterialTextureMetadata metadata =
                    SknMaterialTextureResolver.ReadMetadata(binTree);
                if (loadReferencedTextures)
                {
                    foreach (string texturePath in metadata.ReferencedTexturePaths)
                    {
                        string resolvedPath =
                            SknMaterialTextureResolver.TryResolveTexturePath(assetPath, texturePath);
                        if (resolvedPath != null)
                        {
                            string textureKey = PathUtils.TruncateAtDot(Path.GetFileNameWithoutExtension(resolvedPath));
                            if (!loadedTextures.ContainsKey(textureKey))
                            {
                                LoadTextureFile(resolvedPath, loadedTextures);
                            }
                        }
                    }
                }

                SknMaterialTextureResolution resolution =
                    SknMaterialTextureResolver.Resolve(metadata, loadedTextures.Keys);
                _logService.LogDebug(
                    $"Loaded skin texture metadata from '{Path.GetFileName(skinBinPath)}': " +
                    $"default='{resolution.DefaultTextureKey ?? "none"}', overrides={resolution.Overrides.Count}.");
                return resolution;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to read skin material bin: {skinBinPath}");
                return null;
            }
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
