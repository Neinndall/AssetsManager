using System.Windows;
using System.Windows.Controls;
using Microsoft.WindowsAPICodePack.Dialogs;
using AssetsManager.Views.Models.Shared;

namespace AssetsManager.Views.Settings
{
    public partial class DefaultPathsSettingsView : UserControl
    {
        public DefaultPathsSettingsView()
        {
            InitializeComponent();
        }

        public void ApplySettingsToUI(SettingsModel model)
        {
            this.DataContext = model;
        }

        private void btnBrowseLol_Click(object sender, RoutedEventArgs e)
        {
            using (var folderBrowserDialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select lol PBE directory"
            })
            {
                if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    textBoxLolPath.Text = folderBrowserDialog.FileName;
                }
            }
        }

        private void btnBrowseLolLive_Click(object sender, RoutedEventArgs e)
        {
            using (var folderBrowserDialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select lol Live directory"
            })
            {
                if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    textBoxLolLivePath.Text = folderBrowserDialog.FileName;
                }
            }
        }

        private void btnBrowseDefaultExtracted_Click(object sender, RoutedEventArgs e)
        {
            using (var folderBrowserDialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select default extraction directory"
            })
            {
                if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    textBoxDefaultExtractedPath.Text = folderBrowserDialog.FileName;
                }
            }
        }

        private void btnBrowseCustomFloorTexture_Click(object sender, RoutedEventArgs e)
        {
            using (var openFileDialog = new CommonOpenFileDialog
            {
                IsFolderPicker = false,
                Title = "Select custom floor texture file",
                Filters = {
                    new CommonFileDialogFilter("Image Files", "*.png;*.jpg;*.jpeg;*.dds;*.tga"),
                    new CommonFileDialogFilter("All Files", "*.*")
                }
            })
            {
                if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    textBoxCustomFloorTexturePath.Text = openFileDialog.FileName;
                }
            }
        }
    }
}
