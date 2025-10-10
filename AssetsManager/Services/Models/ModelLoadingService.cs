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

namespace AssetsManager.Services.Models
{
    public class ModelLoadingService
    {
        private readonly LogService _logService;

        public ModelLoadingService(LogService logService)
        {
            _logService = logService;
        }

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

                string initialMatchingKey = FindBestTextureMatch(materialName, loadedTextures.Keys, defaultTextureKey);

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

        private string FindBestTextureMatch(string materialName, IEnumerable<string> availableTextureKeys, string defaultTextureKey)
        {
            _logService.LogDebug($"Finding texture for material: '{materialName}'");

            // 1. FNV1a hash match (MindCorpViewer's method)
            uint materialHash = Fnv1aHasher.Hash(materialName);
            string hashedKey = materialHash.ToString();
            _logService.LogDebug($"Calculated FNV1a hash: '{hashedKey}' for material '{materialName}'");

            string hashMatch = availableTextureKeys.FirstOrDefault(key => key.Equals(hashedKey, StringComparison.OrdinalIgnoreCase));
            if (hashMatch != null)
            {
                _logService.LogDebug($"Found texture '{hashMatch}' via FNV1a hash.");
                return hashMatch;
            }

            // 2. Exact match
            string exactMatch = availableTextureKeys.FirstOrDefault(key => key.Equals(materialName, StringComparison.OrdinalIgnoreCase));
            if (exactMatch != null)
            {
                _logService.LogDebug($"Found texture '{exactMatch}' via exact name match.");
                return exactMatch;
            }

            // 3. Keyword-based scoring match
            _logService.LogDebug("No hash or exact match found. Trying keyword-based scoring...");
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
    }
}
