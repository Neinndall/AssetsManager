using AssetsManager.Views.Dialogs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;

using Microsoft.WindowsAPICodePack.Dialogs;
using AssetsManager.Views.Models;
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

        private string _oldLolPath;
        private string _newLolPath;

        public WadComparisonControl()
        {
            InitializeComponent();
        }

        private async void createLolBackupButton_Click(object sender, RoutedEventArgs e)
        {
            string sourceLolPath = AppSettings.LolDirectory;

            if (string.IsNullOrEmpty(sourceLolPath))
            {
                CustomMessageBoxService.ShowWarning("Warning", "LoL directory is not configured. Please set it in Settings > Default Paths.", Window.GetWindow(this));
                return;
            }

            if (!Directory.Exists(sourceLolPath))
            {
                CustomMessageBoxService.ShowError("Error", $"The configured LoL directory does not exist: {sourceLolPath}", Window.GetWindow(this));
                return;
            }

            string destinationBackupPath = sourceLolPath + "_old";

            createLolBackupButton.IsEnabled = false;
            try
            {
                await BackupManager.CreateLolDirectoryBackupAsync(sourceLolPath, destinationBackupPath);
                CustomMessageBoxService.ShowInfo("Info", "LoL backup completed successfully.", Window.GetWindow(this));
            }
            catch (DirectoryNotFoundException ex)
            {
                LogService.LogError(ex, "Error creating LoL backup");
                CustomMessageBoxService.ShowError("Error", ex.Message, Window.GetWindow(this));
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Error creating LoL backup");
                CustomMessageBoxService.ShowError("Error", $"An unexpected error occurred while creating the backup: {ex.Message}", Window.GetWindow(this));
            }
            finally
            {
                createLolBackupButton.IsEnabled = true;
            }
        }

        private void btnSelectOldLolDirectory_Click(object sender, RoutedEventArgs e)
        {
            using (var folderBrowserDialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select old directory",
                InitialDirectory = AppSettings.LolDirectory
            })
            {
                if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    var oldPath = folderBrowserDialog.FileName;
                    oldLolDirectoryTextBox.Text = oldPath;
                    newLolDirectoryTextBox.Text = oldPath.Replace("(PBE)_old", "(PBE)");
                    LogService.LogDebug($"Old Directory selected: {oldPath}");
                }
            }
        }

        private void btnSelectNewLolDirectory_Click(object sender, RoutedEventArgs e)
        {
            using (var folderBrowserDialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select new directory",
                InitialDirectory = AppSettings.LolDirectory
            })
            {
                if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    var newPath = folderBrowserDialog.FileName;
                    newLolDirectoryTextBox.Text = newPath;
                    oldLolDirectoryTextBox.Text = newPath.Replace("(PBE)", "(PBE)_old");
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
                InitialDirectory = AppSettings.LolDirectory
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
                InitialDirectory = AppSettings.LolDirectory
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
                if (wadComparatorTabControl.SelectedIndex == 0) // By Directory
                {
                    if (string.IsNullOrEmpty(oldLolDirectoryTextBox.Text) || string.IsNullOrEmpty(newLolDirectoryTextBox.Text))
                    {
                        CustomMessageBoxService.ShowWarning("Warning", "Please select both directories.", Window.GetWindow(this));
                        compareWadButton.IsEnabled = true;
                        return;
                    }
                    _oldLolPath = oldLolDirectoryTextBox.Text;
                    _newLolPath = newLolDirectoryTextBox.Text;
                    await WadComparatorService.CompareWadsAsync(_oldLolPath, _newLolPath);
                }
                else // By File
                {
                    if (string.IsNullOrEmpty(oldWadFileTextBox.Text) || string.IsNullOrEmpty(newWadFileTextBox.Text))
                    {
                        CustomMessageBoxService.ShowWarning("Warning", "Please select both WAD files.", Window.GetWindow(this));
                        compareWadButton.IsEnabled = true;
                        return;
                    }
                    await WadComparatorService.CompareSingleWadAsync(oldWadFileTextBox.Text, newWadFileTextBox.Text);
                }
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