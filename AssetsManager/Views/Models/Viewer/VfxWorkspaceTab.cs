using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Viewer
{
    public enum VfxWorkspaceTabKind
    {
        Skin,
        Map
    }

    /// <summary>
    /// One independently addressable VFX Studio workspace document. The tab only owns navigation
    /// state; heavyweight Champion/MAP resources remain owned by VfxInspectorControl and are swapped
    /// into the single viewport when the tab becomes active.
    /// </summary>
    public sealed class VfxWorkspaceTab : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _key;
        private string _title;
        private string _subtitle;
        private object _payload;

        public string Key
        {
            get => _key;
            internal set
            {
                if (string.Equals(_key, value, System.StringComparison.Ordinal)) return;
                _key = value;
                OnPropertyChanged();
            }
        }

        public string Title
        {
            get => _title;
            internal set
            {
                if (string.Equals(_title, value, System.StringComparison.Ordinal)) return;
                _title = value;
                OnPropertyChanged();
            }
        }

        public string Subtitle
        {
            get => _subtitle;
            internal set
            {
                if (string.Equals(_subtitle, value, System.StringComparison.Ordinal)) return;
                _subtitle = value;
                OnPropertyChanged();
            }
        }

        public VfxWorkspaceTabKind Kind { get; init; }

        internal object Payload
        {
            get => _payload;
            set
            {
                if (ReferenceEquals(_payload, value)) return;
                _payload = value;
                OnPropertyChanged();
            }
        }

        // Lightweight navigation memory only. Runtime/GPU ownership stays in the single Studio viewport.
        internal uint? SelectedSystemPathHash { get; set; }
        internal string SelectedAnimationFilePath { get; set; }
        internal uint? SelectedAnimationGraphPathHash { get; set; }
        internal uint? SelectedAnimationOwnerPathHash { get; set; }
        internal uint? SelectedSpellPathHash { get; set; }
        internal float? AnimationParameter { get; set; }

        // Character/Skin viewport state. The tab owns only lightweight controls; the single Studio
        // viewport still owns every decoded model, MAP scene and GPU resource.
        internal bool CharacterEffectsEnabled { get; set; }
        internal bool CharacterArmatureVisible { get; set; }
        internal bool CharacterJointNamesVisible { get; set; }
        internal bool CharacterAutoRotate { get; set; }
        internal double CharacterAutoRotateDegrees { get; set; }
        internal bool CharacterTransformGizmoEnabled { get; set; } = true;
        internal bool CharacterBackdropEnabled { get; set; }
        internal bool BackdropParticlesVisible { get; set; }
        internal bool BackdropStructuresVisible { get; set; } = true;
        internal string CharacterBackdropKey { get; set; }
        internal int? CharacterBackdropVisibilityFlags { get; set; }
        internal double CharacterPositionX { get; set; }
        internal double CharacterPositionY { get; set; }
        internal double CharacterPositionZ { get; set; }
        internal double CharacterRotationX { get; set; }
        internal double CharacterRotationY { get; set; }
        internal double CharacterRotationZ { get; set; }
        internal double CharacterScaleMultiplier { get; set; } = 1d;
        internal bool CharacterPlacementCustomized { get; set; }
        internal string CharacterPlacedOnKey { get; set; }
        internal Dictionary<uint, bool> CharacterSubmeshOverrides { get; } = new();

        public bool IsSelected
        {
            get => _isSelected;
            internal set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
