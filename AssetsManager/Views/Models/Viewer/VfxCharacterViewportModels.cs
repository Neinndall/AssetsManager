using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    }

    public sealed class VfxCharacterSubmeshOption : INotifyPropertyChanged
    {
        private bool _isVisible;
        private bool _isOverridden;
        private bool _suppressChanged;

        internal VfxCharacterSubmeshOption(string name, bool isVisible)
        {
            Name = name ?? string.Empty;
            NameHash = Fnv1a.HashLower(Name);
            _isVisible = isVisible;
        }

        public string Name { get; }
        public uint NameHash { get; }

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

        internal event EventHandler VisibilityChanged;

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
