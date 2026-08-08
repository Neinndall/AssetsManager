using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using LeagueToolkit.Core.Environment;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Memory;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Composition;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Services.Viewer.Loading
{
    public class MapGeometryLoadingService
    {
        private const int MapTextureMaxSize = 2048;

        private readonly LogService _logService;

        public MapGeometryLoadingService(LogService logService)
        {
            _logService = logService;
        }

        public async Task<SceneModel> LoadMapGeometry(
            string filePath,
            string gameDataPath,
            CancellationToken cancellationToken = default)
        {
            return await LoadMapGeometry(filePath, null, gameDataPath, cancellationToken);
        }

        public async Task<SceneModel> LoadMapGeometry(
            string filePath,
            string materialsPath,
            string gameDataPath,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(async () =>
            {
                BinTree materialsBin = null;
                if (!string.IsNullOrEmpty(materialsPath) && File.Exists(materialsPath))
                {
                    try
                    {
                        using (var stream = File.OpenRead(materialsPath))
                        {
                            materialsBin = new BinTree(stream);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logService.LogError(ex, "Failed to load materials.bin");
                    }
                }

                try
                {
                    using (var stream = File.OpenRead(filePath))
                    using (var mapGeometry = new EnvironmentAsset(stream))
                    {
                        string modelName = Path.GetFileNameWithoutExtension(filePath);

                        return await CreateSceneModel(
                            mapGeometry,
                            modelName,
                            materialsBin,
                            gameDataPath,
                            cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, "Failed to load map geometry");
                    return null;
                }
            }, cancellationToken);
        }

        private async Task<SceneModel> CreateSceneModel(
            EnvironmentAsset mapGeometry,
            string modelName,
            BinTree materialsBin,
            string gameDataPath,
            CancellationToken cancellationToken)
        {
            MapGeometryProcessingResult processingResult = await Task.Run(
                () => ProcessMapGeometry(mapGeometry, materialsBin, gameDataPath, cancellationToken),
                cancellationToken);
            SceneModel sceneModel = CreateSceneFromProcessedMap(modelName, processingResult);
            LogMaterialDiagnostics(processingResult, materialsBin != null);
            _logService.LogDebug("--- Finished displaying model ---");
            return sceneModel;
        }

        private MapGeometryProcessingResult ProcessMapGeometry(
            EnvironmentAsset mapGeometry,
            BinTree materialsBin,
            string gameDataPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var materialResolver = new MapGeometryMaterialResolver(materialsBin);

            var dataList = new List<MapGeometrySubmeshData>();
            var allTexturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unresolvedMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var materialsWithoutVisuals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var layeredMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var materialDefinitions = new Dictionary<string, MapGeometryMaterialDefinition>(
                StringComparer.OrdinalIgnoreCase);
            var mappingBuilders = new Dictionary<string, MapGeometryUvWorldMappingBuilder>(
                StringComparer.OrdinalIgnoreCase);
            MapLightingProfile lightingProfile = MapGeometryLightingResolver.Resolve(materialsBin);

            foreach (var mesh in mapGeometry.Meshes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var positions = mesh.VerticesView.GetAccessor(VertexElement.POSITION.Name).AsVector3Array();
                var texCoordAccessor = mesh.VerticesView.GetAccessor(VertexElement.TEXCOORD_0.Name);
                bool isPacked1616 = texCoordAccessor.Element.Format == ElementFormat.XY_Packed1616;
                MapGeometryLightmapData lightmap = MapGeometryLightingResolver.ResolveLightmap(mesh);
                if (lightmap != null)
                    allTexturePaths.Add(lightmap.TexturePath);

                foreach (var submesh in mesh.Submeshes)
                {
                    string materialName = submesh.Material.TrimEnd('\0');
                    MapGeometryMaterialDefinition material = null;
                    if (materialsBin != null && !materialResolver.TryResolve(materialName, out material))
                    {
                        unresolvedMaterials.Add(materialName);
                    }
                    else if (material != null)
                    {
                        materialDefinitions[materialName] = material;
                    }

                    var subPositions = new Point3D[submesh.VertexCount];
                    for (int i = 0; i < submesh.VertexCount; i++)
                    {
                        var p = positions[submesh.MinVertex + i];
                        subPositions[i] = new Point3D(p.X, p.Y, p.Z);
                    }

                    var indices = mesh.Indices.Slice(submesh.StartIndex, submesh.IndexCount);
                    var triangleIndices = new int[submesh.IndexCount];
                    for (int i = 0; i < submesh.IndexCount; i++)
                    {
                        triangleIndices[i] = (int)indices[i] - submesh.MinVertex;
                    }

                    var subTexCoords = new Point[submesh.VertexCount];
                    if (isPacked1616)
                    {
                        var texCoords = texCoordAccessor.AsXyF16Array();
                        for (int i = 0; i < submesh.VertexCount; i++)
                        {
                            var uv = texCoords[submesh.MinVertex + i];
                            subTexCoords[i] = new Point((float)uv.Item1, (float)uv.Item2);
                        }
                    }
                    else
                    {
                        var texCoords = texCoordAccessor.AsVector2Array();
                        for (int i = 0; i < submesh.VertexCount; i++)
                        {
                            var uv = texCoords[submesh.MinVertex + i];
                            subTexCoords[i] = new Point(uv.X, uv.Y);
                        }
                    }

                    float[] subLightmapCoordinates = lightmap?.SliceCoordinates(
                        submesh.MinVertex,
                        submesh.VertexCount);

                    bool isTerrainBlend = MapGeometryLayeredTextureComposer.IsTerrainBlend(material);
                    if (isTerrainBlend)
                    {
                        layeredMaterials.Add(materialName);
                        if (!mappingBuilders.TryGetValue(materialName, out MapGeometryUvWorldMappingBuilder builder))
                        {
                            builder = new MapGeometryUvWorldMappingBuilder();
                            mappingBuilders[materialName] = builder;
                        }

                        for (int i = 0; i < submesh.VertexCount; i++)
                        {
                            var uv = subTexCoords[i];
                            builder.Add(
                                (float)uv.X,
                                (float)uv.Y,
                                positions[submesh.MinVertex + i],
                                mesh.Transform);
                        }
                    }

                    MapGeometryTextureSampler primarySampler = material?.PrimarySampler;
                    string primaryTexturePath = primarySampler?.TexturePath;
                    if (material != null)
                    {
                        foreach (MapGeometryTextureSampler sampler in material.Samplers)
                        {
                            allTexturePaths.Add(PathUtils.ToVirtualPath(sampler.TexturePath));
                        }
                    }

                    if (string.IsNullOrWhiteSpace(primaryTexturePath) && material?.TintColor == null)
                        materialsWithoutVisuals.Add(materialName);

                    dataList.Add(new MapGeometrySubmeshData(
                        materialName,
                        subPositions,
                        triangleIndices,
                        subTexCoords,
                        mesh.Transform,
                        primaryTexturePath,
                        primarySampler?.AddressU == 0 || primarySampler?.AddressV == 0,
                        material?.TintColor,
                        mesh.DisableBackfaceCulling,
                        mesh.RenderFlags.HasFlag(EnvironmentAssetMeshRenderFlags.IsDecal),
                        lightmap?.TexturePath,
                        subLightmapCoordinates));
                }
            }

            MapGeometryTextureLoadResult textures = LoadTextures(
                gameDataPath,
                allTexturePaths,
                cancellationToken);
            Dictionary<string, string> layeredTextureKeys = ComposeLayeredTextures(
                layeredMaterials,
                materialDefinitions,
                mappingBuilders,
                textures,
                cancellationToken);

            return new MapGeometryProcessingResult(
                dataList,
                textures.LoadedTextures,
                textures.TextureKeys,
                layeredTextureKeys,
                unresolvedMaterials,
                materialsWithoutVisuals,
                textures.MissingTexturePaths,
                layeredMaterials,
                lightingProfile);
        }

        private MapGeometryTextureLoadResult LoadTextures(
            string gameDataPath,
            IEnumerable<string> texturePaths,
            CancellationToken cancellationToken)
        {
            Dictionary<string, string> textureKeys = BuildTextureKeys(texturePaths);
            var loadedTextures = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
            var texturesByPath = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
            var missingTexturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string texturePath in textureKeys.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string absoluteFilePath = ResolveTextureFilePath(gameDataPath, texturePath);
                if (absoluteFilePath == null)
                {
                    missingTexturePaths.Add(texturePath);
                    continue;
                }

                try
                {
                    using Stream fileStream = File.OpenRead(absoluteFilePath);
                    BitmapSource loadedTexture = TextureUtils.LoadViewerTexture(
                        fileStream,
                        Path.GetExtension(absoluteFilePath),
                        MapTextureMaxSize,
                        MapTextureMaxSize);
                    if (loadedTexture == null)
                    {
                        missingTexturePaths.Add(texturePath);
                        continue;
                    }

                    if (loadedTexture.CanFreeze) loadedTexture.Freeze();
                    loadedTextures[textureKeys[texturePath]] = loadedTexture;
                    texturesByPath[texturePath] = loadedTexture;
                }
                catch (Exception ex)
                {
                    missingTexturePaths.Add(texturePath);
                    _logService.LogError(ex, $"Failed to load texture file: {absoluteFilePath}");
                }
            }

            return new MapGeometryTextureLoadResult(
                loadedTextures,
                texturesByPath,
                textureKeys,
                missingTexturePaths);
        }

        private Dictionary<string, string> ComposeLayeredTextures(
            IEnumerable<string> layeredMaterials,
            IReadOnlyDictionary<string, MapGeometryMaterialDefinition> materialDefinitions,
            IReadOnlyDictionary<string, MapGeometryUvWorldMappingBuilder> mappingBuilders,
            MapGeometryTextureLoadResult textures,
            CancellationToken cancellationToken)
        {
            var layeredTextureKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string materialName in layeredMaterials)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    BitmapSource composite = MapGeometryLayeredTextureComposer.Compose(
                        materialDefinitions[materialName],
                        mappingBuilders[materialName].Build(),
                        textures.TexturesByPath,
                        cancellationToken);
                    if (composite == null)
                        continue;

                    string key = $"{materialName} [Terrain Blend]";
                    textures.LoadedTextures[key] = composite;
                    layeredTextureKeys[materialName] = key;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Failed to compose layered terrain material: {materialName}");
                }
            }

            return layeredTextureKeys;
        }

        private static SceneModel CreateSceneFromProcessedMap(
            string modelName,
            MapGeometryProcessingResult result)
        {
            var availableTextureNames = new ObservableRangeCollection<string>(result.LoadedTextures.Keys);
            var parts = new List<ModelPart>(result.Submeshes.Count);
            var sceneModel = new SceneModel
            {
                Name = modelName,
                MapLightingProfile = result.LightingProfile
            };

            foreach (MapGeometrySubmeshData data in result.Submeshes)
            {
                var positions = new Point3DCollection(data.Positions);
                var indices = new Int32Collection(data.TriangleIndices);
                var textureCoordinates = new PointCollection(data.TextureCoordinates);
                if (positions.CanFreeze) positions.Freeze();
                if (indices.CanFreeze) indices.Freeze();
                if (textureCoordinates.CanFreeze) textureCoordinates.Freeze();

                var mesh = new MeshGeometry3D
                {
                    Positions = positions,
                    TriangleIndices = indices,
                    TextureCoordinates = textureCoordinates
                };
                if (mesh.CanFreeze) mesh.Freeze();

                string textureKey = ResolveTextureKey(data, result);
                var fallbackBrush = new SolidColorBrush(ToMediaColor(data.TintColor));
                if (fallbackBrush.CanFreeze) fallbackBrush.Freeze();
                var fallbackMaterial = new DiffuseMaterial(fallbackBrush);
                if (fallbackMaterial.CanFreeze) fallbackMaterial.Freeze();
                var geometryModel = new GeometryModel3D(mesh, fallbackMaterial)
                {
                    Transform = ToMediaTransform(data.Transform),
                    BackMaterial = data.IsDoubleSided ? fallbackMaterial : null
                };

                var modelPart = new ModelPart(
                    PathUtils.SimplifyMeshName(data.MaterialName),
                    geometryModel)
                {
                    AllTextures = result.LoadedTextures,
                    AvailableTextureNames = availableTextureNames,
                    SelectedTextureName = textureKey,
                    IsTextureTiled = data.IsTextureTiled,
                    IsDoubleSided = data.IsDoubleSided,
                    IsDecal = data.IsDecal,
                    Lightmap = CreateLightmapBinding(data, result)
                };

                TextureUtils.UpdateMaterial(modelPart);
                parts.Add(modelPart);
            }

            sceneModel.AddParts(parts);
            return sceneModel;
        }

        private static string ResolveTextureKey(
            MapGeometrySubmeshData data,
            MapGeometryProcessingResult result)
        {
            if (result.LayeredTextureKeys.TryGetValue(data.MaterialName, out string layeredKey))
                return layeredKey;

            return ResolveTextureKey(data.TexturePath, result);
        }

        private static string ResolveTextureKey(
            string texturePath,
            MapGeometryProcessingResult result)
        {
            string normalizedPath = PathUtils.ToVirtualPath(texturePath);
            return !string.IsNullOrEmpty(normalizedPath) &&
                   result.TextureKeys.TryGetValue(normalizedPath, out string textureKey)
                ? textureKey
                : null;
        }

        private static MapLightmapBinding CreateLightmapBinding(
            MapGeometrySubmeshData data,
            MapGeometryProcessingResult result)
        {
            if (data.LightmapUvCoordinates == null || data.LightmapUvCoordinates.Length == 0)
                return null;

            string textureKey = ResolveTextureKey(data.LightmapTexturePath, result);
            return string.IsNullOrEmpty(textureKey)
                ? null
                : new MapLightmapBinding(textureKey, data.LightmapUvCoordinates);
        }

        private static Color ToMediaColor(System.Numerics.Vector4? value)
        {
            if (value == null)
                return Colors.Magenta;

            static byte ToByte(float component) =>
                (byte)Math.Round(Math.Clamp(component, 0f, 1f) * byte.MaxValue);

            System.Numerics.Vector4 color = value.Value;
            return Color.FromArgb(ToByte(color.W), ToByte(color.X), ToByte(color.Y), ToByte(color.Z));
        }

        private static Transform3D ToMediaTransform(System.Numerics.Matrix4x4 value)
        {
            if (value == System.Numerics.Matrix4x4.Identity)
                return Transform3D.Identity;

            return new MatrixTransform3D(new Matrix3D(
                value.M11, value.M12, value.M13, value.M14,
                value.M21, value.M22, value.M23, value.M24,
                value.M31, value.M32, value.M33, value.M34,
                value.M41, value.M42, value.M43, value.M44));
        }

        private void LogMaterialDiagnostics(MapGeometryProcessingResult result, bool hasMaterials)
        {
            if (!hasMaterials)
            {
                _logService.LogWarning("MapGeometry loaded without materials metadata.");
                return;
            }

            if (result.UnresolvedMaterials.Count > 0)
            {
                _logService.LogWarning(
                    $"MapGeometry materials missing from materials.bin ({result.UnresolvedMaterials.Count}): " +
                    string.Join(", ", result.UnresolvedMaterials.Take(8)));
            }

            if (result.MissingTexturePaths.Count > 0)
            {
                _logService.LogWarning(
                    $"MapGeometry texture files missing or unreadable ({result.MissingTexturePaths.Count}): " +
                    string.Join(", ", result.MissingTexturePaths.Take(8)));
            }

            if (result.MaterialsWithoutVisuals.Count > 0)
            {
                _logService.LogDebug(
                    $"MapGeometry materials without a color sampler or tint ({result.MaterialsWithoutVisuals.Count}): " +
                    string.Join(", ", result.MaterialsWithoutVisuals.Take(8)));
            }

            if (result.LayeredMaterials.Count > 0)
            {
                _logService.LogDebug(
                    $"MapGeometry layered terrain materials composed: " +
                    $"{result.LayeredTextureKeys.Count}/{result.LayeredMaterials.Count}.");
            }

            _logService.LogDebug(
                $"MapGeometry materials parsed: textures={result.LoadedTextures.Count}, " +
                $"unresolvedMaterials={result.UnresolvedMaterials.Count}, missingTextures={result.MissingTexturePaths.Count}.");
        }

        internal static Dictionary<string, string> BuildTextureKeys(IEnumerable<string> texturePaths)
        {
            string[] paths = texturePaths
                .Select(PathUtils.ToVirtualPath)
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var duplicateNames = paths
                .GroupBy(x => Path.GetFileNameWithoutExtension(x), StringComparer.OrdinalIgnoreCase)
                .Where(x => x.Count() > 1)
                .Select(x => x.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return paths.ToDictionary(
                x => x,
                x =>
                {
                    string shortName = Path.GetFileNameWithoutExtension(x);
                    return duplicateNames.Contains(shortName) ? x : shortName;
                },
                StringComparer.OrdinalIgnoreCase);
        }

        internal static string ResolveTextureFilePath(string gameDataPath, string texturePath)
        {
            if (string.IsNullOrWhiteSpace(gameDataPath) || string.IsNullOrWhiteSpace(texturePath))
                return null;

            string normalizedPath = PathUtils.ToVirtualPath(texturePath).Replace('/', Path.DirectorySeparatorChar);
            var searchDirs = new List<string>();

            if (Directory.Exists(gameDataPath))
            {
                var dir = new DirectoryInfo(gameDataPath);
                while (dir != null)
                {
                    searchDirs.Add(dir.FullName);
                    dir = dir.Parent;
                }
            }
            else if (File.Exists(gameDataPath))
            {
                var dir = new FileInfo(gameDataPath).Directory;
                while (dir != null)
                {
                    searchDirs.Add(dir.FullName);
                    dir = dir.Parent;
                }
            }

            foreach (string rootPath in searchDirs)
            {
                string candidatePath = Path.GetFullPath(Path.Combine(rootPath, normalizedPath));

                if (File.Exists(candidatePath))
                    return candidatePath;

                string extension = Path.GetExtension(candidatePath);
                foreach (string alternativeExtension in new[] { ".tex", ".dds", ".png", ".tga" })
                {
                    if (extension.Equals(alternativeExtension, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string alternativePath = Path.ChangeExtension(candidatePath, alternativeExtension);
                    if (File.Exists(alternativePath))
                        return alternativePath;
                }
            }

            return null;
        }

        private sealed record MapGeometryProcessingResult(
            List<MapGeometrySubmeshData> Submeshes,
            Dictionary<string, BitmapSource> LoadedTextures,
            Dictionary<string, string> TextureKeys,
            Dictionary<string, string> LayeredTextureKeys,
            HashSet<string> UnresolvedMaterials,
            HashSet<string> MaterialsWithoutVisuals,
            HashSet<string> MissingTexturePaths,
            HashSet<string> LayeredMaterials,
            MapLightingProfile LightingProfile);

        private sealed record MapGeometryTextureLoadResult(
            Dictionary<string, BitmapSource> LoadedTextures,
            Dictionary<string, BitmapSource> TexturesByPath,
            Dictionary<string, string> TextureKeys,
            HashSet<string> MissingTexturePaths);

        private sealed record MapGeometrySubmeshData(
            string MaterialName,
            Point3D[] Positions,
            int[] TriangleIndices,
            Point[] TextureCoordinates,
            System.Numerics.Matrix4x4 Transform,
            string TexturePath,
            bool IsTextureTiled,
            System.Numerics.Vector4? TintColor,
            bool IsDoubleSided,
            bool IsDecal,
            string LightmapTexturePath,
            float[] LightmapUvCoordinates);

    }
}
