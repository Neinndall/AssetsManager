using System.Reflection;
using LeagueToolkit.Hashing;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using AssetsManager.Services.Hashes;
using LeagueToolkit.Core.Environment;
using LeagueToolkit.Core.Mesh;
using AssetsManager.Views.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Media;
using System.Collections.ObjectModel;
using LeagueToolkit.Core.Renderer;
using LeagueToolkit.Toolkit;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Windows;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using System.Threading.Tasks;
using AssetsManager.Services.Explorer;

namespace AssetsManager.Services.Models
{
    public class ModelLoadingService
    {
        private readonly LogService _logService;
        private readonly HashResolverService _hashResolverService;
        private readonly WadExtractionService _wadExtractionService;
        private readonly WadNodeLoaderService _wadNodeLoaderService;

        public ModelLoadingService(LogService logService, HashResolverService hashResolverService, WadExtractionService wadExtractionService, WadNodeLoaderService wadNodeLoaderService)
        {
            _logService = logService;
            _hashResolverService = hashResolverService;
            _wadExtractionService = wadExtractionService;
            _wadNodeLoaderService = wadNodeLoaderService;
        }

        #region SKN Model Loading

        public SceneModel LoadModel(string filePath)
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

                var loadedTextures = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
                string[] textureFiles = Directory.GetFiles(modelDirectory, "*.tex", SearchOption.TopDirectoryOnly);
                textureFiles = textureFiles.Concat(Directory.GetFiles(modelDirectory, "*.dds", SearchOption.TopDirectoryOnly)).ToArray();

