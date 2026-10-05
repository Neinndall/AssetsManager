using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;

namespace AssetsManager.Views.Models.Viewer
{
    public enum StudioBrowserFolderKind
    {
        Root,
        Character,
        Group
    }

    /// <summary>
    /// A semantic branch in the Studio asset browser. Unlike the old flat BIN list, these folders
    /// represent Riot ownership (Characters, one character, Skins/Spells) rather than a physical
    /// directory that the renderer has to load. Theme BINs remain catalog support data only.
    /// </summary>
    public sealed class StudioBrowserFolder : INotifyPropertyChanged
    {
        public StudioBrowserFolder(string title, StudioBrowserFolderKind kind, string subtitle = null)
        {
            Title = title;
            Kind = kind;
            Subtitle = subtitle;
        }

        public string Title { get; }
        public string Subtitle { get; }
        public StudioBrowserFolderKind Kind { get; }
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
    public sealed class StudioSpellBrowserItem
    {
        public StudioSkinItem Owner { get; init; }
        public string Name { get; init; }
        public string ObjectPath { get; init; }
        public uint PathHash { get; init; }
        public string BinPath { get; init; }
        public int DeclarationCount { get; init; } = 1;
        public VfxSpellPreview Preview { get; init; }
        public VfxSpellAvailability Availability { get; init; }
        public VfxSpellUnplayableReason UnplayableReason { get; init; }
        public bool IsPlayable => Availability == VfxSpellAvailability.Supported;
        public string AvailabilityText => Availability switch
        {
            VfxSpellAvailability.Supported => "Play",
            VfxSpellAvailability.Ambiguous => "Ambiguous",
            VfxSpellAvailability.Unavailable => "Unavailable",
            _ => UnplayableReason switch
            {
                VfxSpellUnplayableReason.Buff => "Buff",
                VfxSpellUnplayableReason.ScriptOnly => "Script",
                VfxSpellUnplayableReason.NoVisuals => "No visuals",
                VfxSpellUnplayableReason.UnsupportedMissile => "Missile",
                _ => "Unsupported"
            }
        };
        public string SourceName => System.IO.Path.GetFileName(BinPath);
    }

    public enum StudioBrowserSectionKind
    {
        Systems,
        Clips,
        Spells
    }

    public sealed class StudioBrowserSection : INotifyPropertyChanged
    {
        public StudioBrowserSection(StudioSkinItem owner, string title, StudioBrowserSectionKind kind)
        {
            Owner = owner;
            Title = title;
            Kind = kind;
        }

        public StudioSkinItem Owner { get; }
        public string Title { get; }
        public StudioBrowserSectionKind Kind { get; }
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
