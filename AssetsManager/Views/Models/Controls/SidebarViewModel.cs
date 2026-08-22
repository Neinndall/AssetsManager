using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using AssetsManager.Utils;
using Material.Icons;

namespace AssetsManager.Views.Models.Controls
{
    public class SidebarViewModel : INotifyPropertyChanged
    {
        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        // Technical identity properties resolved from VersionInfo.
        public string Version => VersionInfo.Version;
        public string BuildType => VersionInfo.IsQA ? "Experimental Build" : "Stable Build";
        public MaterialIconKind BuildIcon => VersionInfo.IsQA ? MaterialIconKind.Flask : MaterialIconKind.CheckDecagram;

        // Visual properties resolved from ResourceDictionary
        public Brush BuildBrush => Application.Current.FindResource(VersionInfo.IsQA ? "AccentOrange" : "AccentGreen") as Brush;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
