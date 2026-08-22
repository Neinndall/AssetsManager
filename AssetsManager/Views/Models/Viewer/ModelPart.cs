using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using System.Collections.Generic;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Views.Models.Viewer
{
    public class ModelPart : INotifyPropertyChanged, IDisposable
    {
        private ModelVisual3D _visual;
        private GeometryModel3D _geometry;

        public ModelPart()
        {
            _visual = new ModelVisual3D();
        }

        public ModelPart(string name, GeometryModel3D geometry)
            : this()
        {
            Name = name;
            Geometry = geometry;
        }

        public string Name { get; set; }

        private bool _isVisible = true;
        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible == value) return;
                _isVisible = value;
                UpdateVisualContent();
                OnPropertyChanged();
            }
        }

        public ModelVisual3D Visual => _visual;

        public GeometryModel3D Geometry
        {
            get => _geometry;
            set
            {
                if (ReferenceEquals(_geometry, value)) return;
                _geometry = value;
                UpdateVisualContent();
                OnPropertyChanged();
            }
        }
        public int[] SourceVertexIndices { get; set; }
        public bool IsTextureTiled { get; set; } = true;
        public bool IsDoubleSided { get; set; } = true;
        public bool IsDecal { get; set; }
        public System.Numerics.Vector4 ColorTint { get; set; } = System.Numerics.Vector4.One;
        internal bool IsAlphaBlended =>
            ColorTint.W < 0.999f ||
            TextureUtils.HasTranslucentAlpha(AllTextures, SelectedTextureName) ||
            MaterialEffect?.RequiresAlphaBlend == true;
        internal float AlphaCutoff { get; set; } = 0.1f;
        internal bool UsesBakedDiffuse { get; set; }
        internal byte[] VertexColors { get; set; }
        public MapLightmapBinding Lightmap { get; set; }
        public ModelMaterialEffectDefinition MaterialEffect { get; set; } = ModelMaterialEffectDefinition.None;

        public Dictionary<string, BitmapSource> AllTextures
        {
            get => _allTextures;
            set
            {
                _allTextures = value;
                if (_allTextures != null)
                {
                    AvailableTextureNames.ReplaceRange(_allTextures.Keys);
                }
                else
                {
                    AvailableTextureNames.Clear();
                }
            }
        }
        private Dictionary<string, BitmapSource> _allTextures = new Dictionary<string, BitmapSource>();

        public ObservableRangeCollection<string> AvailableTextureNames { get; set; } = new ObservableRangeCollection<string>();

        private string _selectedTextureName;
        public string SelectedTextureName
        {
            get => _selectedTextureName;
            set
            {
                if (_selectedTextureName == value) return;
                _selectedTextureName = value;
                TextureUtils.UpdateMaterial(this);
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void UpdateVisualContent()
        {
            if (Visual == null) return;
            Visual.Content = _isVisible ? Geometry : null;
        }

        public void Dispose()
        {
            IsVisible = false;
            if (_visual != null)
            {
                _visual.Content = null;
                _visual = null;
            }

            if (_geometry != null)
            {
                _geometry.Material = null;
                _geometry.BackMaterial = null;
                _geometry.Geometry = null;
                _geometry = null;
            }

            // Shared texture dictionaries are cleared by the owning SceneModel.
            _allTextures = null;

            AvailableTextureNames?.Clear();
            SourceVertexIndices = null;
            VertexColors = null;
            Lightmap = null;
            MaterialEffect = ModelMaterialEffectDefinition.None;

            PropertyChanged = null;
        }
    }
}
