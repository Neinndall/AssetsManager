using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Media3D;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Core.Animation;
using AssetsManager.Views.Models;

namespace AssetsManager.Views.Models
{
    public class SceneModel : IDisposable
    {
        public string Name { get; set; }
        public SkinnedMesh SkinnedMesh { get; set; }
        public ModelVisual3D RootVisual { get; set; }
        public TranslateTransform3D Transform { get; set; }
        public ObservableCollection<ModelPart> Parts { get; set; }
        public ObservableCollection<AnimationData> Animations { get; set; }
        public ObservableCollection<string> AnimationNames { get; set; }

        public SceneModel()
        {
            Name = "New Model";
            RootVisual = new ModelVisual3D();
            Transform = new TranslateTransform3D();
            RootVisual.Transform = this.Transform;
            Parts = new ObservableCollection<ModelPart>();
            Animations = new ObservableCollection<AnimationData>();
            AnimationNames = new ObservableCollection<string>();
        }

        public void Dispose()
        {
            // Limpiar children del RootVisual
            RootVisual?.Children.Clear();
            
            // Limpiar Parts (geometrías y texturas)
            if (Parts != null)
            {
                foreach (var part in Parts)
                {
                    part.Dispose();
                }
                Parts.Clear();
            }

            // Limpiar animaciones
            Animations.Clear();
            AnimationNames.Clear();
            
            // Limpiar referencias
            SkinnedMesh = null;
            RootVisual = null;
            Transform = null;
        }
    }
}