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
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Mesh;

namespace AssetsManager.Services.Viewer
{
    public class ScoLoadingService
    {
        private readonly LogService _logService;

        public ScoLoadingService(LogService logService)
        {
            _logService = logService;
        }

        public async Task<SceneModel> LoadModel(string filePath)
        {
            try
            {
                StaticMesh staticMesh;
                using (var stream = File.OpenRead(filePath))
                {
                    byte[] header = new byte[12];
                    if (stream.Read(header, 0, 12) < 8)
                        throw new Exception("File too short");
                    
                    stream.Position = 0;
                    string magic = System.Text.Encoding.ASCII.GetString(header, 0, 8);

                    if (magic == "r3d2Mesh")
                    {
                        staticMesh = StaticMesh.ReadBinary(stream);
                    }
                    else if (System.Text.Encoding.ASCII.GetString(header, 0, 11) == "[ObjectBegin")
                    {
                        staticMesh = StaticMesh.ReadAscii(stream);
                    }
                    else
                    {
                         using (StreamReader reader = new StreamReader(stream, System.Text.Encoding.ASCII, false, 1024, true))
                         {
                             string firstLine = reader.ReadLine();
                             stream.Position = 0;
                             if (firstLine != null && firstLine.Trim() == "[ObjectBegin]")
                             {
                                 staticMesh = StaticMesh.ReadAscii(stream);
                             }
                             else
                             {
                                 throw new Exception("Unknown file format signature.");
                             }
                         }
                    }
                }

                string modelDirectory = Path.GetDirectoryName(filePath);
                var loadedTextures = LoadTexturesFromDirectory(modelDirectory);

                _logService.LogDebug($"Loaded SCO/SCB model: {staticMesh.Name}");
                return await CreateSceneModel(staticMesh, loadedTextures, Path.GetFileNameWithoutExtension(filePath));
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to load SCO/SCB model: {filePath}");
                return null;
            }
        }

        private Dictionary<string, BitmapSource> LoadTexturesFromDirectory(string directoryPath)
        {
            var loadedTextures = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath)) return loadedTextures;

            var allFiles = Directory.GetFiles(directoryPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(s => s.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

            foreach (string texPath in allFiles)
            {
                try
                {
                    using (Stream fileStream = File.OpenRead(texPath))
                    {
                        BitmapSource loadedTex = TextureUtils.LoadViewerTexture(fileStream, Path.GetExtension(texPath));
                        if (loadedTex != null)
                        {
                            string textureKey = Path.GetFileNameWithoutExtension(texPath);
                            if (!loadedTextures.ContainsKey(textureKey))
                                loadedTextures[textureKey] = loadedTex;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogWarning($"Failed to load texture: {texPath}. Error: {ex.Message}");
                }
            }
            return loadedTextures;
        }

        private async Task<SceneModel> CreateSceneModel(StaticMesh staticMesh, Dictionary<string, BitmapSource> loadedTextures, string modelName)
        {
            var availableTextureNames = new ObservableRangeCollection<string>(loadedTextures.Keys.Select(k => PathUtils.TruncateAtDot(k)));
            var colorTextureKeys = TextureUtils.GetColorTextureCandidates(loadedTextures.Keys);
            string defaultTextureKey = colorTextureKeys.FirstOrDefault();

            var dataList = await Task.Run(() =>
            {
                var list = new List<SubmeshData>();
                var facesByMaterial = staticMesh.Faces.GroupBy(f => f.Material.TrimEnd('\0'));

                foreach (var group in facesByMaterial)
                {
                    string materialName = group.Key;
                    if (string.IsNullOrEmpty(materialName)) materialName = "Default";

                    int faceCount = group.Count();
                    var subPositions = new Point3D[faceCount * 3];
                    var subTexCoords = new System.Windows.Point[faceCount * 3];
                    var triangleIndices = new int[faceCount * 3];

                    int i = 0;
                    foreach (var face in group)
                    {
                        var v0 = staticMesh.Vertices[face.VertexId0];
                        var v1 = staticMesh.Vertices[face.VertexId1];
                        var v2 = staticMesh.Vertices[face.VertexId2];

                        subPositions[i] = new Point3D(v0.X, v0.Y, v0.Z);
                        subPositions[i + 1] = new Point3D(v1.X, v1.Y, v1.Z);
                        subPositions[i + 2] = new Point3D(v2.X, v2.Y, v2.Z);

                        subTexCoords[i] = new System.Windows.Point(face.UV0.X, face.UV0.Y);
                        subTexCoords[i + 1] = new System.Windows.Point(face.UV1.X, face.UV1.Y);
                        subTexCoords[i + 2] = new System.Windows.Point(face.UV2.X, face.UV2.Y);

                        triangleIndices[i] = i;
                        triangleIndices[i + 1] = i + 1;
                        triangleIndices[i + 2] = i + 2;

                        i += 3;
                    }

                    string initialMatchingKey = TextureUtils.FindBestTextureMatch(materialName, modelName, colorTextureKeys, defaultTextureKey, _logService);
                    list.Add(new SubmeshData(materialName, subPositions, triangleIndices, subTexCoords, initialMatchingKey));
                }
                return list;
            });

            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var sceneModel = new SceneModel { Name = modelName };
                foreach (var data in dataList)
                {
                    var positionsCol = new Point3DCollection(data.Positions);
                    var texCoordsCol = new PointCollection(data.TextureCoordinates);
                    var indicesCol = new Int32Collection(data.TriangleIndices);

                    if (positionsCol.CanFreeze) positionsCol.Freeze();
                    if (texCoordsCol.CanFreeze) texCoordsCol.Freeze();
                    if (indicesCol.CanFreeze) indicesCol.Freeze();

                    MeshGeometry3D meshGeometry = new MeshGeometry3D
                    {
                        Positions = positionsCol,
                        TextureCoordinates = texCoordsCol,
                        TriangleIndices = indicesCol
                    };

                    var geometryModel = new GeometryModel3D(meshGeometry, new DiffuseMaterial(new SolidColorBrush(Colors.White)));

                    var modelPart = new ModelPart
                    {
                        Name = data.MaterialName,
                        Visual = new ModelVisual3D(),
                        AllTextures = loadedTextures,
                        AvailableTextureNames = availableTextureNames,
                        SelectedTextureName = data.TexturePath,
                        Geometry = geometryModel
                    };

                    modelPart.Visual.Content = geometryModel;
                    TextureUtils.UpdateMaterial(modelPart, false);

                    sceneModel.Parts.Add(modelPart);
                    sceneModel.RootVisual.Children.Add(modelPart.Visual);
                }

                return sceneModel;
            });
        }

        private record SubmeshData(string MaterialName, Point3D[] Positions, int[] TriangleIndices, System.Windows.Point[] TextureCoordinates, string TexturePath);
    }
}
