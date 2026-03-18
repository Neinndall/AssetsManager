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
using AssetsManager.Utils.Framework;

namespace AssetsManager.Views.Models.Viewer
{
    public class SceneModel : INotifyPropertyChanged, IDisposable
    {
        public string Name { get; set; }
        public SkinnedMesh SkinnedMesh { get; set; }
        public ModelVisual3D RootVisual { get; set; }

        // --- Transformation Properties ---
        private double _positionX;
        private double _positionY;
        private double _positionZ;
        private double _rotationX;
        private double _rotationY;
        private double _rotationZ;
        private double _scale = 1.0;

        public double PositionX { get => _positionX; set { if (SetField(ref _positionX, value)) UpdateTransform(); } }
        public double PositionY { get => _positionY; set { if (SetField(ref _positionY, value)) UpdateTransform(); } }
        public double PositionZ { get => _positionZ; set { if (SetField(ref _positionZ, value)) UpdateTransform(); } }
        public double RotationX { get => _rotationX; set { if (SetField(ref _rotationX, value)) UpdateTransform(); } }
        public double RotationY { get => _rotationY; set { if (SetField(ref _rotationY, value)) UpdateTransform(); } }
        public double RotationZ { get => _rotationZ; set { if (SetField(ref _rotationZ, value)) UpdateTransform(); } }
        public double Scale { get => _scale; set { if (SetField(ref _scale, value)) UpdateTransform(); } }

        private void UpdateTransform()
        {
            var group = new Transform3DGroup();
            group.Children.Add(new ScaleTransform3D(_scale, _scale, _scale));
            group.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), _rotationX)));
            group.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), _rotationY)));
            group.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), _rotationZ)));
            group.Children.Add(new TranslateTransform3D(_positionX, _positionY, _positionZ));

            if (RootVisual != null)
                RootVisual.Transform = group;
        }

        private ObservableRangeCollection<ModelPart> _parts;
        public ObservableRangeCollection<ModelPart> Parts
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

        public ObservableRangeCollection<AnimationData> Animations { get; set; }

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
            UpdateTransform();
            Parts = new ObservableRangeCollection<ModelPart>();
            Animations = new ObservableRangeCollection<AnimationData>();
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
            // 1. Limpiar transformaciones y visuales
            if (RootVisual != null)
            {
                RootVisual.Transform = null;
                RootVisual.Children.Clear();
            }

            // 2. Limpiar Parts (geometrías y texturas críticas)
            if (_parts != null)
            {
                _parts.CollectionChanged -= Parts_CollectionChanged;
                foreach (var part in _parts)
                {
                    part.PropertyChanged -= Part_PropertyChanged;
                    part.Dispose();
                }
                _parts.Clear();
            }

            // 3. Limpiar estado de animaciones
            Animations?.Clear();
            CurrentAnimation = null;
            IsAnimationPaused = false;
            AnimationTime = 0;

            // 4. Liberar referencias finales
            Skeleton = null;
            SkinnedMesh = null;
            RootVisual = null;
        }
    }
}
