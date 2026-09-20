using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using LeagueToolkit.Core.Animation;
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
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Views.Models.Viewer;


namespace AssetsManager.Services.Viewer.Loading
{
    public class SknLoadingService
    {
        private const int MaximumLinkedMaterialBins = 32;
        private readonly LogService _logService;
        private readonly HashResolverService _hashResolverService;


        public SknLoadingService(
            LogService logService,
            HashResolverService hashResolverService = null)
        {
            _logService = logService;
            _hashResolverService = hashResolverService;
        }

        // Loads an SKN model and its textures from a custom texture directory (for chromas).
        public async Task<SceneModel> LoadModel(string filePath, string textureDirectoryPath, CancellationToken cancellationToken = default)
        {
            if (_hashResolverService != null)
                await _hashResolverService.LoadHashesAsync();

            return await Task.Run(async () =>
            {
                try
                {
                    SkinnedMesh skinnedMesh = SkinnedMesh.ReadFromSimpleSkin(filePath);
                    if (string.IsNullOrEmpty(textureDirectoryPath) || !Directory.Exists(textureDirectoryPath))
                    {
                        _logService.LogError("Invalid texture directory provided for chroma model.");
                        skinnedMesh.Dispose();
                        return null;
                    }

                    var loadedTextures = LoadTexturesFromDirectory(textureDirectoryPath, cancellationToken);
                    string[] selectableTextureKeys = loadedTextures.Keys.ToArray();
                    // Chroma folders contain the replacement color maps, while the exact skin BIN
                    // may reference shared/parent-skin effect maps. Load those dependencies as well;
                    // the dictionary keeps the chroma files authoritative when names collide.
                    var materialTextures = LoadMaterialTextures(
                        textureDirectoryPath,
                        loadedTextures,
                        true,
                        targetSknPath: filePath);

                    _logService.LogDebug($"Loaded model (with custom textures): {Path.GetFileNameWithoutExtension(filePath)}");
                    return await CreateSceneModel(
                        skinnedMesh,
                        loadedTextures,
                        selectableTextureKeys,
                        Path.GetFileNameWithoutExtension(filePath),
                        materialTextures,
                        filePath,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logService.LogError(ex, "Failed to load model with custom textures");
                    return null;
                }
            }, cancellationToken);
        }

        // Loads an SKN model and its textures from the SKN file directory (standard behavior).
        public Task<SceneModel> LoadModel(string filePath, CancellationToken cancellationToken = default)
            => LoadModelCore(filePath, null, cancellationToken);

        /// <summary>
        /// Loads an SKN while using the exact skin BIN already selected by the caller.
        /// This avoids re-inferring the BIN from the SKN path when an extracted WAD is flat/hash-named.
        /// </summary>
        public Task<SceneModel> LoadModelWithSkinBin(
            string filePath,
            string skinBinPath,
            CancellationToken cancellationToken = default)
            => LoadModelCore(filePath, skinBinPath, cancellationToken);

        private async Task<SceneModel> LoadModelCore(
            string filePath,
            string explicitSkinBinPath,
            CancellationToken cancellationToken)
        {
            if (_hashResolverService != null)
                await _hashResolverService.LoadHashesAsync();

            return await Task.Run(async () =>
            {
                try
                {
                    SkinnedMesh skinnedMesh = SkinnedMesh.ReadFromSimpleSkin(filePath);
                    string modelDirectory = ResolveTextureDirectory(filePath);

                    if (string.IsNullOrEmpty(modelDirectory))
                    {
                        _logService.LogError("Could not determine the model directory.");
                        skinnedMesh.Dispose();
                        return null;
                    }

                    var loadedTextures = LoadTexturesFromDirectory(modelDirectory, cancellationToken);
                    string[] selectableTextureKeys = loadedTextures.Keys.ToArray();
                    var materialTextures = LoadMaterialTextures(
                        filePath,
                        loadedTextures,
                        true,
                        explicitSkinBinPath,
                        filePath);

                    _logService.LogDebug($"Loaded model: {Path.GetFileNameWithoutExtension(filePath)}");
                    return await CreateSceneModel(
                        skinnedMesh,
                        loadedTextures,
                        selectableTextureKeys,
                        Path.GetFileNameWithoutExtension(filePath),
                        materialTextures,
                        filePath,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logService.LogError(ex, "Failed to load model");
                    return null;
                }
            }, cancellationToken);
        }

        private Dictionary<string, BitmapSource> LoadTexturesFromDirectory(string directoryPath, CancellationToken cancellationToken)
        {
            var loadedTextures = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
            var textureFiles = Directory.GetFiles(directoryPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                .Where(path => !SknMaterialTextureResolver.IsPresentationTexture(
                    Path.GetFileNameWithoutExtension(path)))
                .OrderBy(path => Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

            foreach (string texPath in textureFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
            IReadOnlyCollection<string> selectableTextureKeys,
            string modelName,
            SknMaterialTextureResolution materialTextures,
            string filePath,
            CancellationToken cancellationToken)
        {
            var availableTextureNames = new ObservableRangeCollection<string>(
                SknMaterialTextureResolver.GetSelectableTextureCandidates(selectableTextureKeys, materialTextures));
            // A missing BIN leaves the SKN unbound instead of guessing an albedo from filenames.
            // Available textures remain selectable manually in the Viewer.
            string defaultTextureKey = materialTextures?.DefaultMaterialDefinition.BaseTextureName;

            var dataList = new List<SubmeshData>();
            var vertexAccessor = skinnedMesh.VerticesView.GetAccessor(VertexElement.POSITION.Name);
            var positions = vertexAccessor.AsVector3Array().ToArray();
            var texCoordAccessor = skinnedMesh.VerticesView.GetAccessor(VertexElement.TEXCOORD_0.Name);
            var texCoords = texCoordAccessor.AsVector2Array().ToArray();
            System.Numerics.Vector3[] normals = null;
            if (skinnedMesh.VerticesView.TryGetAccessor(VertexElement.NORMAL.Name, out VertexElementAccessor normalAccessor))
            {
                System.Numerics.Vector3[] authoredNormals = normalAccessor.AsVector3Array().ToArray();
                if (authoredNormals.Length == positions.Length)
                    normals = authoredNormals;
            }
            var indices = skinnedMesh.Indices;

            foreach (var rangeObj in skinnedMesh.Ranges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string materialName = rangeObj.Material.TrimEnd('\0');

                var subIndices = indices.Slice(rangeObj.StartIndex, rangeObj.IndexCount);
                var vertexMap = new Dictionary<int, int>();
                var subPositions = new List<Point3D>();
                var subTexCoords = new List<System.Windows.Point>();
                var subNormals = normals == null ? null : new List<Vector3D>();
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
                        if (subNormals != null)
                        {
                            System.Numerics.Vector3 normal = normals[sourceIndex];
                            subNormals.Add(new Vector3D(normal.X, normal.Y, normal.Z));
                        }
                        sourceVertexIndices.Add(sourceIndex);
                    }

                    triangleIndices[i] = localIndex;
                }

                string normalizedMaterialName = SknMaterialTextureResolver.NormalizeMaterialKey(materialName);
                ModelMaterialDefinition materialDefinition =
                    materialTextures?.ResolveMaterialDefinition(normalizedMaterialName) ??
                    ModelMaterialDefinition.TextureOnly(defaultTextureKey);

                dataList.Add(new SubmeshData(
                    materialName,
                    subPositions.ToArray(),
                    triangleIndices,
                    subTexCoords.ToArray(),
                    subNormals?.ToArray(),
                    sourceVertexIndices.ToArray(),
                    materialDefinition));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var initiallyHiddenSubmeshes = (materialTextures?.InitialHiddenSubmeshes ?? Array.Empty<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            RigResource skeleton = null;
            string skeletonPath = Path.ChangeExtension(filePath, ".skl");
            if (File.Exists(skeletonPath))
            {
                using var stream = File.OpenRead(skeletonPath);
                skeleton = new RigResource(stream);
            }

            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var sceneModel = new SceneModel
                {
                    Name = modelName,
                    SkinnedMesh = skinnedMesh,
                    FilePath = filePath,
                    Skeleton = skeleton,
                    Scale = materialTextures?.SkinScale ?? 1f
                };
                _logService.LogDebug("--- Displaying Model ---");
                var parts = new List<ModelPart>();

                foreach (var data in dataList)
                {
                    var positionsCol = new Point3DCollection(data.Positions);
                    var indicesCol = new Int32Collection(data.TriangleIndices);
                    var texCoordsCol = new PointCollection(data.TextureCoordinates);
                    Vector3DCollection normalsCol = data.Normals == null
                        ? null
                        : new Vector3DCollection(data.Normals);

                    if (positionsCol.CanFreeze) positionsCol.Freeze();
                    if (indicesCol.CanFreeze) indicesCol.Freeze();
                    if (texCoordsCol.CanFreeze) texCoordsCol.Freeze();
                    if (normalsCol?.CanFreeze == true) normalsCol.Freeze();

                    MeshGeometry3D meshGeometry = new MeshGeometry3D
                    {
                        Positions = positionsCol,
                        TriangleIndices = indicesCol,
                        TextureCoordinates = texCoordsCol,
                        Normals = normalsCol
                    };

                    var geometryModel = new GeometryModel3D(meshGeometry, null);

                    var modelPart = new ModelPart(
                        string.IsNullOrEmpty(data.MaterialName) ? "Default" : data.MaterialName,
                        geometryModel)
                    {
                        SourceVertexIndices = data.SourceVertexIndices,
                        AllTextures = loadedTextures,
                        AvailableTextureNames = availableTextureNames,
                        SelectedTextureName = data.MaterialDefinition.BaseTextureName,
                        MaterialDefinition = data.MaterialDefinition
                    };

                    if (initiallyHiddenSubmeshes.Contains(modelPart.Name))
                        modelPart.IsVisible = false;

                    parts.Add(modelPart);
                }

                sceneModel.AddParts(parts);
                _logService.LogDebug("--- Finished displaying model ---");
                return sceneModel;
            });
        }

        private SknMaterialTextureResolution LoadMaterialTextures(
            string assetPath,
            Dictionary<string, BitmapSource> loadedTextures,
            bool loadReferencedTextures,
            string explicitSkinBinPath = null,
            string targetSknPath = null)
        {
            string skinBinPath = !string.IsNullOrWhiteSpace(explicitSkinBinPath) && File.Exists(explicitSkinBinPath)
                ? Path.GetFullPath(explicitSkinBinPath)
                : SknMaterialTextureResolver.TryResolveBinPath(assetPath);
            if (string.IsNullOrEmpty(skinBinPath) || !File.Exists(skinBinPath))
            {
                _logService.LogDebug($"No exact skin material bin found for '{Path.GetFileName(assetPath)}'.");
                return null;
            }

            try
            {
                var binTrees = LoadMaterialBinTrees(skinBinPath).ToList();
                if (binTrees.Count == 0)
                {
                    return null;
                }

                string shaderBinPath = SknMaterialTextureResolver.TryResolveShaderBinPath(assetPath);
                if (!string.IsNullOrEmpty(shaderBinPath) &&
                    !Path.GetFullPath(shaderBinPath).Equals(Path.GetFullPath(skinBinPath), StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var shaderStream = File.OpenRead(shaderBinPath);
                        binTrees.Add(new BinTree(shaderStream));
                    }
                    catch (Exception ex)
                    {
                        // Shader definitions are optional; material-authored values remain valid when the shared file is absent or unreadable.
                        _logService.LogDebug($"Could not read optional shader definitions '{shaderBinPath}': {ex.Message}");
                    }
                }

                Func<ulong, string> wadChunkPathResolver = _hashResolverService == null
                    ? null
                    : _hashResolverService.ResolveHash;
                Func<uint, string> binEntryResolver = _hashResolverService == null
                    ? null
                    : _hashResolverService.ResolveBinEntry;
                SknMaterialTextureMetadata metadata =
                    SknMaterialTextureResolver.ReadMetadata(
                        binTrees,
                        wadChunkPathResolver,
                        binEntryResolver,
                        targetSknPath);
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
                    $"Loaded skin material metadata from '{Path.GetFileName(skinBinPath)}': " +
                    $"default='{resolution.DefaultMaterialDefinition?.BaseTextureName ?? "none"}', " +
                    $"submeshMaterials={resolution.MaterialDefinitions.Count}.");
                return resolution;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to read skin material bin: {skinBinPath}");
                return null;
            }
        }

        private IReadOnlyList<BinTree> LoadMaterialBinTrees(string primaryBinPath)
        {
            var trees = new List<BinTree>();
            var pendingPaths = new Queue<(string Path, bool IsLinked)>();
            var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pendingPaths.Enqueue((primaryBinPath, false));
            int openedLinkedBins = 0;

            while (pendingPaths.Count > 0)
            {
                (string binPath, bool isLinked) = pendingPaths.Dequeue();
                string fullPath = Path.GetFullPath(binPath);
                if (!visitedPaths.Add(fullPath))
                    continue;
                if (isLinked && openedLinkedBins >= MaximumLinkedMaterialBins)
                    break;
                if (isLinked)
                    openedLinkedBins++;
                if (!File.Exists(fullPath))
                    continue;

                try
                {
                    using var stream = File.OpenRead(fullPath);
                    var tree = new BinTree(stream);
                    trees.Add(tree);

                    foreach (string dependency in tree.Dependencies)
                    {
                        string dependencyPath =
                            SknMaterialTextureResolver.TryResolveDependencyBinPath(fullPath, dependency);
                        if (dependencyPath != null)
                        {
                            pendingPaths.Enqueue((dependencyPath, true));
                        }
                        else
                        {
                            _logService.LogDebug(
                                $"Could not resolve skin material BIN dependency '{dependency}' from '{fullPath}'.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogDebug(
                        $"Could not read skin material BIN dependency '{fullPath}': {ex.Message}");
                }
            }

            return trees;
        }

        private record SubmeshData(
            string MaterialName,
            Point3D[] Positions,
            int[] TriangleIndices,
            System.Windows.Point[] TextureCoordinates,
            Vector3D[] Normals,
            int[] SourceVertexIndices,
            ModelMaterialDefinition MaterialDefinition);

    }
}
