using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.WindowsAPICodePack.Dialogs;
using AssetsManager.Views.Models.Comparator;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Services.Monitor;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Views.Models.Settings;

namespace AssetsManager.Views.Controls.Comparator
{
    public partial class WadComparisonControl : UserControl
    {
        public WadComparatorService WadComparatorService { get; set; }
        public LogService LogService { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public AppSettings AppSettings { get; set; }
        public TaskCancellationManager TaskCancellationManager { get; set; }
        public BackupManager BackupManager { get; set; }
        public VersionService VersionService { get; set; }

        public WadComparisonModel ViewModel => DataContext as WadComparisonModel;

        public WadComparisonControl()
        {
            InitializeComponent();
            this.Loaded += WadComparisonControl_Loaded;
            this.Unloaded += WadComparisonControl_Unloaded;
        }

        private async void WadComparisonControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (AppSettings != null)
            {
                AppSettings.ConfigurationSaved += OnConfigurationSaved;
            }
            await LoadBackupsAsync();
            await InitializeDefaultPathsAsync();
        }

        private void WadComparisonControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (AppSettings != null)
            {
                AppSettings.ConfigurationSaved -= OnConfigurationSaved;
            }
        }

        private async void OnConfigurationSaved(object sender, EventArgs e)
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                await LoadBackupsAsync();
                await InitializeDefaultPathsAsync();
            });
        }

        private async Task InitializeDefaultPathsAsync()
        {
            if (ViewModel == null || AppSettings == null || VersionService == null) return;

            string defaultPath = null;

            // Respect user preference for default active client
            if (AppSettings.PreferredBackupClient == PreferredClient.PBE && !string.IsNullOrEmpty(AppSettings.LolPbeDirectory))
            {
                defaultPath = AppSettings.LolPbeDirectory;
            }
            else if (AppSettings.PreferredBackupClient == PreferredClient.LIVE && !string.IsNullOrEmpty(AppSettings.LolLiveDirectory))
            {
                defaultPath = AppSettings.LolLiveDirectory;
            }
            else
            {
                // Fallback if preferred is not set or not available
                defaultPath = !string.IsNullOrEmpty(AppSettings.LolPbeDirectory) ? AppSettings.LolPbeDirectory : AppSettings.LolLiveDirectory;
            }

            if (!string.IsNullOrEmpty(defaultPath))
            {
                ViewModel.NewDirectoryPath = defaultPath;
                await ViewModel.UpdateMetadataFromPathAsync(false, ViewModel.NewDirectoryPath, VersionService, BackupManager);
            }
        }

        private async Task LoadBackupsAsync()
        {
            if (BackupManager == null || ViewModel == null) return;

            try
            {
                var backups = await BackupManager.GetBackupsAsync();
                ViewModel.AvailableBackups.Clear();
                foreach (var backup in backups)
                {
                    ViewModel.AvailableBackups.Add(backup);
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Error loading backups for comparator.");
            }
        }

        private async void BaseQuickSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is BackupModel backup)
            {
                await ViewModel.UpdateMetadataFromPathAsync(true, backup.Path, VersionService, BackupManager);
                
                if (ViewModel.IsFileMode)
                {
                    await SyncWadFilePathsAsync();
                }
            }
        }

        private async void TargetQuickSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is BackupModel backup)
            {
                await ViewModel.UpdateMetadataFromPathAsync(false, backup.Path, VersionService, BackupManager);
                
                if (ViewModel.IsFileMode)
                {
                    await SyncWadFilePathsAsync();
                }
            }
        }

        private async void btnSelectOldLolPbeDirectory_Click(object sender, RoutedEventArgs e)
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
                    ViewModel.OldDirectoryPath = folderBrowserDialog.FileName;
                    await ViewModel.UpdateMetadataFromPathAsync(true, ViewModel.OldDirectoryPath, VersionService, BackupManager);
                    
                    if (ViewModel.IsFileMode)
                    {
                        await SyncWadFilePathsAsync();
                    }
                }
            }
        }

        private async void btnSelectNewLolPbeDirectory_Click(object sender, RoutedEventArgs e)
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
                    ViewModel.NewDirectoryPath = folderBrowserDialog.FileName;
                    await ViewModel.UpdateMetadataFromPathAsync(false, ViewModel.NewDirectoryPath, VersionService, BackupManager);
                }
            }
        }

        private async void btnSelectOldWadFile_Click(object sender, RoutedEventArgs e)
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
                ViewModel.OldWadFilePath = openFileDialog.FileName;
                await ViewModel.UpdateMetadataFromPathAsync(true, System.IO.Path.GetDirectoryName(ViewModel.OldWadFilePath), VersionService, BackupManager);
            }
        }

        private async void btnSelectNewWadFile_Click(object sender, RoutedEventArgs e)
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
                ViewModel.NewWadFilePath = openFileDialog.FileName;
                await ViewModel.UpdateMetadataFromPathAsync(false, System.IO.Path.GetDirectoryName(ViewModel.NewWadFilePath), VersionService, BackupManager);
                
                await SyncWadFilePathsAsync();
            }
        }

        private async Task SyncWadFilePathsAsync()
        {
            if (ViewModel == null || !ViewModel.IsFileMode || string.IsNullOrEmpty(ViewModel.NewWadFilePath)) return;

            string targetRoot = GetBaseGameDirectory(ViewModel.NewWadFilePath);
            if (string.IsNullOrEmpty(targetRoot)) return;

            string relativePath = System.IO.Path.GetRelativePath(targetRoot, ViewModel.NewWadFilePath);

            // If we have a base source already (manual or backup), try to find the matching file there
            if (!string.IsNullOrEmpty(ViewModel.BaseSourcePath))
            {
                string expectedPath = System.IO.Path.Combine(ViewModel.BaseSourcePath, relativePath);
                
                // Use Task.Run for File.Exists to ensure zero UI lag if many checks were to happen
                bool exists = await Task.Run(() => System.IO.File.Exists(expectedPath));
                
                if (exists)
                {
                    ViewModel.OldWadFilePath = expectedPath;
                    await ViewModel.UpdateMetadataFromPathAsync(true, System.IO.Path.GetDirectoryName(expectedPath), VersionService, BackupManager);
                }
            }
        }

        private string GetBaseGameDirectory(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;

            if (!string.IsNullOrEmpty(AppSettings.LolPbeDirectory) && filePath.StartsWith(AppSettings.LolPbeDirectory, StringComparison.OrdinalIgnoreCase))
                return AppSettings.LolPbeDirectory;

            if (!string.IsNullOrEmpty(AppSettings.LolLiveDirectory) && filePath.StartsWith(AppSettings.LolLiveDirectory, StringComparison.OrdinalIgnoreCase))
                return AppSettings.LolLiveDirectory;

            foreach (var backup in ViewModel.AvailableBackups)
            {
                if (filePath.StartsWith(backup.Path, StringComparison.OrdinalIgnoreCase))
                    return backup.Path;
            }

            // Heuristic
            string dir = System.IO.Path.GetDirectoryName(filePath);
            while (!string.IsNullOrEmpty(dir))
            {
                if (System.IO.Directory.Exists(System.IO.Path.Combine(dir, "Game")))
                    return dir;
                dir = System.IO.Path.GetDirectoryName(dir);
            }

            return null;
        }

        private async void compareWadButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;

            ViewModel.IsComparing = true;

            try
            {
                var cancellationToken = TaskCancellationManager.PrepareNewOperation();
                
                if (string.IsNullOrEmpty(ViewModel.BaseSourcePath) || string.IsNullOrEmpty(ViewModel.TargetSourcePath))
                {
                    string msg = ViewModel.IsDirectoryMode ? "Please select both directories." : "Please select both WAD files.";
                    CustomMessageBoxService.ShowWarning("Warning", msg, Window.GetWindow(this));
                    return;
                }

                if (ViewModel.IsDirectoryMode) // By Directory
                {
                    await WadComparatorService.CompareWadsAsync(ViewModel.BaseSourcePath, ViewModel.TargetSourcePath, cancellationToken);
                }
                else // By File
                {
                    await WadComparatorService.CompareSingleWadAsync(ViewModel.BaseSourcePath, ViewModel.TargetSourcePath, cancellationToken);
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
