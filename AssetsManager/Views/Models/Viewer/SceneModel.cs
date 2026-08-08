using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Media3D;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Mesh;
using AssetsManager.Utils.Framework;
using AssetsManager.Utils;

namespace AssetsManager.Views.Models.Viewer
{
    public class SceneModel : INotifyPropertyChanged, IDisposable
    {
        private string _name = "New Model";
        public string Name
        {
            get => _name;
            set
            {
                SetField(ref _name, PathUtils.TruncateAtDot(value));
            }
        }
        public string SourceType { get; set; } = "Model"; // "Model" or "Chroma"
        public string FilePath { get; set; } = string.Empty;
        public SkinnedMesh SkinnedMesh { get; set; }
        public ModelVisual3D RootVisual { get; set; }
        public MapLightingProfile MapLightingProfile { get; set; }

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

        // Persistent transforms to avoid per-property-change allocations
        private readonly TranslateTransform3D _translateTransform = new TranslateTransform3D();
        private readonly ScaleTransform3D _scaleTransform = new ScaleTransform3D(1, 1, 1);
        private readonly RotateTransform3D _rotateXTransform = new RotateTransform3D();
        private readonly RotateTransform3D _rotateYTransform = new RotateTransform3D();
        private readonly RotateTransform3D _rotateZTransform = new RotateTransform3D();
        private readonly Transform3DGroup _userTransformGroup = new Transform3DGroup();

        /// <summary>
        /// The persistent Transform3DGroup that owns the user-defined Position/Rotation/Scale.
        /// Exposed so the Viewport can safely inject the auto-rotation transform
        /// without losing its reference when properties change.
        /// </summary>
        public Transform3DGroup UserTransformGroup => _userTransformGroup;

        private void UpdateTransform()
        {
            // Only mutate values on persistent transforms — no allocations.
            _translateTransform.OffsetX = _positionX;
            _translateTransform.OffsetY = _positionY;
            _translateTransform.OffsetZ = _positionZ;

            _scaleTransform.ScaleX = _scale;
            _scaleTransform.ScaleY = _scale;
            _scaleTransform.ScaleZ = _scale;

            ((AxisAngleRotation3D)_rotateXTransform.Rotation).Angle = _rotationX;
            ((AxisAngleRotation3D)_rotateYTransform.Rotation).Angle = _rotationY;
            ((AxisAngleRotation3D)_rotateZTransform.Rotation).Angle = _rotationZ;
        }

        private ObservableRangeCollection<ModelPart> _parts;
        private readonly HashSet<ModelPart> _trackedParts = new HashSet<ModelPart>();
        public ObservableRangeCollection<ModelPart> Parts
        {
            get => _parts;
            private set
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
        public bool IsAnimationPaused { get; set; } = true;
        public double AnimationTime { get; set; }
        private bool _isUpdatingVisibility;

        private bool _isVisible = true;
        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (SetField(ref _isVisible, value))
                {
                    if (!_isUpdatingVisibility)
                    {
                        try
                        {
                            _isUpdatingVisibility = true;
                            if (Parts != null)
                            {
                                foreach (var part in Parts)
                                {
                                    part.IsVisible = value;
                                }
                            }
                            _areAllPartsVisible = value;
                            OnPropertyChanged(nameof(AreAllPartsVisible));
                        }
                        finally
                        {
                            _isUpdatingVisibility = false;
                        }
                    }
                }
            }
        }

        private bool _isMeshSyncEnabled;
        public bool IsMeshSyncEnabled
        {
            get => _isMeshSyncEnabled;
            set => SetField(ref _isMeshSyncEnabled, value);
        }

        private bool _isTextureSyncEnabled;
        public bool IsTextureSyncEnabled
        {
            get => _isTextureSyncEnabled;
            set => SetField(ref _isTextureSyncEnabled, value);
        }

