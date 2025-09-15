using System.Collections.Generic;
using System.Collections.ObjectModel;
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
}
