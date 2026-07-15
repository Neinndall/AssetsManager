using System.Windows;
using System.Windows.Controls;
using Microsoft.WindowsAPICodePack.Dialogs;
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

            using (var folderBrowserDialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select lol PBE directory",
                InitialDirectory = ViewModel.Settings.LolPbeDirectory
            })
            {
                if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    ViewModel.Settings.LolPbeDirectory = folderBrowserDialog.FileName;
                    ViewModel.NotifySettingsChanged();
                }
            }
        }

        private void btnBrowseLolLive_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.Settings == null) return;

            using (var folderBrowserDialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select lol Live directory",
                InitialDirectory = ViewModel.Settings.LolLiveDirectory
            })
            {
                if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    ViewModel.Settings.LolLiveDirectory = folderBrowserDialog.FileName;
                    ViewModel.NotifySettingsChanged();
                }
            }
        }

        private void btnBrowseDefaultExtracted_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.Settings == null) return;

            using (var folderBrowserDialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select default extraction directory",
                InitialDirectory = ViewModel.Settings.DefaultExtractedSelectDirectory
            })
            {
                if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    ViewModel.Settings.DefaultExtractedSelectDirectory = folderBrowserDialog.FileName;
                    ViewModel.NotifySettingsChanged();
                }
            }
        }

        private void btnBrowseCustomGroundLogo_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.Settings == null) return;

            using (var openFileDialog = new CommonOpenFileDialog
            {
                IsFolderPicker = false,
                Title = "Select viewport ground logo",
                InitialDirectory = System.IO.Path.GetDirectoryName(ViewModel.Settings.CustomGroundLogoPath),
                Filters = {
                    new CommonFileDialogFilter("Ground Logo Image", "*.png;*.webp"),
                    new CommonFileDialogFilter("All Files", "*.*")
                }
            })
            {
                if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    ViewModel.Settings.CustomGroundLogoPath = openFileDialog.FileName;
                    ViewModel.NotifySettingsChanged();
                }
            }
        }
    }
}
