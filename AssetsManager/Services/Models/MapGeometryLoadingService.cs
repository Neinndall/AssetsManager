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
using AssetsManager.Utils;
using LeagueToolkit.Core.Environment;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Renderer;
using LeagueToolkit.Toolkit;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models;

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
                            byte[] textureBytes = await GetTextureBytesAsync(normalizedTexturePath, gameDataPath);
                            if (textureBytes != null && textureBytes.Length > 0)
                            {
                                using (MemoryStream ms = new MemoryStream(textureBytes))
                                {
                                    BitmapSource loadedTex = TextureUtils.LoadTexture(ms, Path.GetExtension(normalizedTexturePath));
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


        private async Task<byte[]> GetTextureBytesAsync(string texturePath, string gameDataPath)
        {
            try
            {
                string absoluteFilePath = Path.Combine(gameDataPath, texturePath);

                if (File.Exists(absoluteFilePath))
                {
                    return await File.ReadAllBytesAsync(absoluteFilePath);
                }

                _logService.LogError($"Texture file not found at: {absoluteFilePath}");
                return null;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to get texture bytes for path: {texturePath}");
                return null;
            }
        }
    }
}
