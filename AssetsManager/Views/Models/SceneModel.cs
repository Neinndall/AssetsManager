using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Media3D;
using LeagueToolkit.Core.Mesh;

namespace AssetsManager.Views.Models
{
    public class SceneModel
    {
        public string Name { get; set; }
        public SkinnedMesh SkinnedMesh { get; set; }
        public ModelVisual3D RootVisual { get; set; }
        public TranslateTransform3D Transform { get; set; }
        public ObservableCollection<ModelPart> Parts { get; set; }

        public SceneModel()
        {
            Name = "New Model";
            RootVisual = new ModelVisual3D();
            Transform = new TranslateTransform3D();
            RootVisual.Transform = this.Transform;
            Parts = new ObservableCollection<ModelPart>();
        }
    }

    public readonly struct SubmeshData
    {
        public readonly string MaterialName;
        public readonly Point3D[] Positions;
        public readonly int[] TriangleIndices;
        public readonly Point[] TextureCoordinates;
        public readonly string TexturePath;

        public SubmeshData(string materialName, Point3D[] positions, int[] triangleIndices, Point[] textureCoordinates, string texturePath)
        {
            MaterialName = materialName;
            Positions = positions;
            TriangleIndices = triangleIndices;
            TextureCoordinates = textureCoordinates;
            TexturePath = texturePath;
        }
    }
}
