using System.Collections.ObjectModel;
using System.ComponentModel;

namespace AssetsManager.Views.Models.Viewer
{
    public enum MapBrowserNodeKind
    {
        MapFile,
        Map,
        Geometry,
        Materials,
        Material,
        Chunks,
        Chunk,
        Placeable,
        Characters,
        CharacterSkin,
        CharacterPlacement,
        Clips,
        Clip,
        Particles,
        ParticleSystem,
        ParticlePlacement
    }

    /// <summary>
    /// One semantic MAP entry shown in the shared VFX Studio asset tree.
    /// </summary>
    public sealed class MapBrowserNode : INotifyPropertyChanged
    {
        internal MapBrowserNode(
            string title,
            MapBrowserNodeKind kind,
            string subtitle = null,
            object payload = null,
            bool canHide = false,
            string inspectorSummary = null)
        {
            Title = title;
            Kind = kind;
            Subtitle = subtitle;
            Payload = payload;
            CanHide = canHide;
            InspectorSummary = inspectorSummary;
        }

        public string Title { get; }
        public string Subtitle { get; }
        public string InspectorSummary { get; }
        public MapBrowserNodeKind Kind { get; }
        public ObservableCollection<object> Children { get; } = new();
        public bool CanHide { get; }
        internal object Payload { get; }

        private bool _isExpanded;
        private bool _isHidden;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        public bool IsHidden
        {
            get => _isHidden;
            internal set
            {
                if (_isHidden == value) return;
                _isHidden = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHidden)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
