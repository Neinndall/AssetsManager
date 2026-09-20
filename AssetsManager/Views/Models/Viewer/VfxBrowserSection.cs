using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;

namespace AssetsManager.Views.Models.Viewer
{
    public enum VfxBrowserFolderKind
    {
        Root,
        Character,
        Group,
        Theme
    }

    /// <summary>
    /// A semantic branch in the VFX asset browser. Unlike the old flat BIN list, these folders
    /// represent Riot ownership (Characters, one character, Skins/Themes/Spells) rather than a
    /// physical directory that the renderer has to load.
    /// </summary>
    public sealed class VfxBrowserFolder : INotifyPropertyChanged
    {
        public VfxBrowserFolder(string title, VfxBrowserFolderKind kind, string subtitle = null)
        {
            Title = title;
            Kind = kind;
            Subtitle = subtitle;
        }

        public string Title { get; }
        public string Subtitle { get; }
        public VfxBrowserFolderKind Kind { get; }
        public ObservableCollection<object> Children { get; } = new();

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new(nameof(IsExpanded)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>One named SpellObject discovered below Characters/{character}/Spells.</summary>
    public sealed class VfxSpellBrowserItem
    {
        public string Name { get; init; }
        public string ObjectPath { get; init; }
        public uint PathHash { get; init; }
        public string BinPath { get; init; }
        public string SourceName => System.IO.Path.GetFileName(BinPath);
    }

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
