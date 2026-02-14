using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Media3D;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Mesh;

namespace AssetsManager.Views.Models.Viewer
{
    public class SceneModel : INotifyPropertyChanged, IDisposable
    {
        public string Name { get; set; }
        public SkinnedMesh SkinnedMesh { get; set; }
        public ModelVisual3D RootVisual { get; set; }
        public TranslateTransform3D Transform { get; set; }

        private ObservableCollection<ModelPart> _parts;
        public ObservableCollection<ModelPart> Parts
        {
            get => _parts;
            set
            {
                if (_parts != null)
                {
                    _parts.CollectionChanged -= Parts_CollectionChanged;
                }
                _parts = value;
                if (_parts != null)
                {
                    _parts.CollectionChanged += Parts_CollectionChanged;
                }
                OnPropertyChanged();
            }
        }

        public ObservableCollection<AnimationData> Animations { get; set; }

        public RigResource Skeleton { get; set; }
        public IAnimationAsset CurrentAnimation { get; set; }
        public bool IsAnimationPaused { get; set; }
        public double AnimationTime { get; set; }

        private bool _areAllPartsVisible = true;
        public bool AreAllPartsVisible
        {
            get => _areAllPartsVisible;
            set
            {
                if (SetField(ref _areAllPartsVisible, value))
                {
                    foreach (var part in Parts)
                    {
                        part.IsVisible = value;
                    }
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        public SceneModel()
        {
            Name = "New Model";
            RootVisual = new ModelVisual3D();
            Transform = new TranslateTransform3D();
            RootVisual.Transform = this.Transform;
            Parts = new ObservableCollection<ModelPart>();
            Animations = new ObservableCollection<AnimationData>();
        }

        private void Parts_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (ModelPart item in e.NewItems)
                {
                    item.PropertyChanged += Part_PropertyChanged;
                }
            }

            if (e.OldItems != null)
            {
                foreach (ModelPart item in e.OldItems)
                {
                    item.PropertyChanged -= Part_PropertyChanged;
                }
            }
            UpdateMasterVisibility();
        }

        private void Part_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ModelPart.IsVisible))
            {
                UpdateMasterVisibility();
            }
        }

        private void UpdateMasterVisibility()
        {
            // This avoids re-triggering the setter loop
            var allVisible = Parts.All(p => p.IsVisible);
            SetField(ref _areAllPartsVisible, allVisible, nameof(AreAllPartsVisible));
        }

        public void Dispose()
        {
            // Limpiar children del RootVisual
            RootVisual?.Children.Clear();

            // Limpiar Parts (geometrías y texturas)
            if (Parts != null)
            {
                Parts.CollectionChanged -= Parts_CollectionChanged;
                foreach (var part in Parts)
                {
                    part.PropertyChanged -= Part_PropertyChanged;
                    part.Dispose();
                }
                Parts.Clear();
            }

            // Limpiar animaciones
            Animations.Clear();

            CurrentAnimation = null;
            IsAnimationPaused = false;
            AnimationTime = 0;

            // Limpiar referencias
            Skeleton = null;
            SkinnedMesh = null;
            RootVisual = null;
            Transform = null;
        }
    }
}
