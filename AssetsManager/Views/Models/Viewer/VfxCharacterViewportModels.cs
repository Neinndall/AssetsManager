using System;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using AssetsManager.Utils.Framework;
using LeagueToolkit.Hashing;

namespace AssetsManager.Views.Models.Viewer
{
    public sealed class VfxCharacterBackdropOption
    {
        internal VfxCharacterBackdropOption(string label, MapSceneSource source)
        {
            Label = label ?? string.Empty;
            Source = source;
        }

        public string Label { get; }
        internal MapSceneSource Source { get; }
        public override string ToString() => Label;
    }

    internal static class VfxCharacterViewportSemantics
    {
        internal static bool ResolveSubmeshVisibility(bool authoredVisible, bool hasManualOverride, bool manualVisible) =>
            hasManualOverride ? manualVisible : authoredVisible;

        internal static bool CanAdoptLoadedBackdrop(bool enabled, string requestedKey, string loadedKey) =>
            enabled &&
            !string.IsNullOrWhiteSpace(requestedKey) &&
            !string.IsNullOrWhiteSpace(loadedKey) &&
            string.Equals(requestedKey, loadedKey, StringComparison.OrdinalIgnoreCase);

        internal static bool MapCharacterClipOwnsVfxRenderer(bool hasActiveClip, bool groupHasPreviewClip) =>
            hasActiveClip || groupHasPreviewClip;

        internal static double AdvanceAutoRotation(double degrees, double deltaSeconds, double speedDegreesPerSecond = 30d)
        {
            if (!double.IsFinite(degrees)) degrees = 0d;
            if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0d) return NormalizeDegrees(degrees);
            if (!double.IsFinite(speedDegreesPerSecond)) speedDegreesPerSecond = 30d;
            return NormalizeDegrees(degrees + deltaSeconds * speedDegreesPerSecond);
        }

        private static double NormalizeDegrees(double degrees)
        {
            double normalized = degrees % 360d;
            return normalized < 0d ? normalized + 360d : normalized;
        }

        /// <summary>
        /// Creates the complete world transform for character-attached VFX in the VFX Studio viewport.
        /// Scales X by -scaleMultiplier to match the character mesh mirror convention (GlMeshRenderer.CreateWorldMatrix with mirrorCharacterX: true).
        /// </summary>
        internal static Matrix4x4 CharacterPlacementWorld(
            double rotationX,
            double rotationY,
            double rotationZ,
            double scaleMultiplier,
            double positionX,
            double positionY,
            double positionZ)
        {
            float pitch = (float)(rotationX * Math.PI / 180d);
            float yaw = (float)(rotationY * Math.PI / 180d);
            float roll = (float)(rotationZ * Math.PI / 180d);
            float scale = (float)scaleMultiplier;
            return Matrix4x4.CreateScale(-scale, scale, scale) *
                   Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll) *
                   Matrix4x4.CreateTranslation((float)positionX, (float)positionY, (float)positionZ);
        }
    }

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
