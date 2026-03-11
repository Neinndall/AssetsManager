using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Threading.Tasks;
using Microsoft.WindowsAPICodePack.Dialogs;
using AssetsManager.Views.Models.Comparator;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using AssetsManager.Services.Hashes;

namespace AssetsManager.Views.Controls.Comparator
{
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

        public WadComparisonModel ViewModel => DataContext as WadComparisonModel;

        public WadComparisonControl()
        {
            InitializeComponent();
        }

        private void btnSelectOldLolPbeDirectory_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;

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
                    ViewModel.OldDirectoryPath = oldPath;
                    ViewModel.NewDirectoryPath = oldPath.Replace("(PBE)_old", "(PBE)");
                    LogService.LogDebug($"Old Directory selected: {oldPath}");
                }
            }
        }

        private void btnSelectNewLolPbeDirectory_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;

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
                    ViewModel.NewDirectoryPath = newPath;
                    ViewModel.OldDirectoryPath = newPath.Replace("(PBE)", "(PBE)_old");
                    LogService.LogDebug($"New Directory selected: {newPath}");
                }
            }
        }

        private void btnSelectOldWadFile_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;

            var openFileDialog = new CommonOpenFileDialog
            {
                Filters = { new CommonFileDialogFilter("WAD files", "*.wad;*.wad.client"), new CommonFileDialogFilter("All files", "*.*") },
                Title = "Select old wad file",
                InitialDirectory = AppSettings.LolPbeDirectory
            };

            if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                var oldPath = openFileDialog.FileName;
                ViewModel.OldWadFilePath = oldPath;
                ViewModel.NewWadFilePath = oldPath.Replace("(PBE)_old", "(PBE)");
            }
        }

        private void btnSelectNewWadFile_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;

            var openFileDialog = new CommonOpenFileDialog
            {
                Filters = { new CommonFileDialogFilter("WAD files", "*.wad;*.wad.client"), new CommonFileDialogFilter("All files", "*.*") },
                Title = "Select new wad file",
                InitialDirectory = AppSettings.LolPbeDirectory
            };

            if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                var newPath = openFileDialog.FileName;
                ViewModel.NewWadFilePath = newPath;
                ViewModel.OldWadFilePath = newPath.Replace("(PBE)", "(PBE)_old");
            }
        }

        private async void compareWadButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;

            ViewModel.IsComparing = true;

            try
            {
                var cancellationToken = TaskCancellationManager.PrepareNewOperation();
                if (ViewModel.IsDirectoryMode) // By Directory
                {
                    if (string.IsNullOrEmpty(ViewModel.OldDirectoryPath) || string.IsNullOrEmpty(ViewModel.NewDirectoryPath))
                    {
                        CustomMessageBoxService.ShowWarning("Warning", "Please select both directories.", Window.GetWindow(this));
                        return;
                    }
                    await WadComparatorService.CompareWadsAsync(ViewModel.OldDirectoryPath, ViewModel.NewDirectoryPath, cancellationToken);
                }
                else // By File
                {
                    if (string.IsNullOrEmpty(ViewModel.OldWadFilePath) || string.IsNullOrEmpty(ViewModel.NewWadFilePath))
                    {
                        CustomMessageBoxService.ShowWarning("Warning", "Please select both WAD files.", Window.GetWindow(this));
                        return;
                    }
                    await WadComparatorService.CompareSingleWadAsync(ViewModel.OldWadFilePath, ViewModel.NewWadFilePath, cancellationToken);
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
                ViewModel.IsComparing = false;
            }
        }
    }
}
