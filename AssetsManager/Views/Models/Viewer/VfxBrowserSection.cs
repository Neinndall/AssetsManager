using System;
using System.ComponentModel;
using System.Windows.Data;

namespace AssetsManager.Views.Models.Viewer
{
    public sealed class VfxBrowserSection : INotifyPropertyChanged
    {
        public VfxBrowserSection(VfxSkinItem owner, string title, bool isAnimation)
        {
            Owner = owner;
            Title = title;
            IsAnimation = isAnimation;
        }

        public VfxSkinItem Owner { get; }
        public string Title { get; }
        public bool IsAnimation { get; }
        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); }
        }
        private ICollectionView _items = new ListCollectionView(Array.Empty<object>());
        public ICollectionView Items
        {
            get => _items;
            set { _items = value; PropertyChanged?.Invoke(this, new(nameof(Items))); }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }
}
