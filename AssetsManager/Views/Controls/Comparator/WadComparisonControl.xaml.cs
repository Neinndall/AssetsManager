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
                // Defensive pattern to avoid duplicate subscriptions on reload
                AppSettings.ConfigurationSaved -= OnConfigurationSaved;
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

            // Clear heavy data from memory when not in use
            if (ViewModel != null)
            {
                ViewModel.AvailableBackups.Clear();
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
            string defaultPath = GetPreferredInitialDirectory();
            if (!string.IsNullOrEmpty(defaultPath))
            {
                ViewModel.NewDirectoryPath = defaultPath;
                await ViewModel.UpdateMetadataFromPathAsync(false, defaultPath, VersionService, BackupManager);

                // --- DIRECTORY AUTO-SYNC ---
                if (ViewModel.IsDirectoryMode && string.IsNullOrEmpty(ViewModel.OldDirectoryPath))
                {
                    var (isPbe, _) = BackupManager.GetPathIdentification(defaultPath);
                    var suggestedBackup = ViewModel.AvailableBackups
                        .Where(b => !b.IsMainClient && b.IsPbe == isPbe)
                        .OrderByDescending(b => b.CreationDate)
                        .FirstOrDefault();

                    if (suggestedBackup != null)
                    {
                        ViewModel.SelectedBaseBackup = suggestedBackup;
                        await ViewModel.UpdateMetadataFromPathAsync(true, suggestedBackup.Path, VersionService, BackupManager);
                    }
                }
            }
        }

        private string GetPreferredInitialDirectory()
        {
            if (AppSettings == null) return null;
            if (AppSettings.PreferredClient == PreferredClient.PBE && !string.IsNullOrEmpty(AppSettings.LolPbeDirectory))
                return AppSettings.LolPbeDirectory;
            else if (AppSettings.PreferredClient == PreferredClient.LIVE && !string.IsNullOrEmpty(AppSettings.LolLiveDirectory))
                return AppSettings.LolLiveDirectory;
            return !string.IsNullOrEmpty(AppSettings.LolPbeDirectory) ? AppSettings.LolPbeDirectory : AppSettings.LolLiveDirectory;
        }

        private async Task LoadBackupsAsync()
        {
            if (BackupManager == null || ViewModel == null) return;
            try
            {
                var backups = await BackupManager.GetBackupsAsync();
                ViewModel.AvailableBackups.Clear();
                foreach (var backup in backups) { ViewModel.AvailableBackups.Add(backup); }
            }
            catch (Exception ex) { LogService.LogError(ex, "Error loading backups."); }
        }

        private async void BaseQuickSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is BackupModel backup)
            {
                await ViewModel.UpdateMetadataFromPathAsync(true, backup.Path, VersionService, BackupManager);
                if (ViewModel.IsFileMode)
                {
                    await SyncWadFilePathsAsync(backup.Path);
                }
            }
        }

        private async void TargetQuickSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is BackupModel backup)
            {
                await ViewModel.UpdateMetadataFromPathAsync(false, backup.Path, VersionService, BackupManager);

                // --- DIRECTORY AUTO-SYNC ---
                if (ViewModel.IsDirectoryMode && string.IsNullOrEmpty(ViewModel.OldDirectoryPath))
                {
                    var suggestedBackup = ViewModel.AvailableBackups
                        .Where(b => !b.IsMainClient && b.IsPbe == backup.IsPbe)
                        .OrderByDescending(b => b.CreationDate)
                        .FirstOrDefault();

                    if (suggestedBackup != null)
                    {
                        ViewModel.SelectedBaseBackup = suggestedBackup;
                        await ViewModel.UpdateMetadataFromPathAsync(true, suggestedBackup.Path, VersionService, BackupManager);
                    }
                }
            }
        }

        private async void btnSelectOldLolPbeDirectory_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            using (var folderBrowserDialog = new CommonOpenFileDialog { IsFolderPicker = true, Title = "Select old directory", InitialDirectory = GetPreferredInitialDirectory() })
            {
                if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    ViewModel.OldDirectoryPath = folderBrowserDialog.FileName;
                    await ViewModel.UpdateMetadataFromPathAsync(true, ViewModel.OldDirectoryPath, VersionService, BackupManager);
                }
            }
        }

        private async void btnSelectNewLolPbeDirectory_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            using (var folderBrowserDialog = new CommonOpenFileDialog { IsFolderPicker = true, Title = "Select new directory", InitialDirectory = GetPreferredInitialDirectory() })
            {
                if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    string newPath = folderBrowserDialog.FileName;
                    ViewModel.NewDirectoryPath = newPath;
                    await ViewModel.UpdateMetadataFromPathAsync(false, newPath, VersionService, BackupManager);

                    // --- DIRECTORY AUTO-SYNC ---
                    if (string.IsNullOrEmpty(ViewModel.OldDirectoryPath))
                    {
                        var (isPbe, _) = BackupManager.GetPathIdentification(newPath);
                        var suggestedBackup = ViewModel.AvailableBackups
                            .Where(b => !b.IsMainClient && b.IsPbe == isPbe)
                            .OrderByDescending(b => b.CreationDate)
                            .FirstOrDefault();

                        if (suggestedBackup != null)
                        {
                            ViewModel.SelectedBaseBackup = suggestedBackup;
                            await ViewModel.UpdateMetadataFromPathAsync(true, suggestedBackup.Path, VersionService, BackupManager);
                        }
                    }
                }
            }
        }

        private async void btnSelectOldWadFile_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var openFileDialog = new CommonOpenFileDialog { Filters = { new CommonFileDialogFilter("WAD files", "*.wad;*.wad.client"), new CommonFileDialogFilter("All files", "*.*") }, Title = "Select old wad file", InitialDirectory = GetPreferredInitialDirectory() };
            if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                ViewModel.OldWadFilePath = openFileDialog.FileName;
                await ViewModel.UpdateMetadataFromPathAsync(true, ViewModel.OldWadFilePath, VersionService, BackupManager);
            }
        }

        private async void btnSelectNewWadFile_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var openFileDialog = new CommonOpenFileDialog { Filters = { new CommonFileDialogFilter("WAD files", "*.wad;*.wad.client"), new CommonFileDialogFilter("All files", "*.*") }, Title = "Select new wad file", InitialDirectory = GetPreferredInitialDirectory() };
            
            if (openFileDialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                string newPath = openFileDialog.FileName;
                ViewModel.NewWadFilePath = newPath;
                await ViewModel.UpdateMetadataFromPathAsync(false, newPath, VersionService, BackupManager);
                
                // --- ROBUST AUTO-SYNC ENGINE ---
                if (string.IsNullOrEmpty(ViewModel.OldWadFilePath))
                {
                    var (isPbe, _) = BackupManager.GetPathIdentification(newPath);

                    var suggestedBackup = ViewModel.AvailableBackups
                        .Where(b => !b.IsMainClient && b.IsPbe == isPbe)
                        .OrderByDescending(b => b.CreationDate)
                        .FirstOrDefault();

                    if (suggestedBackup == null)
                        suggestedBackup = ViewModel.AvailableBackups.FirstOrDefault(b => b.IsMainClient && b.IsPbe == !isPbe);

                    if (suggestedBackup != null)
                    {
                        ViewModel.SelectedBaseBackup = suggestedBackup;
                        await SyncWadFilePathsAsync(suggestedBackup.Path);
                        return; 
                    }
                }
                await SyncWadFilePathsAsync();
            }
        }

        private async Task SyncWadFilePathsAsync(string overrideBaseRoot = null)
        {
            if (ViewModel == null || !ViewModel.IsFileMode || string.IsNullOrEmpty(ViewModel.NewWadFilePath)) return;

            string targetRoot = GetBaseGameDirectory(ViewModel.NewWadFilePath);
            if (string.IsNullOrEmpty(targetRoot)) return;

            try 
            {
                string relativePath = System.IO.Path.GetRelativePath(targetRoot, ViewModel.NewWadFilePath);
                string baseRoot = overrideBaseRoot ?? ViewModel.BaseSourceRoot;

                if (!string.IsNullOrEmpty(baseRoot))
                {
                    string expectedPath = System.IO.Path.Combine(baseRoot, relativePath);
                    bool exists = await Task.Run(() => System.IO.File.Exists(expectedPath));
                    
                    if (!exists && expectedPath.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
                    {
                        string altPath = expectedPath.Substring(0, expectedPath.Length - 7);
                        if (await Task.Run(() => System.IO.File.Exists(altPath))) { expectedPath = altPath; exists = true; }
                    }

                    if (exists)
                    {
                        ViewModel.OldWadFilePath = expectedPath;
                        await ViewModel.UpdateMetadataFromPathAsync(true, expectedPath, VersionService, BackupManager);
                    }
                }
            }
            catch (Exception ex) { LogService.LogError(ex, "Error during WAD sync."); }
        }

        private string GetBaseGameDirectory(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            filePath = System.IO.Path.GetFullPath(filePath);

            if (!string.IsNullOrEmpty(AppSettings.LolPbeDirectory) && filePath.StartsWith(AppSettings.LolPbeDirectory, StringComparison.OrdinalIgnoreCase))
                return AppSettings.LolPbeDirectory;
            if (!string.IsNullOrEmpty(AppSettings.LolLiveDirectory) && filePath.StartsWith(AppSettings.LolLiveDirectory, StringComparison.OrdinalIgnoreCase))
                return AppSettings.LolLiveDirectory;

            foreach (var backup in ViewModel.AvailableBackups)
            {
                if (filePath.StartsWith(backup.Path, StringComparison.OrdinalIgnoreCase))
                    return backup.Path;
            }

            string dir = System.IO.Path.GetDirectoryName(filePath);
            while (!string.IsNullOrEmpty(dir))
            {
                if (System.IO.Directory.Exists(System.IO.Path.Combine(dir, "Game")) || System.IO.Directory.Exists(System.IO.Path.Combine(dir, "Plugins")))
                    return dir;
                dir = System.IO.Path.GetDirectoryName(dir);
            }
            return System.IO.Path.GetDirectoryName(filePath);
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
                if (ViewModel.IsDirectoryMode) await WadComparatorService.CompareWadsAsync(ViewModel.BaseSourcePath, ViewModel.TargetSourcePath, cancellationToken);
                else await WadComparatorService.CompareSingleWadAsync(ViewModel.BaseSourcePath, ViewModel.TargetSourcePath, cancellationToken);
            }
            catch (OperationCanceledException) { LogService.LogWarning("WAD comparison cancelled."); }
            catch (Exception ex) { LogService.LogError(ex, "Comparison error."); CustomMessageBoxService.ShowError("Error", ex.Message, Window.GetWindow(this)); }
            finally { ViewModel.IsComparing = false; }
        }
    }
}
