using Microsoft.WindowsAPICodePack.Dialogs;
using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace AssetsManager.Views.Controls.Comparator
{
    public partial class JsonComparisonControl : UserControl
    {
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public DiffViewService DiffViewService { get; set; }

        public JsonComparisonControl()
        {
            InitializeComponent();
        }

        private void btnSelectOriginal_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new CommonOpenFileDialog
            {
                Filters = { new CommonFileDialogFilter("JSON files", "*.json"), new CommonFileDialogFilter("All files", "*.*") },
                Title = "Select Original JSON File"
            };

            if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                originalFileTextBox.Text = openFileDialog.FileName;
            }
        }

        private void btnSelectNew_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new CommonOpenFileDialog
            {
                Filters = { new CommonFileDialogFilter("JSON files", "*.json"), new CommonFileDialogFilter("All files", "*.*") },
                Title = "Select New JSON File"
            };

            if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                newFileTextBox.Text = openFileDialog.FileName;
            }
        }

        private async void btnCompare_Click(object sender, RoutedEventArgs e)
        {
            string originalPath = originalFileTextBox.Text;
            string newPath = newFileTextBox.Text;

            if (string.IsNullOrWhiteSpace(originalPath) || string.IsNullOrWhiteSpace(newPath))
            {
                CustomMessageBoxService.ShowWarning("Warning", "Please select both an original and a new file.", Window.GetWindow(this));
                return;
            }

            if (DiffViewService != null)
            {
                await DiffViewService.ShowFileDiffAsync(originalPath, newPath, Window.GetWindow(this));
            }
            else
            {
                CustomMessageBoxService.ShowError("Error", "The DiffViewService is not available.", Window.GetWindow(this));
            }
        }
    }
}
