using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
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

        private string _lastPreferredClientKey;

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

            _lastPreferredClientKey = GetPreferredClientKey();
            await InitializeAsync();
        }

        private void WadComparisonControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (AppSettings != null)
            {
                AppSettings.ConfigurationSaved -= OnConfigurationSaved;
            }
        }

        private async Task InitializeAsync()
        {
            string defaultPath = GetPreferredInitialDirectory();
            Task targetMetadataTask = Task.CompletedTask;

            // Publish the configured target path before scanning backups so
            // the read-only field is populated during the first render.
            if (!string.IsNullOrEmpty(defaultPath))
            {
                SetPathWithSync(false, defaultPath);
                targetMetadataTask = ViewModel.UpdateMetadataFromPathAsync(
                    false,
                    defaultPath,
                    VersionService,
                    BackupManager);
            }

            await LoadBackupsAsync();
            await targetMetadataTask;
            await InitializeDefaultPathsAsync(defaultPath);
        }

        private async void OnConfigurationSaved(object sender, EventArgs e)
        {
            string preferredClientKey = GetPreferredClientKey();
            bool preferredClientConfigurationChanged = !string.Equals(
                _lastPreferredClientKey,
                preferredClientKey,
                StringComparison.OrdinalIgnoreCase);
            _lastPreferredClientKey = preferredClientKey;

            await Dispatcher.InvokeAsync(async () =>
            {
                await LoadBackupsAsync();
                await InitializeDefaultPathsAsync(
                    resetSources: preferredClientConfigurationChanged);
            });
        }

        private string GetPreferredClientKey() =>
            $"{AppSettings?.PreferredClient}:{GetPreferredInitialDirectory()}";

        private void SetPathWithSync(bool isBase, string path)
        {
            if (ViewModel == null) return;

            var match = ViewModel.AvailableBackups.FirstOrDefault(b =>
                string.Equals(ViewModel.ApplySyncSuffix(b.Path), path, StringComparison.OrdinalIgnoreCase));
            if (isBase)
            {
                if (match != null) ViewModel.SelectedBaseBackup = match;
                else ViewModel.OldDirectoryPath = path;
            }
            else
            {
                if (match != null) ViewModel.SelectedTargetBackup = match;
                else ViewModel.NewDirectoryPath = path;
            }
        }

        private string GetRelativeSubDirectory(string path)
        {
            if (string.IsNullOrEmpty(path) || BackupManager == null) return null;
            string root = BackupManager.GetGameRoot(path);
            if (string.IsNullOrEmpty(root) || string.Equals(root, path, StringComparison.OrdinalIgnoreCase)) return null;
            string relative = System.IO.Path.GetRelativePath(root, path);
            if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal)) return null;
            return relative;
        }

        private async Task SyncDirectoryBaseAsync(string targetPath)
        {
            if (ViewModel == null || !ViewModel.IsDirectoryMode || !string.IsNullOrEmpty(ViewModel.OldDirectoryPath)) return;

            var (isPbe, _) = BackupManager.GetPathIdentification(targetPath);
            var suggestedBackup = ViewModel.AvailableBackups
                .Where(b => !b.IsMainClient && b.IsPbe == isPbe)
                .OrderByDescending(b => b.CreationDate)
                .FirstOrDefault();

            if (suggestedBackup == null) return;

            ViewModel.SelectedBaseBackup = suggestedBackup;
            await ViewModel.UpdateMetadataFromPathAsync(
                true,
                ViewModel.ApplySyncSuffix(suggestedBackup.Path),
                VersionService,
                BackupManager);
        }

        private async Task InitializeDefaultPathsAsync(
            string defaultPath = null,
            bool resetSources = false)
        {
            if (ViewModel == null || AppSettings == null || VersionService == null) return;
            if (resetSources)
            {
                ViewModel.SelectedTargetBackup = null;
                ViewModel.SelectedBaseBackup = null;
                if (ViewModel.IsDirectoryMode)
                {
                    ViewModel.NewDirectoryPath = null;
                    ViewModel.OldDirectoryPath = null;
                }
                else
                {
                    ViewModel.NewWadFilePath = null;
                    ViewModel.OldWadFilePath = null;
                }
                ViewModel.ClearMetadata(false);
                ViewModel.ClearMetadata(true);
            }

            defaultPath ??= GetPreferredInitialDirectory();
            if (!string.IsNullOrEmpty(defaultPath))
            {
                SetPathWithSync(false, defaultPath);
                ViewModel.DirectorySyncSuffix = GetRelativeSubDirectory(defaultPath);
                await ViewModel.UpdateMetadataFromPathAsync(false, defaultPath, VersionService, BackupManager);

                // --- DIRECTORY AUTO-SYNC ---
                if (ViewModel.IsDirectoryMode && string.IsNullOrEmpty(ViewModel.OldDirectoryPath))
                {
                    await SyncDirectoryBaseAsync(defaultPath);
                }
            }
        }

        private string GetPreferredInitialDirectory()
        {
            if (AppSettings == null) return null;
            return AppSettings.PreferredClient == PreferredClient.PBE
                ? AppSettings.LolPbeDirectory
                : AppSettings.LolLiveDirectory;
        }

        private async Task LoadBackupsAsync()
        {
            if (BackupManager == null || ViewModel == null) return;
            try
            {
                PreferredClient client = AppSettings?.PreferredClient ?? PreferredClient.PBE;
                var backups = await BackupManager.GetBackupsAsync(
                    includeStorageMetrics: false,
                    client: client);
                ViewModel.AvailableBackups.Clear();
                foreach (var backup in backups) { ViewModel.AvailableBackups.Add(backup); }

                // Re-sync selections after collection update to ensure reference matching
                if (!string.IsNullOrEmpty(ViewModel.NewDirectoryPath)) SetPathWithSync(false, ViewModel.NewDirectoryPath);
                if (!string.IsNullOrEmpty(ViewModel.OldDirectoryPath)) SetPathWithSync(true, ViewModel.OldDirectoryPath);
            }
            catch (Exception ex) { LogService.LogError(ex, "Error loading backups."); }
        }

        private async void BaseQuickSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is BackupModel backup)
            {
                string effectivePath = ViewModel.IsDirectoryMode
                    ? ViewModel.ApplySyncSuffix(backup.Path)
                    : backup.Path;
                await ViewModel.UpdateMetadataFromPathAsync(true, effectivePath, VersionService, BackupManager);
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
                string effectivePath = ViewModel.IsDirectoryMode
                    ? ViewModel.ApplySyncSuffix(backup.Path)
                    : backup.Path;
                await ViewModel.UpdateMetadataFromPathAsync(false, effectivePath, VersionService, BackupManager);

                // --- DIRECTORY AUTO-SYNC ---
                if (ViewModel.IsDirectoryMode && string.IsNullOrEmpty(ViewModel.OldDirectoryPath))
                {
                    await SyncDirectoryBaseAsync(effectivePath);
                }
            }
        }

        private async void btnSelectOldLolPbeDirectory_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var folderBrowserDialog = new OpenFolderDialog { Title = "Select old directory", InitialDirectory = GetPreferredInitialDirectory() };
            if (folderBrowserDialog.ShowDialog() == true)
            {
                string oldPath = folderBrowserDialog.FolderName;
                SetPathWithSync(true, oldPath);
                await ViewModel.UpdateMetadataFromPathAsync(true, oldPath, VersionService, BackupManager);
            }
        }

        private async void btnSelectNewLolPbeDirectory_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var folderBrowserDialog = new OpenFolderDialog { Title = "Select new directory", InitialDirectory = GetPreferredInitialDirectory() };
            if (folderBrowserDialog.ShowDialog() == true)
            {
                string newPath = folderBrowserDialog.FolderName;
                SetPathWithSync(false, newPath);
                ViewModel.DirectorySyncSuffix = GetRelativeSubDirectory(newPath);
                await ViewModel.UpdateMetadataFromPathAsync(false, newPath, VersionService, BackupManager);

                // --- DIRECTORY AUTO-SYNC ---
                if (string.IsNullOrEmpty(ViewModel.OldDirectoryPath))
                {
                    await SyncDirectoryBaseAsync(newPath);
                }
            }
        }

        private async void btnSelectOldWadFile_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var openFileDialog = new OpenFileDialog { Filter = "WAD files (*.wad;*.wad.client)|*.wad;*.wad.client|All files (*.*)|*.*", Title = "Select old wad file", InitialDirectory = GetPreferredInitialDirectory() };
            if (openFileDialog.ShowDialog() == true)
            {
                ViewModel.OldWadFilePath = openFileDialog.FileName;
                await ViewModel.UpdateMetadataFromPathAsync(true, ViewModel.OldWadFilePath, VersionService, BackupManager);
            }
        }

        private async void btnSelectNewWadFile_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var openFileDialog = new OpenFileDialog { Filter = "WAD files (*.wad;*.wad.client)|*.wad;*.wad.client|All files (*.*)|*.*", Title = "Select new wad file", InitialDirectory = GetPreferredInitialDirectory() };
            
            if (openFileDialog.ShowDialog() == true)
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
                if (ViewModel.IsDirectoryMode) await WadComparatorService.CompareWadsAsync(ViewModel.BaseSourcePath, ViewModel.TargetSourcePath, ViewModel.TargetVersion, cancellationToken);
                else await WadComparatorService.CompareSingleWadAsync(ViewModel.BaseSourcePath, ViewModel.TargetSourcePath, ViewModel.TargetVersion, cancellationToken);
            }
            catch (OperationCanceledException) { LogService.LogWarning("WAD comparison cancelled."); }
            catch (Exception ex) { LogService.LogError(ex, "Comparison error."); CustomMessageBoxService.ShowError("Error", ex.Message, Window.GetWindow(this)); }
            finally { ViewModel.IsComparing = false; }
        }
    }
}
