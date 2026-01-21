using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Models3D;
using LeagueToolkit.Core.Mesh;

namespace AssetsManager.Services.Models
{
    public class ScoModelLoadingService
    {
        private readonly LogService _logService;

        public ScoModelLoadingService(LogService logService)
        {
            _logService = logService;
        }

        public SceneModel LoadModel(string filePath)
        {
            try
            {
                StaticMesh staticMesh;
                using (var stream = File.OpenRead(filePath))
                {
                    // Detect signature to decide between Binary (.scb) or Ascii (.sco)
                    byte[] header = new byte[12];
                    if (stream.Read(header, 0, 12) < 8)
                        throw new Exception("File too short");
                    
                    stream.Position = 0;
                    string magic = System.Text.Encoding.ASCII.GetString(header, 0, 8);

                    if (magic == "r3d2Mesh")
                    {
                        staticMesh = StaticMesh.ReadBinary(stream);
                    }
                    else if (System.Text.Encoding.ASCII.GetString(header, 0, 11) == "[ObjectBegin") // Usually "[ObjectBegin]"
                    {
                        staticMesh = StaticMesh.ReadAscii(stream);
                    }
                    else
                    {
                         // Fallback heuristic: try ascii if it starts with text
                         // But for now, let's assume standard format. 
                         // Check strictly for [ObjectBegin]
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
                                 // Try binary as fallback? Or assume it failed.
                                 // Let's assume it failed signature check.
                                 throw new Exception("Unknown file format signature.");
                             }
                         }
                    }
                }

                string modelDirectory = Path.GetDirectoryName(filePath);
                var loadedTextures = LoadTexturesFromDirectory(modelDirectory);

                _logService.LogDebug($"Loaded SCO/SCB model: {staticMesh.Name}");
                return CreateSceneModel(staticMesh, loadedTextures, Path.GetFileNameWithoutExtension(filePath));
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to load SCO/SCB model: {filePath}");
                return null;
            }
        }

        private Dictionary<string, BitmapSource> LoadTexturesFromDirectory(string directoryPath)
        {
            // Reusing the same logic from SknModelLoadingService ideally, but copying here for independence
            var loadedTextures = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath)) return loadedTextures;

            string[] textureFiles = Directory.GetFiles(directoryPath, "*.tex", SearchOption.TopDirectoryOnly); // Also .dds?
             // LeagueToolkit might handle .dds inside LoadTexture?
             // Let's stick to what SknModelLoadingService does: *.tex. 
             // Actually, usually old models use .dds or .tga. TextureUtils.LoadTexture handles extensions.
             // Let's add dds just in case, as old sco files might be paired with dds.
            
            var allFiles = Directory.GetFiles(directoryPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(s => s.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".dds", StringComparison.OrdinalIgnoreCase));

            foreach (string texPath in allFiles)
            {
                try
                {
                    using (Stream fileStream = File.OpenRead(texPath))
                    {
                        BitmapSource loadedTex = TextureUtils.LoadTexture(fileStream, Path.GetExtension(texPath));
                        if (loadedTex != null)
                        {
                            string textureKey = Path.GetFileName(texPath).Split('.')[0];
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

        private SceneModel CreateSceneModel(StaticMesh staticMesh, Dictionary<string, BitmapSource> loadedTextures, string modelName)
        {
            var sceneModel = new SceneModel { Name = modelName };
            var availableTextureNames = new ObservableCollection<string>(loadedTextures.Keys);
            
            // Group faces by material to create submeshes
            var facesByMaterial = staticMesh.Faces.GroupBy(f => f.Material.TrimEnd('\0'));

            foreach (var group in facesByMaterial)
            {
                string materialName = group.Key;
                if (string.IsNullOrEmpty(materialName)) materialName = "Default";

                MeshGeometry3D meshGeometry = new MeshGeometry3D();
                Point3DCollection positions = new Point3DCollection();
                PointCollection textureCoordinates = new PointCollection();
                Int32Collection triangleIndices = new Int32Collection();

                int currentIndex = 0;

                foreach (var face in group)
                {
                    // Vertex 0
                    var v0 = staticMesh.Vertices[face.VertexId0];
                    positions.Add(new Point3D(v0.X, v0.Y, v0.Z));
                    textureCoordinates.Add(new System.Windows.Point(face.UV0.X, face.UV0.Y));
                    triangleIndices.Add(currentIndex++);

                    // Vertex 1
                    var v1 = staticMesh.Vertices[face.VertexId1];
                    positions.Add(new Point3D(v1.X, v1.Y, v1.Z));
                    textureCoordinates.Add(new System.Windows.Point(face.UV1.X, face.UV1.Y));
                    triangleIndices.Add(currentIndex++);

                    // Vertex 2
                    var v2 = staticMesh.Vertices[face.VertexId2];
                    positions.Add(new Point3D(v2.X, v2.Y, v2.Z));
                    textureCoordinates.Add(new System.Windows.Point(face.UV2.X, face.UV2.Y));
                    triangleIndices.Add(currentIndex++);
                }

                meshGeometry.Positions = positions;
                meshGeometry.TextureCoordinates = textureCoordinates;
                meshGeometry.TriangleIndices = triangleIndices;

                // Attempt to find a matching texture
                // SCO files usually have material names that match texture names directly
                string initialMatchingKey = TextureUtils.FindBestTextureMatch(materialName, modelName, loadedTextures.Keys, null, _logService);

                var geometryModel = new GeometryModel3D(meshGeometry, new DiffuseMaterial(new SolidColorBrush(Colors.White))); // White brush initially

                var modelPart = new ModelPart
                {
                    Name = materialName,
                    Visual = new ModelVisual3D(),
                    AllTextures = loadedTextures,
                    AvailableTextureNames = availableTextureNames,
                    SelectedTextureName = initialMatchingKey,
                    Geometry = geometryModel
                };

                modelPart.Visual.Content = geometryModel;
                TextureUtils.UpdateMaterial(modelPart); // Applies the texture if found

                sceneModel.Parts.Add(modelPart);
                sceneModel.RootVisual.Children.Add(modelPart.Visual);
            }

            return sceneModel;
        }
    }
}
