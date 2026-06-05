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

                _logService.LogDebug($"Loaded model (with custom textures): {Path.GetFileNameWithoutExtension(filePath)}");
                return await CreateSceneModel(skinnedMesh, loadedTextures, Path.GetFileNameWithoutExtension(filePath));
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

                _logService.LogDebug($"Loaded model: {Path.GetFileNameWithoutExtension(filePath)}");
                return await CreateSceneModel(skinnedMesh, loadedTextures, Path.GetFileNameWithoutExtension(filePath));
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
                            loadedTex.Freeze();
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

        private async Task<SceneModel> CreateSceneModel(SkinnedMesh skinnedMesh, Dictionary<string, BitmapSource> loadedTextures, string modelName)
        {
            var availableTextureNames = new ObservableRangeCollection<string>(loadedTextures.Keys);

            string defaultTextureKey = loadedTextures.Keys
                .Where(k => k.EndsWith("_tx_cm", StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k.Length)
                .FirstOrDefault();

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

                    var subPositions = new Point3D[rangeObj.VertexCount];
                    for (int i = 0; i < rangeObj.VertexCount; i++)
                    {
                        var p = positions[rangeObj.StartVertex + i];
                        subPositions[i] = new Point3D(p.X, p.Y, p.Z);
                    }

                    var triangleIndices = new int[rangeObj.IndexCount];
                    var subIndices = indices.Slice(rangeObj.StartIndex, rangeObj.IndexCount);
                    for (int i = 0; i < rangeObj.IndexCount; i++)
                    {
                        triangleIndices[i] = (int)subIndices[i] - rangeObj.StartVertex;
                    }

                    var subTexCoords = new System.Windows.Point[rangeObj.VertexCount];
                    for (int i = 0; i < rangeObj.VertexCount; i++)
                    {
                        var uv = texCoords[rangeObj.StartVertex + i];
                        subTexCoords[i] = new System.Windows.Point(uv.X, uv.Y);
                    }

                    string initialMatchingKey = TextureUtils.FindBestTextureMatch(materialName, skinName, loadedTextures.Keys, defaultTextureKey, _logService);
                    list.Add(new SubmeshData(materialName, subPositions, triangleIndices, subTexCoords, initialMatchingKey));
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
                    MeshGeometry3D meshGeometry = new MeshGeometry3D
                    {
                        Positions = new Point3DCollection(data.Positions),
                        TriangleIndices = new Int32Collection(data.TriangleIndices),
                        TextureCoordinates = new PointCollection(data.TextureCoordinates)
                    };

                    var geometryModel = new GeometryModel3D(meshGeometry, new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Colors.Black)));

                    var modelPart = new ModelPart
                    {
                        Name = string.IsNullOrEmpty(data.MaterialName) ? "Default" : data.MaterialName,
                        Visual = new ModelVisual3D(),
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

        private record SubmeshData(string MaterialName, Point3D[] Positions, int[] TriangleIndices, System.Windows.Point[] TextureCoordinates, string TexturePath);
    }
}
