using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Threading;
using Microsoft.WindowsAPICodePack.Dialogs;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Views.Dialogs;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Downloads;
using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using AssetsManager.Services.Hashes;

namespace AssetsManager.Views.Controls.Comparator
{
    public class LoadWadComparisonEventArgs : EventArgs
    {
        public List<SerializableChunkDiff> Diffs { get; }
        public string OldPath { get; }
        public string NewPath { get; }
        public string JsonPath { get; }

        public LoadWadComparisonEventArgs(List<SerializableChunkDiff> diffs, string oldPath, string newPath, string jsonPath)
        {
            Diffs = diffs;
            OldPath = oldPath;
            NewPath = newPath;
            JsonPath = jsonPath;
        }
    }

    public partial class WadComparisonControl : UserControl
    {
        public WadComparatorService WadComparatorService { get; set; }
        public LogService LogService { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public DirectoriesCreator DirectoriesCreator { get; set; }
        public AssetDownloader AssetDownloaderService { get; set; }
        public WadDifferenceService WadDifferenceService { get; set; }
        public WadPackagingService WadPackagingService { get; set; }
        public BackupManager BackupManager { get; set; }
        public AppSettings AppSettings { get; set; }
        public IServiceProvider ServiceProvider { get; set; }
        public DiffViewService DiffViewService { get; set; }
        public HashResolverService HashResolverService { get; set; }
        public TaskCancellationManager TaskCancellationManager { get; set; }

        private string _oldLolPath;
        private string _newLolPath;

        public WadComparisonControl()
        {
            InitializeComponent();
        }

        private void btnSelectOldLolPbeDirectory_Click(object sender, RoutedEventArgs e)
        {
            using (var folderBrowserDialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select old directory",
                InitialDirectory = AppSettings.LolPbeDirectory
            })
            {
                if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    var oldPath = folderBrowserDialog.FileName;
                    oldLolPbeDirectoryTextBox.Text = oldPath;
                    newLolPbeDirectoryTextBox.Text = oldPath.Replace("(PBE)_old", "(PBE)");
                    LogService.LogDebug($"Old Directory selected: {oldPath}");
                }
            }
        }

        private void btnSelectNewLolPbeDirectory_Click(object sender, RoutedEventArgs e)
        {
            using (var folderBrowserDialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select new directory",
                InitialDirectory = AppSettings.LolPbeDirectory
            })
            {
                if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    var newPath = folderBrowserDialog.FileName;
                    newLolPbeDirectoryTextBox.Text = newPath;
                    oldLolPbeDirectoryTextBox.Text = newPath.Replace("(PBE)", "(PBE)_old");
                    LogService.LogDebug($"New Directory selected: {newPath}");
                }
            }
        }

        private void btnSelectOldWadFile_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new CommonOpenFileDialog
            {
                Filters = { new CommonFileDialogFilter("WAD files", "*.wad;*.wad.client"), new CommonFileDialogFilter("All files", "*.*") },
                Title = "Select old wad file",
                InitialDirectory = AppSettings.LolPbeDirectory
            };

            if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                var oldPath = openFileDialog.FileName;
                oldWadFileTextBox.Text = oldPath;
                newWadFileTextBox.Text = oldPath.Replace("(PBE)_old", "(PBE)");
            }
        }

        private void btnSelectNewWadFile_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new CommonOpenFileDialog
            {
                Filters = { new CommonFileDialogFilter("WAD files", "*.wad;*.wad.client"), new CommonFileDialogFilter("All files", "*.*") },
                Title = "Select new wad file",
                InitialDirectory = AppSettings.LolPbeDirectory
            };

            if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                var newPath = openFileDialog.FileName;
                newWadFileTextBox.Text = newPath;
                oldWadFileTextBox.Text = newPath.Replace("(PBE)", "(PBE)_old");
            }
        }

        private async void compareWadButton_Click(object sender, RoutedEventArgs e)
        {
            compareWadButton.IsEnabled = false;

            try
            {
                var cancellationToken = TaskCancellationManager.PrepareNewOperation();
                if (ModeDirectory.IsChecked == true) // By Directory
                {
                    if (string.IsNullOrEmpty(oldLolPbeDirectoryTextBox.Text) || string.IsNullOrEmpty(newLolPbeDirectoryTextBox.Text))
                    {
                        CustomMessageBoxService.ShowWarning("Warning", "Please select both directories.", Window.GetWindow(this));
                        return; // Return directly as button.IsEnabled will be set in finally
                    }
                    _oldLolPath = oldLolPbeDirectoryTextBox.Text;
                    _newLolPath = newLolPbeDirectoryTextBox.Text;
                    await WadComparatorService.CompareWadsAsync(_oldLolPath, _newLolPath, cancellationToken);
                }
                else // By File
                {
                    if (string.IsNullOrEmpty(oldWadFileTextBox.Text) || string.IsNullOrEmpty(newWadFileTextBox.Text))
                    {
                        CustomMessageBoxService.ShowWarning("Warning", "Please select both WAD files.", Window.GetWindow(this));
                        return; // Return directly as button.IsEnabled will be set in finally
                    }
                    await WadComparatorService.CompareSingleWadAsync(oldWadFileTextBox.Text, newWadFileTextBox.Text, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                LogService.LogWarning("WAD comparison was cancelled by the user.");
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "An error occurred during comparison.");
                CustomMessageBoxService.ShowError("Error", $"An error occurred during comparison: {ex.Message}", Window.GetWindow(this));
            }
            finally
            {
                compareWadButton.IsEnabled = true;
            }
        }
    }
}
