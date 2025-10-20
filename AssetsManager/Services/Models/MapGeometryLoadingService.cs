using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using LeagueToolkit.Core.Environment;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Memory;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models;
using AssetsManager.Utils;

namespace AssetsManager.Services.Models
{
    public class MapGeometryLoadingService
    {
        private readonly LogService _logService;
        private readonly HashResolverService _hashResolverService;
        private readonly WadExtractionService _wadExtractionService;
        private readonly WadNodeLoaderService _wadNodeLoaderService;

        public MapGeometryLoadingService(LogService logService, HashResolverService hashResolverService, WadExtractionService wadExtractionService, WadNodeLoaderService wadNodeLoaderService)
        {
            _logService = logService;
            _hashResolverService = hashResolverService;
            _wadExtractionService = wadExtractionService;
            _wadNodeLoaderService = wadNodeLoaderService;
        }

        public async Task<SceneModel> LoadMapGeometry(string filePath, string gameDataPath)
        {
            return await LoadMapGeometryInternal(filePath, null, gameDataPath);
        }

        public async Task<SceneModel> LoadMapGeometry(string filePath, string materialsPath, string gameDataPath)
        {
            try
            {
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
                using (var mapGeometry = new EnvironmentAsset(stream))
                {
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
            var sceneModel = new SceneModel { Name = modelName };

            var processingResult = await Task.Run(() =>
            {
                var dataList = new List<SubmeshData>();
                foreach (var mesh in mapGeometry.Meshes)
                {
                    foreach (var submesh in mesh.Submeshes)
                    {
                        string materialName = submesh.Material.TrimEnd('\0');

                        var positions = mesh.VerticesView.GetAccessor(VertexElement.POSITION.Name).AsVector3Array();
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

                        var texCoordAccessor = mesh.VerticesView.GetAccessor(VertexElement.TEXCOORD_0.Name);
                        var subTexCoords = new Point[submesh.VertexCount];
                        if (texCoordAccessor.Element.Format == ElementFormat.XY_Packed1616)
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

                        string fullTexturePath = null;
                        if (materialsBin != null)
                        {
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
                                        if (samplerElement is BinTreeStruct samplerObject)
                                        {
                                            var textureNamePropKvp = samplerObject.Properties.FirstOrDefault(propKvp =>
                                                _hashResolverService.ResolveBinHashGeneral(propKvp.Key).Equals("TextureName", StringComparison.OrdinalIgnoreCase)
                                            );

                                            if (textureNamePropKvp.Value is BinTreeString textureNameString &&
                                                (textureNameString.Value.Equals("Diffuse_Texture", StringComparison.OrdinalIgnoreCase) ||
                                                 textureNameString.Value.Equals("DiffuseTexture", StringComparison.OrdinalIgnoreCase) ||
                                                 textureNameString.Value.Equals("ColorTexture", StringComparison.OrdinalIgnoreCase)))
                                            {
                                                var texturePathKvp = samplerObject.Properties.FirstOrDefault(propKvp =>
                                                    _hashResolverService.ResolveBinHashGeneral(propKvp.Key).Equals("texturePath", StringComparison.OrdinalIgnoreCase)
                                                );

                                                if (texturePathKvp.Value is BinTreeString tps && !string.IsNullOrEmpty(tps.Value))
                                                {
                                                    fullTexturePath = tps.Value;
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        dataList.Add(new SubmeshData(materialName, subPositions, triangleIndices, subTexCoords, fullTexturePath));
                    }
                }

                var loadedTextures = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
                foreach (var data in dataList)
                {
                    if (!string.IsNullOrEmpty(data.TexturePath))
                    {
                        string textureNameKey = Path.GetFileNameWithoutExtension(data.TexturePath);
                        if (!loadedTextures.ContainsKey(textureNameKey))
                        {
                            string absoluteFilePath = Path.Combine(gameDataPath, data.TexturePath.Replace('\\', '/'));
                            if (File.Exists(absoluteFilePath))
                            {
                                try
                                {
                                    using (Stream fileStream = File.OpenRead(absoluteFilePath))
                                    {
                                        BitmapSource loadedTex = TextureUtils.LoadTexture(fileStream, Path.GetExtension(absoluteFilePath));
                                        if (loadedTex != null)
                                        {
                                            loadedTex.Freeze(); // Freeze the texture to make it thread-safe
                                            loadedTextures[textureNameKey] = loadedTex;
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logService.LogError(ex, $"Failed to load texture file: {absoluteFilePath}");
                                }
                            }
                        }
                    }
                }

                return new { SubmeshDataList = dataList, LoadedTextures = loadedTextures };
            });

            var submeshDataList = processingResult.SubmeshDataList;
            var loadedTextures = processingResult.LoadedTextures;
            var availableTextureNames = new ObservableCollection<string>(loadedTextures.Keys);

            foreach (var data in submeshDataList)
            {
                var meshGeometry = new MeshGeometry3D
                {
                    Positions = new Point3DCollection(data.Positions),
                    TriangleIndices = new Int32Collection(data.TriangleIndices),
                    TextureCoordinates = new PointCollection(data.TextureCoordinates)
                };

                string textureNameKey = string.IsNullOrEmpty(data.TexturePath) ? null : Path.GetFileNameWithoutExtension(data.TexturePath);
                
                if (string.IsNullOrEmpty(textureNameKey) || !loadedTextures.ContainsKey(textureNameKey))
                {
                    _logService.LogWarning($"Could not find or load texture for material '{data.MaterialName}'.");
                }

                var geometryModel = new GeometryModel3D(meshGeometry, new DiffuseMaterial(new SolidColorBrush(System.Windows.Media.Colors.Magenta)));

                var modelPart = new ModelPart
                {
                    Name = string.IsNullOrEmpty(data.MaterialName) ? "Default" : data.MaterialName,
                    Visual = new ModelVisual3D(),
                    AllTextures = loadedTextures,
                    AvailableTextureNames = availableTextureNames,
                    SelectedTextureName = textureNameKey,
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
