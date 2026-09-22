using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Viewer
{
    public sealed class MapOutlinerItemModel : INotifyPropertyChanged
    {
        private bool _isHidden;
        private bool _isFocused;

        internal MapOutlinerItemModel(MapOutlineItemData data)
        {
            Data = data;
        }

        internal MapOutlineItemData Data { get; }
        public string Id => Data.Id;
        public string Name => Data.Name;
        public string ClassName => Data.ClassName;
        public MapOutlineItemKind Kind => Data.Kind;
        public bool IsDrawable => Data.IsDrawable;

        public bool IsHidden
        {
            get => _isHidden;
            internal set
            {
                if (_isHidden == value) return;
                _isHidden = value;
                OnPropertyChanged();
            }
        }

        public bool IsFocused
        {
            get => _isFocused;
            internal set
            {
                if (_isFocused == value) return;
                _isFocused = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed class MapOutlinerChunkModel : INotifyPropertyChanged
    {
        private bool _isExpanded;
        private bool _isHidden;

        internal MapOutlinerChunkModel(MapOutlineChunkData data)
        {
            Data = data;
            Items = new ObservableCollection<MapOutlinerItemModel>(
                data.Items.Select(item => new MapOutlinerItemModel(item)));
        }

        internal MapOutlineChunkData Data { get; }
        public string Id => Data.Id;
        public string Label => Data.Label;
        public int ItemCount => Items.Count;
        public ObservableCollection<MapOutlinerItemModel> Items { get; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                OnPropertyChanged();
            }
        }

        public bool IsHidden
        {
            get => _isHidden;
            internal set
            {
                if (_isHidden == value) return;
                _isHidden = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}