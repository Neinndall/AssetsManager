using System;
using System.ComponentModel;
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

            // AllTextures is SHARED across all ModelParts of the same model
            // (Skn/Sco/MapGeo services assign the same dictionary instance to every part).
            // Calling Clear() or nulling the property here would destroy textures for
            // sibling parts that are still alive in the viewport.
            // We only release the local reference; the GC will reclaim the dictionary
            // when the last ModelPart referencing it is disposed.
            _allTextures = null;

            AvailableTextureNames?.Clear();

            PropertyChanged = null;
        }
    }
}
