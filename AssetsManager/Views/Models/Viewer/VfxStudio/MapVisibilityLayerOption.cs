using System.ComponentModel;

namespace AssetsManager.Views.Models.Viewer
{
    public sealed class MapVisibilityLayerOption : INotifyPropertyChanged
    {
        private bool _isEnabled;

        internal MapVisibilityLayerOption(MapGeometryLayerData layer, bool isEnabled, string label = null)
        {
            Index = layer?.Index ?? 0;
            Triangles = layer?.Triangles ?? 0;
            _isEnabled = isEnabled;
            Label = string.IsNullOrWhiteSpace(label) ? $"Layer {Index + 1}" : $"{Index + 1} · {label}";
        }

        public int Index { get; }
        public int Triangles { get; }
        public string Label { get; }
        public string TriangleText => $"{Triangles:N0} tris";

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
