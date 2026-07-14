using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using AssetsManager.Info;
using Material.Icons;

namespace AssetsManager.Views.Models.Help
{
    public class AboutModel : INotifyPropertyChanged
    {
        private static Brush _cachedBuildBrush;
        public string ApplicationVersion => ApplicationInfos.Version;
        public string BuildType => ApplicationInfos.BuildType;
        public MaterialIconKind BuildIcon => ApplicationInfos.BuildIcon;

        public Brush BuildBrush => _cachedBuildBrush ??= Application.Current.FindResource(ApplicationInfos.BuildColorKey) as Brush;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
