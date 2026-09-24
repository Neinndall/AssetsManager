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

        public string Key { get; init; }
        public string Title { get; init; }
        public string Subtitle { get; init; }
        public VfxWorkspaceTabKind Kind { get; init; }
        internal object Payload { get; init; }

        // Lightweight navigation memory only. Runtime/GPU ownership stays in the single Studio viewport.
        internal uint? SelectedSystemPathHash { get; set; }
        internal string SelectedAnimationFilePath { get; set; }
        internal uint? SelectedAnimationGraphPathHash { get; set; }
        internal uint? SelectedAnimationOwnerPathHash { get; set; }
        internal uint? SelectedSpellPathHash { get; set; }
        internal float? AnimationParameter { get; set; }

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
