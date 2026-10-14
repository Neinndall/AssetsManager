using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using AssetsManager.Views.Models.Shared;

namespace AssetsManager.Views.Settings
{
    public partial class DefaultPathsSettingsView : UserControl
    {
        public SettingsModel ViewModel => DataContext as SettingsModel;

        public DefaultPathsSettingsView()
        {
            InitializeComponent();
        }

        private void btnBrowseLol_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.Settings == null) return;

            var folderBrowserDialog = new OpenFolderDialog
            {
                Title = "Select lol PBE directory",
                InitialDirectory = ViewModel.Settings.LolPbeDirectory
            };

            if (folderBrowserDialog.ShowDialog() == true)
            {
                ViewModel.Settings.LolPbeDirectory = folderBrowserDialog.FolderName;
                ViewModel.NotifySettingsChanged();
            }
        }

        private void btnBrowseLolLive_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.Settings == null) return;

            var folderBrowserDialog = new OpenFolderDialog
            {
                Title = "Select lol Live directory",
                InitialDirectory = ViewModel.Settings.LolLiveDirectory
            };

            if (folderBrowserDialog.ShowDialog() == true)
            {
                ViewModel.Settings.LolLiveDirectory = folderBrowserDialog.FolderName;
                ViewModel.NotifySettingsChanged();
            }
        }

        private void btnBrowseDefaultExtracted_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.Settings == null) return;

            var folderBrowserDialog = new OpenFolderDialog
            {
                Title = "Select default extraction directory",
                InitialDirectory = ViewModel.Settings.DefaultExtractedSelectDirectory
            };

            if (folderBrowserDialog.ShowDialog() == true)
            {
                ViewModel.Settings.DefaultExtractedSelectDirectory = folderBrowserDialog.FolderName;
                ViewModel.NotifySettingsChanged();
            }
        }

        private void btnBrowseCustomGroundLogo_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.Settings == null) return;

            var openFileDialog = new OpenFileDialog
            {
                Title = "Select viewport ground logo",
                InitialDirectory = System.IO.Path.GetDirectoryName(ViewModel.Settings.CustomGroundLogoPath),
                Filter = "Ground Logo Image (*.png;*.webp)|*.png;*.webp|All Files (*.*)|*.*"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                ViewModel.Settings.CustomGroundLogoPath = openFileDialog.FileName;
                ViewModel.NotifySettingsChanged();
            }
        }

    }
}
