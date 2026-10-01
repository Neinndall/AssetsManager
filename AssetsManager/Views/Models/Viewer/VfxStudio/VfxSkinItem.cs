using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// Item model representing a skin selection option inside the VFX Studio.
    /// </summary>
    public class VfxSkinItem : INotifyPropertyChanged
    {
        public VfxSkinItem()
        {
            Sections.Add(new VfxBrowserSection(this, "Systems", VfxBrowserSectionKind.Systems));
            Sections.Add(new VfxBrowserSection(this, "Clips", VfxBrowserSectionKind.Clips));
        }

        public ObservableCollection<VfxBrowserSection> Sections { get; } = new();
        public ObservableCollection<object> SpellItems { get; } = new();
        public string Title => !string.IsNullOrWhiteSpace(BrowserTitle)
            ? BrowserTitle
            : SkinIndex == int.MaxValue
                ? System.IO.Path.GetFileName(BinPath)
                : $"Skin {SkinIndex}";
        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { if (_isExpanded == value) return; _isExpanded = value; OnPropertyChanged(); }
        }

        private string _displayName;
        private string _browserTitle;
        private string _ownerName;
        private string _binPath;
        private int _skinIndex;

        public string DisplayName
        {
            get => _displayName;
            set { _displayName = value; OnPropertyChanged(); }
        }

        public string BrowserTitle
        {
            get => _browserTitle;
            set
            {
                if (_browserTitle == value) return;
                _browserTitle = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Title));
            }
        }

        public string OwnerName
        {
            get => _ownerName;
            set { if (_ownerName == value) return; _ownerName = value; OnPropertyChanged(); }
        }

        public string BinPath
        {
            get => _binPath;
            set { _binPath = value; OnPropertyChanged(); }
        }

        public int SkinIndex
        {
            get => _skinIndex;
            set { _skinIndex = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
