using System;
using System.Linq;
using System.ComponentModel;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using System.Collections.ObjectModel;
using System.Windows.Media;
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
            get { return _isVisible; }
            set
            {
                if (_isVisible != value)
                {
                    _isVisible = value;
                    if (Visual != null)
                    {
                        if (_isVisible)
                        {
                            if (Visual.Content == null)
                            {
                                Visual.Content = Geometry;
                            }
                        }
                        else
                        {
                            Visual.Content = null;
                        }
                    }
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
                }
            }
        }

        public ModelVisual3D Visual { get; set; }
        public GeometryModel3D Geometry { get; set; }

        public Dictionary<string, BitmapSource> AllTextures
        {
            get { return _allTextures; }
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
            get { return _selectedTextureName; }
            set
            {
                if (_selectedTextureName != value)
                {
                    _selectedTextureName = value;
                    TextureUtils.UpdateMaterial(this);
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTextureName)));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

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
                Geometry.Geometry = null;
                Geometry = null;
            }

            AllTextures?.Clear();
            AllTextures = null;
            AvailableTextureNames?.Clear();

            // Desuscribir todos los eventos
            PropertyChanged = null;
        }
    }
}
