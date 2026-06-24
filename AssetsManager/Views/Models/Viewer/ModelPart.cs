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

        public ModelVisual3D Visual { get; set; }
        public GeometryModel3D Geometry { get; set; }
        public int[] SourceVertexIndices { get; set; }

        public Dictionary<string, BitmapSource> AllTextures
        {
            get => _allTextures;
            set
            {
                _allTextures = value;
                if (_allTextures != null)
                {
                    AvailableTextureNames.ReplaceRange(_allTextures.Keys.Select(k => PathUtils.TruncateAtDot(k)));
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
                string normalized = PathUtils.TruncateAtDot(value);
                if (_selectedTextureName == normalized) return;
                _selectedTextureName = normalized;
                TextureUtils.UpdateMaterial(this, this.Name?.Contains("Eye", StringComparison.OrdinalIgnoreCase) == true);
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
            if (Visual != null)
            {
                Visual.Content = null;
                Visual = null;
            }

            if (Geometry != null)
            {
                Geometry.Material = null;
                Geometry.BackMaterial = null;
                Geometry.Geometry = null;
                Geometry = null;
            }

            // Shared texture dictionaries are cleared by the owning SceneModel.
            _allTextures = null;

            AvailableTextureNames?.Clear();
            SourceVertexIndices = null;

            PropertyChanged = null;
        }
    }
}
