using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AssetsManager.Utils.Framework;
using LeagueToolkit.Hashing;

namespace AssetsManager.Views.Models.Viewer
{
    public sealed class VfxCharacterSubmeshOption : INotifyPropertyChanged
    {
        private bool _isVisible;
        private bool _isOverridden;
        private bool _suppressChanged;
        private readonly ModelPart _part;

        internal VfxCharacterSubmeshOption(string name, bool isVisible, ModelPart part = null)
        {
            _part = part;
            Name = name ?? string.Empty;
            NameHash = Fnv1a.HashLower(Name);
            _isVisible = isVisible;

            if (_part != null)
            {
                _part.PropertyChanged += Part_PropertyChanged;
            }
        }

        private void Part_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ModelPart.SelectedTextureName))
            {
                OnPropertyChanged(nameof(SelectedTextureName));
            }
            else if (e.PropertyName == nameof(ModelPart.IsVisible))
            {
                if (_isVisible != _part.IsVisible)
                {
                    _isVisible = _part.IsVisible;
                    OnPropertyChanged(nameof(IsVisible));
                }
            }
        }

        public string Name { get; }
        public uint NameHash { get; }
        public ModelPart Part => _part;

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible == value) return;
                _isVisible = value;
                if (!_suppressChanged)
                {
                    _isOverridden = true;
                    OnPropertyChanged(nameof(IsOverridden));
                    VisibilityChanged?.Invoke(this, EventArgs.Empty);
                }
                OnPropertyChanged();
            }
        }

        public bool IsOverridden => _isOverridden;

        public ObservableRangeCollection<string> AvailableTextureNames => _part?.AvailableTextureNames;

        public string SelectedTextureName
        {
            get => _part?.SelectedTextureName;
            set
            {
                if (_part == null || string.Equals(_part.SelectedTextureName, value, StringComparison.Ordinal)) return;
                _part.SelectedTextureName = value;
                OnPropertyChanged();
                TextureChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool HasTextures => AvailableTextureNames != null && AvailableTextureNames.Count > 0;
        public bool HasMultipleTextures => AvailableTextureNames != null && AvailableTextureNames.Count > 1;

        internal event EventHandler VisibilityChanged;
        internal event EventHandler TextureChanged;

        internal void Detach()
        {
            if (_part != null)
            {
                _part.PropertyChanged -= Part_PropertyChanged;
            }
        }

        internal void Sync(bool visible, bool overridden)
        {
            _suppressChanged = true;
            try
            {
                if (_isVisible != visible)
                {
                    _isVisible = visible;
                    OnPropertyChanged(nameof(IsVisible));
                }
                if (_isOverridden != overridden)
                {
                    _isOverridden = overridden;
                    OnPropertyChanged(nameof(IsOverridden));
                }
            }
            finally
            {
                _suppressChanged = false;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
