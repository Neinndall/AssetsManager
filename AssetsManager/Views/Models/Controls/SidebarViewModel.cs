using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using AssetsManager.Info;
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

        // Technical Identity Properties (Resolved once from ApplicationInfos)
        public string Version => ApplicationInfos.Version;
        public string BuildType => ApplicationInfos.BuildType;
        public MaterialIconKind BuildIcon => ApplicationInfos.BuildIcon;

        // Visual properties resolved from ResourceDictionary
        public Brush BuildBrush => Application.Current.FindResource(ApplicationInfos.BuildColorKey) as Brush;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