                foreach (string texPath in textureFiles)
                {
                    BitmapSource loadedTex = LoadTexture(texPath);
                    if (loadedTex != null)
                    {
                        string textureKey = Path.GetFileName(texPath).Split('.')[0];
                        loadedTextures[textureKey] = loadedTex;
                    }
                }
                _logService.LogDebug($"Loaded model: {Path.GetFileNameWithoutExtension(filePath)}");
                return CreateSceneModel(skinnedMesh, loadedTextures, Path.GetFileNameWithoutExtension(filePath));
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to load model");
                return null;
            }
        }

        private SceneModel CreateSceneModel(SkinnedMesh skinnedMesh, Dictionary<string, BitmapSource> loadedTextures, string modelName)
        {
            _logService.LogDebug("--- Displaying Model ---");
            _logService.LogDebug($"Available texture keys: {string.Join(", ", loadedTextures.Keys)}");

            var sceneModel = new SceneModel { Name = modelName, SkinnedMesh = skinnedMesh };
            var availableTextureNames = new ObservableCollection<string>(loadedTextures.Keys);

            string defaultTextureKey = loadedTextures.Keys
                .Where(k => k.EndsWith("_tx_cm", StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k.Length)
                .FirstOrDefault();

            string skinName = modelName.Split('.')[0];

            foreach (var rangeObj in skinnedMesh.Ranges)
            {
                string materialName = rangeObj.Material.TrimEnd('\0');
                MeshGeometry3D meshGeometry = new MeshGeometry3D();

                var positions = skinnedMesh.VerticesView.GetAccessor(LeagueToolkit.Core.Memory.VertexElement.POSITION.Name).AsVector3Array();
                var subPositions = new Point3D[rangeObj.VertexCount];
                for (int i = 0; i < rangeObj.VertexCount; i++)
                {
                    var p = positions[rangeObj.StartVertex + i];
                    subPositions[i] = new Point3D(p.X, p.Y, p.Z);
                }
                meshGeometry.Positions = new Point3DCollection(subPositions);

                Int32Collection triangleIndices = new Int32Collection();
                var indices = skinnedMesh.Indices.Slice(rangeObj.StartIndex, rangeObj.IndexCount);
                foreach (var index in indices)
                {
                    triangleIndices.Add((int)index - rangeObj.StartVertex);
                }
                meshGeometry.TriangleIndices = triangleIndices;

                var texCoords = skinnedMesh.VerticesView.GetAccessor(LeagueToolkit.Core.Memory.VertexElement.TEXCOORD_0.Name).AsVector2Array();
                var subTexCoords = new System.Windows.Point[rangeObj.VertexCount];
                for (int i = 0; i < rangeObj.VertexCount; i++)
                {
                    var uv = texCoords[rangeObj.StartVertex + i];
                    subTexCoords[i] = new System.Windows.Point(uv.X, uv.Y);
                }
                meshGeometry.TextureCoordinates = new PointCollection(subTexCoords);

                string initialMatchingKey = FindBestTextureMatch(materialName, skinName, loadedTextures.Keys, defaultTextureKey);

                var geometryModel = new GeometryModel3D(meshGeometry, new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Colors.Magenta)));

                var modelPart = new ModelPart
                {
                    Name = string.IsNullOrEmpty(materialName) ? "Default" : materialName,
                    Visual = new ModelVisual3D(),
                    AllTextures = loadedTextures,
                    AvailableTextureNames = availableTextureNames,
                    SelectedTextureName = initialMatchingKey,
                    Geometry = geometryModel
                };

                modelPart.Visual.Content = geometryModel;
                modelPart.UpdateMaterial();

                sceneModel.Parts.Add(modelPart);
                sceneModel.RootVisual.Children.Add(modelPart.Visual);
            }
            _logService.LogDebug("--- Finished displaying model ---");
            return sceneModel;
        }

        public BitmapSource LoadTexture(string textureFilePath)
        {
            try
            {
                string extension = Path.GetExtension(textureFilePath);
                using (Stream resourceStream = textureFilePath.StartsWith("pack://application:")
                    ? Application.GetResourceStream(new Uri(textureFilePath)).Stream
                    : File.OpenRead(textureFilePath))
                {
                    if (resourceStream == null) { return null; }

                    if (extension.Equals(".tex", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
                    {
                        Texture tex = Texture.Load(resourceStream);
                        if (tex.Mips.Length > 0)
                        {
                            Image<Rgba32> imageSharp = tex.Mips[0].ToImage();

                            // Efficiently create BitmapSource from raw pixel data, avoiding slow PNG encoding
                            var pixelBuffer = new byte[imageSharp.Width * imageSharp.Height * 4];
                            imageSharp.CopyPixelDataTo(pixelBuffer);

                            // Manually swap R and B channels to convert from RGBA (ImageSharp default) to BGRA (WPF default)
                            for (int i = 0; i < pixelBuffer.Length; i += 4)
                            {
                                var r = pixelBuffer[i];
                                var b = pixelBuffer[i + 2];
                                pixelBuffer[i] = b;
                                pixelBuffer[i + 2] = r;
                            }

                            int stride = imageSharp.Width * 4;
                            var bitmapSource = BitmapSource.Create(imageSharp.Width, imageSharp.Height, 96, 96, PixelFormats.Bgra32, null, pixelBuffer, stride);
                            bitmapSource.Freeze();

                            return bitmapSource;
                        }
                        return null;
                    }
                    else
                    {
                        BitmapImage bitmapImage = new BitmapImage();
                        bitmapImage.BeginInit();
                        bitmapImage.UriSource = new Uri(textureFilePath, UriKind.Absolute);
                        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                        bitmapImage.EndInit();
                        bitmapImage.Freeze();
                        return bitmapImage;
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to load texture");
                return null;
            }
        }

        private BitmapSource LoadTextureFromStream(Stream textureStream, string extension)
        {
            try
            {
                if (textureStream == null) { return null; }

                if (extension.Equals(".tex", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    Texture tex = Texture.Load(textureStream);
                    if (tex.Mips.Length > 0)
                    {
                        Image<Rgba32> imageSharp = tex.Mips[0].ToImage();

                        var pixelBuffer = new byte[imageSharp.Width * imageSharp.Height * 4];
                        imageSharp.CopyPixelDataTo(pixelBuffer);

                        for (int i = 0; i < pixelBuffer.Length; i += 4)
                        {
                            var r = pixelBuffer[i];
                            var b = pixelBuffer[i + 2];
                            pixelBuffer[i] = b;
                            pixelBuffer[i + 2] = r;
                        }

                        int stride = imageSharp.Width * 4;
                        var bitmapSource = BitmapSource.Create(imageSharp.Width, imageSharp.Height, 96, 96, PixelFormats.Bgra32, null, pixelBuffer, stride);
                        bitmapSource.Freeze();

                        return bitmapSource;
                    }
                    return null;
                }
                else
                {
                    BitmapImage bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.StreamSource = textureStream;
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();
                    return bitmapImage;
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to load texture from stream");
                return null;
            }
        }

        private async Task<byte[]> GetTextureBytesFromWadAsync(string texturePath, string gameDataPath)
        {
            try
            {
                // Strategy 1: Attempt to load from a direct file path, for extracted game clients.
                string absoluteFilePath = Path.Combine(gameDataPath, texturePath);

                _logService.Log($"Attempting to load texture from direct file path: {absoluteFilePath}");

                if (File.Exists(absoluteFilePath))
                {
                    return await File.ReadAllBytesAsync(absoluteFilePath);
                }

                _logService.LogWarning($"Direct file not found at '{absoluteFilePath}'. Falling back to WAD virtual path search.");

                // Strategy 2: Fallback to WAD virtual path search.
                string mapName = new DirectoryInfo(gameDataPath).Name;
                string prefixedPath = $"maps/{mapName.ToLower()}/{texturePath}".Replace('\\', '/');

                _logService.Log($"Attempting to find texture using prefixed virtual path: {prefixedPath}");
                FileSystemNodeModel textureNode = await _wadNodeLoaderService.FindNodeByVirtualPathAsync(prefixedPath, gameDataPath);

                if (textureNode == null)
                {
                    _logService.LogWarning($"Prefixed virtual path not found. Falling back to original virtual path: {texturePath}");
                    textureNode = await _wadNodeLoaderService.FindNodeByVirtualPathAsync(texturePath, gameDataPath);
                }

                if (textureNode != null)
                {
                    return await _wadExtractionService.GetVirtualFileBytesAsync(textureNode);
                }

                _logService.LogError($"Texture not found either as a direct file or within WAD archives for path: {texturePath}");
                return null;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to get texture bytes for path: {texturePath}");
                return null;
            }
        }

        public async Task<SceneModel> LoadMapGeometry(string filePath, string gameDataPath)
        {
            return await LoadMapGeometryInternal(filePath, null, gameDataPath);
        }

        public async Task<SceneModel> LoadMapGeometry(string filePath, string materialsPath, string gameDataPath)
        {
            try
            {
                // Ensure the necessary BIN hash dictionaries are loaded before processing.
                await _hashResolverService.LoadBinHashesAsync();

                using (var stream = File.OpenRead(materialsPath))
                {
                    var materialsBin = new BinTree(stream);
                    return await LoadMapGeometryInternal(filePath, materialsBin, gameDataPath);
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to load materials.bin");
                return null;
            }
        }

        private async Task<SceneModel> LoadMapGeometryInternal(string filePath, BinTree materialsBin, string gameDataPath)
        {
            try
            {
                using (var stream = File.OpenRead(filePath))
                {
                    EnvironmentAsset mapGeometry = new EnvironmentAsset(stream);
                    string modelName = Path.GetFileNameWithoutExtension(filePath);
                    
                    return await CreateSceneModel(mapGeometry, modelName, materialsBin, gameDataPath);
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to load map geometry");
                return null;
            }
        }

        private async Task<SceneModel> CreateSceneModel(EnvironmentAsset mapGeometry, string modelName, BinTree materialsBin, string gameDataPath)
        {
            _logService.LogDebug("--- Displaying Model ---");
            var sceneModel = new SceneModel { Name = modelName };
            var loadedTextures = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
            var availableTextureNames = new ObservableCollection<string>();
            string skinName = modelName.Split('.')[0];

            foreach (var mesh in mapGeometry.Meshes)
            {
                foreach (var submesh in mesh.Submeshes)
                {
                    string materialName = submesh.Material.TrimEnd('\0');
                    MeshGeometry3D meshGeometry = new MeshGeometry3D();

                    var positions = mesh.VerticesView.GetAccessor(LeagueToolkit.Core.Memory.VertexElement.POSITION.Name).AsVector3Array();
                    var subPositions = new Point3D[submesh.VertexCount];
                    for (int i = 0; i < submesh.VertexCount; i++)
                    {
                        var p = positions[submesh.MinVertex + i];
                        subPositions[i] = new Point3D(p.X, p.Y, p.Z);
                    }
                    meshGeometry.Positions = new Point3DCollection(subPositions);

                    Int32Collection triangleIndices = new Int32Collection();
                    var indices = mesh.Indices.Slice(submesh.StartIndex, submesh.IndexCount);
                    foreach (var index in indices)
                    {
                        triangleIndices.Add((int)index - submesh.MinVertex);
                    }
                    meshGeometry.TriangleIndices = triangleIndices;

                    var texCoords = mesh.VerticesView.GetAccessor(LeagueToolkit.Core.Memory.VertexElement.TEXCOORD_0.Name).AsVector2Array();
                    var subTexCoords = new System.Windows.Point[submesh.VertexCount];
                    for (int i = 0; i < submesh.VertexCount; i++)
                    {
                        var uv = texCoords[submesh.MinVertex + i];
                        subTexCoords[i] = new System.Windows.Point(uv.X, uv.Y);
                    }
                    meshGeometry.TextureCoordinates = new PointCollection(subTexCoords);

                    string textureNameKey = null;
                    string fullTexturePath = null;

                    if (materialsBin != null)
                    {
                        // Now that the resolver is primed, we can look up the material by its resolved key hash.
                        var foundMaterialKvp = materialsBin.Objects.FirstOrDefault(kvp => 
                            _hashResolverService.ResolveBinHashGeneral(kvp.Key).Equals(materialName, StringComparison.OrdinalIgnoreCase)
                        );

                        if (foundMaterialKvp.Value != null)
                        {
                            var samplerValuesKvp = foundMaterialKvp.Value.Properties.FirstOrDefault(propKvp => 
                                _hashResolverService.ResolveBinHashGeneral(propKvp.Key).Equals("samplerValues", StringComparison.OrdinalIgnoreCase)
                            );

                            if (samplerValuesKvp.Value is BinTreeContainer samplerValuesContainer && samplerValuesContainer.Elements.Any())
                            {
                                foreach (var samplerElement in samplerValuesContainer.Elements)
                                {
                                    if (samplerElement is BinTreeStruct samplerStruct)
                                    {
                                        var textureNamePropKvp = samplerStruct.Properties.FirstOrDefault(propKvp => 
                                            _hashResolverService.ResolveBinHashGeneral(propKvp.Key).Equals("TextureName", StringComparison.OrdinalIgnoreCase)
                                        );

                                        if (textureNamePropKvp.Value is BinTreeString textureNameString &&
                                            (textureNameString.Value.Equals("Diffuse_Texture", StringComparison.OrdinalIgnoreCase) ||
                                             textureNameString.Value.Equals("DiffuseTexture", StringComparison.OrdinalIgnoreCase)))
                                        {
                                            var texturePathKvp = samplerStruct.Properties.FirstOrDefault(propKvp => 
                                                _hashResolverService.ResolveBinHashGeneral(propKvp.Key).Equals("texturePath", StringComparison.OrdinalIgnoreCase)
                                            );

                                            if (texturePathKvp.Value is BinTreeString texturePathString && !string.IsNullOrEmpty(texturePathString.Value))
                                            {
                                                fullTexturePath = texturePathString.Value;
                                                textureNameKey = Path.GetFileNameWithoutExtension(fullTexturePath);
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                             _logService.Log($"Material '{materialName}' not found in materials.bin.objects.");
                        }
                    }

                    if (!string.IsNullOrEmpty(fullTexturePath) && !loadedTextures.ContainsKey(textureNameKey))
                    {
                        string normalizedTexturePath = fullTexturePath.Replace('\\', '/').ToLower();
                        try
                        {
                            byte[] textureBytes = await GetTextureBytesFromWadAsync(normalizedTexturePath, gameDataPath);
                            if (textureBytes != null && textureBytes.Length > 0)
                            {
                                using (MemoryStream ms = new MemoryStream(textureBytes))
                                {
                                    BitmapSource loadedTex = LoadTextureFromStream(ms, Path.GetExtension(normalizedTexturePath));
                                    if (loadedTex != null)
                                    {
                                        loadedTextures[textureNameKey] = loadedTex;
                                        if (!availableTextureNames.Contains(textureNameKey))
                                        {
                                            availableTextureNames.Add(textureNameKey);
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logService.LogError(ex, $"Failed to load texture '{fullTexturePath}' (normalized: '{normalizedTexturePath}') using WadExtractionService.");
                        }
                    }

                    string initialMatchingKey = null;
                    if (!string.IsNullOrEmpty(textureNameKey) && loadedTextures.ContainsKey(textureNameKey))
                    {
                        initialMatchingKey = textureNameKey;
                    }
                    else
                    {
                         _logService.LogWarning($"Could not find or load texture for material '{materialName}'.");
                    }

                    var geometryModel = new GeometryModel3D(meshGeometry, new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Colors.Magenta)));

                    var modelPart = new ModelPart
                    {
                        Name = string.IsNullOrEmpty(materialName) ? "Default" : materialName,
                        Visual = new ModelVisual3D(),
                        AllTextures = loadedTextures,
                        AvailableTextureNames = availableTextureNames,
                        SelectedTextureName = initialMatchingKey,
                        Geometry = geometryModel
                    };

                    modelPart.Visual.Content = geometryModel;
                    modelPart.UpdateMaterial();

                    sceneModel.Parts.Add(modelPart);
                    sceneModel.RootVisual.Children.Add(modelPart.Visual);
                } 
            }       
            _logService.LogDebug("--- Finished displaying model ---");
            return sceneModel;
        }

        #endregion Map Geometry Loading

        #region Shared Texture Loading

        private string FindBestTextureMatch(string materialName, string skinName, IEnumerable<string> availableTextureKeys, string defaultTextureKey)
        {
            _logService.LogDebug($"Finding texture for material: '{materialName}'");

            // 1. Exact match
            string exactMatch = availableTextureKeys.FirstOrDefault(key => key.Equals(materialName, StringComparison.OrdinalIgnoreCase));
            if (exactMatch != null)
            {
                _logService.LogDebug($"Found texture '{exactMatch}' via exact name match.");
                return exactMatch;
            }

            // 2. Generic material override
            var genericMaterialNames = new List<string> { "body", "face", "head", "eyes" };
            if (genericMaterialNames.Contains(materialName.ToLower()))
            {
                string mainTextureCandidate = $"{skinName}_tx_cm";
                string genericMatch = availableTextureKeys.FirstOrDefault(key => key.Equals(mainTextureCandidate, StringComparison.OrdinalIgnoreCase));
                if (genericMatch != null)
                {
                    _logService.LogDebug($"Found main texture '{genericMatch}' for generic material '{materialName}'.");
                    return genericMatch;
                }
            }

            // 3. Keyword-based scoring match
            _logService.LogDebug("No exact or generic match found. Trying keyword-based scoring...");
            var materialKeywords = materialName.ToLower().Split('_', '-', ' ').Where(k => k != "mat").ToList();

            string bestScoringMatch = null;
            int bestScore = 0;

            foreach (string key in availableTextureKeys)
            {
                string lowerKey = key.ToLower();
                int currentScore = 0;

                foreach (string keyword in materialKeywords)
                {
                    if (string.IsNullOrWhiteSpace(keyword)) continue;
                    if (lowerKey.Contains(keyword))
                    {
                        currentScore++;
                    }
                }

                if (currentScore > bestScore)
                {
                    bestScore = currentScore;
                    bestScoringMatch = key;
                }
                else if (currentScore > 0 && currentScore == bestScore)
                {
                    // Tie-breaking logic: prefer textures with _tx_cm, then shorter names
                    bool bestIsTxCm = bestScoringMatch?.ToLower().Contains("_tx_cm") ?? false;
                    bool currentIsTxCm = lowerKey.Contains("_tx_cm");

                    if (currentIsTxCm && !bestIsTxCm)
                    {
                        bestScoringMatch = key; // Current is preferred
                    }
                    else if (!currentIsTxCm && bestIsTxCm)
                    {
                        // Best is preferred, do nothing
                    }
                    else if (bestScoringMatch == null || key.Length < bestScoringMatch.Length)
                    {
                        // If both or neither are _tx_cm, fall back to shorter name
                        bestScoringMatch = key;
                    }
                }
            }

            if (bestScoringMatch != null)
            {
                _logService.LogDebug($"Found texture '{bestScoringMatch}' with score {bestScore} via keyword matching.");
                return bestScoringMatch;
            }

            // Fallback
            _logService.LogDebug($"No texture found. Falling back to default: '{defaultTextureKey}'");
            return defaultTextureKey;
        }

        #endregion SKN Model Loading

        #region Map Geometry Loading
    }
}
