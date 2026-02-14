using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Viewer;

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
        public SceneModel LoadModel(string filePath, string textureDirectoryPath)
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

                _logService.LogDebug($"Loaded model (with custom textures): {Path.GetFileNameWithoutExtension(filePath)}");
                return CreateSceneModel(skinnedMesh, loadedTextures, Path.GetFileNameWithoutExtension(filePath));
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to load model with custom textures");
                return null;
            }
        }

        // Este método carga un modelo SKN y sus texturas desde el mismo directorio del archivo SKN (comportamiento estándar).
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

                var loadedTextures = LoadTexturesFromDirectory(modelDirectory);

                _logService.LogDebug($"Loaded model: {Path.GetFileNameWithoutExtension(filePath)}");
                return CreateSceneModel(skinnedMesh, loadedTextures, Path.GetFileNameWithoutExtension(filePath));
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
            string[] textureFiles = Directory.GetFiles(directoryPath, "*.tex", SearchOption.TopDirectoryOnly);

            foreach (string texPath in textureFiles)
            {
                try
                {
                    using (Stream fileStream = File.OpenRead(texPath))
                    {
                        BitmapSource loadedTex = TextureUtils.LoadTexture(fileStream, Path.GetExtension(texPath));
                        if (loadedTex != null)
                        {
                            string textureKey = Path.GetFileName(texPath).Split('.')[0];
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

                var positions = skinnedMesh.VerticesView.GetAccessor(VertexElement.POSITION.Name).AsVector3Array();
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

                var texCoords = skinnedMesh.VerticesView.GetAccessor(VertexElement.TEXCOORD_0.Name).AsVector2Array();
                var subTexCoords = new System.Windows.Point[rangeObj.VertexCount];
                for (int i = 0; i < rangeObj.VertexCount; i++)
                {
                    var uv = texCoords[rangeObj.StartVertex + i];
                    subTexCoords[i] = new System.Windows.Point(uv.X, uv.Y);
                }
                meshGeometry.TextureCoordinates = new PointCollection(subTexCoords);

                string initialMatchingKey = TextureUtils.FindBestTextureMatch(materialName, skinName, loadedTextures.Keys, defaultTextureKey, _logService);

                var geometryModel = new GeometryModel3D(meshGeometry, new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Colors.Black)));

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
                TextureUtils.UpdateMaterial(modelPart);

                sceneModel.Parts.Add(modelPart);
                sceneModel.RootVisual.Children.Add(modelPart.Visual);
            }
            _logService.LogDebug("--- Finished displaying model ---");
            return sceneModel;
        }
    }
}
