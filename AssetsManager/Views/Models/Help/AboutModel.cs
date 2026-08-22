using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using AssetsManager.Utils;
using Material.Icons;

namespace AssetsManager.Views.Models.Help
{
    public class AboutModel : INotifyPropertyChanged
    {
        private static Brush _cachedBuildBrush;
        public string ApplicationVersion => VersionInfo.Version;
        public string BuildType => VersionInfo.IsQA ? "Experimental Build" : "Stable Build";
        public MaterialIconKind BuildIcon => VersionInfo.IsQA ? MaterialIconKind.Flask : MaterialIconKind.CheckDecagram;

        public Brush BuildBrush => _cachedBuildBrush ??= Application.Current.FindResource(VersionInfo.IsQA ? "AccentOrange" : "AccentGreen") as Brush;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