        private bool _areAllPartsVisible = true;
        public bool AreAllPartsVisible
        {
            get => _areAllPartsVisible;
            set
            {
                _areAllPartsVisible = value;
                OnPropertyChanged();
                if (Parts != null && !_isUpdatingVisibility)
                {
                    try
                    {
                        _isUpdatingVisibility = true;
                        foreach (var part in Parts)
                        {
                            part.IsVisible = value;
                        }
                        _isVisible = value;
                        OnPropertyChanged(nameof(IsVisible));
                    }
                    finally
                    {
                        _isUpdatingVisibility = false;
                    }
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event Action<ModelPart> MeshVisibilityChanged;
        public event Action<ModelPart> MeshTextureChanged;

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
            _rotateXTransform.Rotation = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
            _rotateYTransform.Rotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
            _rotateZTransform.Rotation = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);

            _userTransformGroup.Children.Add(_scaleTransform);
            _userTransformGroup.Children.Add(_rotateXTransform);
            _userTransformGroup.Children.Add(_rotateYTransform);
            _userTransformGroup.Children.Add(_rotateZTransform);
            _userTransformGroup.Children.Add(_translateTransform);

            RootVisual = new ModelVisual3D();
            RootVisual.Transform = _userTransformGroup;

            UpdateTransform();

            Parts = new ObservableRangeCollection<ModelPart>();
            Animations = new ObservableRangeCollection<AnimationData>();
        }

        public bool AddPart(ModelPart part)
        {
            if (part == null || Parts.Contains(part))
                return false;

            Parts.Add(part);
            return true;
        }

        public int AddParts(IEnumerable<ModelPart> parts)
        {
            if (parts == null)
                throw new ArgumentNullException(nameof(parts));

            var existing = new HashSet<ModelPart>(Parts);
            var additions = parts
                .Where(part => part != null && existing.Add(part))
                .ToList();

            Parts.AddRange(additions);
            return additions.Count;
        }

        public bool RemovePart(ModelPart part, bool dispose = false)
        {
            if (part == null || !Parts.Remove(part))
                return false;

            if (dispose)
                part.Dispose();
            return true;
        }

        private void SynchronizeParts()
        {
            var currentParts = new HashSet<ModelPart>(Parts ?? Enumerable.Empty<ModelPart>());
            foreach (ModelPart removedPart in _trackedParts.Where(part => !currentParts.Contains(part)).ToList())
            {
                removedPart.PropertyChanged -= Part_PropertyChanged;
                if (removedPart.Visual != null && RootVisual?.Children.Contains(removedPart.Visual) == true)
                    RootVisual.Children.Remove(removedPart.Visual);
                _trackedParts.Remove(removedPart);
            }

            foreach (ModelPart part in currentParts)
            {
                if (_trackedParts.Add(part))
                    part.PropertyChanged += Part_PropertyChanged;
                if (part.Visual != null && RootVisual?.Children.Contains(part.Visual) == false)
                    RootVisual.Children.Add(part.Visual);
            }
        }

        private void Parts_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            SynchronizeParts();
            UpdateMasterVisibility();
        }

        private void Part_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ModelPart.IsVisible))
            {
                if (sender is ModelPart part)
                {
                    if (IsMeshSyncEnabled)
                    {
                        MeshVisibilityChanged?.Invoke(part);
                    }
                }
                UpdateMasterVisibility();
            }
            else if (e.PropertyName == nameof(ModelPart.SelectedTextureName))
            {
                if (sender is ModelPart part)
                {
                    if (IsTextureSyncEnabled)
                    {
                        MeshTextureChanged?.Invoke(part);
                    }
                }
            }
        }

        private void UpdateMasterVisibility()
        {
            if (_isUpdatingVisibility) return;
            try
            {
                _isUpdatingVisibility = true;
                bool allVisible = Parts != null && Parts.Count > 0 && Parts.All(p => p.IsVisible);
                bool anyVisible = Parts != null && Parts.Any(p => p.IsVisible);

                SetField(ref _areAllPartsVisible, allVisible, nameof(AreAllPartsVisible));
                SetField(ref _isVisible, anyVisible, nameof(IsVisible));
            }
            finally
            {
                _isUpdatingVisibility = false;
            }
        }

        public void Dispose()
        {
            CurrentAnimation?.Dispose();
            SkinnedMesh?.Dispose();

            if (RootVisual != null)
            {
                RootVisual.Transform = null;
                RootVisual.Children.Clear();
            }

            if (_parts != null)
            {
                var uniqueTextureDicts = new List<Dictionary<string, System.Windows.Media.Imaging.BitmapSource>>();
                foreach (var part in _parts)
                {
                    if (part.AllTextures != null && !uniqueTextureDicts.Contains(part.AllTextures))
                    {
                        uniqueTextureDicts.Add(part.AllTextures);
                    }
                }

                _parts.CollectionChanged -= Parts_CollectionChanged;
                foreach (var part in _parts)
                {
                    part.PropertyChanged -= Part_PropertyChanged;
                    part.Dispose();
                }
                _parts.Clear();
                _trackedParts.Clear();

                foreach (var dict in uniqueTextureDicts)
                {
                    dict.Clear();
                }
            }
            Animations?.Clear();

            if (_userTransformGroup.Children.Contains(_scaleTransform))
            {
                _userTransformGroup.Children.Clear();
            }

            CurrentAnimation = null;
            SkinnedMesh = null;
            Skeleton = null;
            MapLightingProfile = null;
            RootVisual = null;

            IsAnimationPaused = false;
            AnimationTime = 0;

            if (PropertyChanged != null)
            {
                foreach (var d in PropertyChanged.GetInvocationList())
                {
                    PropertyChanged -= (PropertyChangedEventHandler)d;
                }
            }
            if (MeshVisibilityChanged != null)
            {
                foreach (var d in MeshVisibilityChanged.GetInvocationList())
                {
                    MeshVisibilityChanged -= (Action<ModelPart>)d;
                }
            }
            if (MeshTextureChanged != null)
            {
                foreach (var d in MeshTextureChanged.GetInvocationList())
                {
                    MeshTextureChanged -= (Action<ModelPart>)d;
                }
            }
        }
    }
}
