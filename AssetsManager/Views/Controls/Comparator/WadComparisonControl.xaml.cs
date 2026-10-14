using System;
using System.Linq;
using System.Threading;
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
        private bool _isUpdatingSources;
        private readonly SemaphoreSlim _refreshGate = new(1, 1);

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

            await RefreshSourcesAsync();
        }

        private void WadComparisonControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (AppSettings != null)
            {
                AppSettings.ConfigurationSaved -= OnConfigurationSaved;
            }
        }

        private async Task RefreshSourcesAsync()
        {
            await _refreshGate.WaitAsync();
            try
            {
                if (ViewModel == null || AppSettings == null || BackupManager == null || VersionService == null) return;

                string preferredClientKey = GetPreferredClientKey();
                bool resetSources = _lastPreferredClientKey != null
                    && !string.Equals(_lastPreferredClientKey, preferredClientKey, StringComparison.OrdinalIgnoreCase);
                _lastPreferredClientKey = preferredClientKey;

                _isUpdatingSources = true;
                try
                {
                    if (resetSources)
                    {
                        ViewModel.SelectedTargetBackup = null;
                        ViewModel.SelectedBaseBackup = null;
                        ViewModel.NewDirectoryPath = null;
                        ViewModel.OldDirectoryPath = null;
                        ViewModel.NewWadFilePath = null;
                        ViewModel.OldWadFilePath = null;
                    }

                    // Publish the initial target before the first asynchronous inventory scan.
                    if (ViewModel.IsDirectoryMode && string.IsNullOrEmpty(ViewModel.NewDirectoryPath))
                    {
                        string defaultPath = GetPreferredInitialDirectory();
                        ViewModel.DirectorySyncSuffix = GetRelativeSubDirectory(defaultPath);
                        SetPathWithSync(false, defaultPath);
                    }
                }
                finally
                {
                    _isUpdatingSources = false;
                }

                await LoadBackupsAsync();

                await ViewModel.UpdateMetadataFromPathAsync(
                    false, ViewModel.TargetSourcePath, VersionService, BackupManager);
                if (ViewModel.IsDirectoryMode && string.IsNullOrEmpty(ViewModel.OldDirectoryPath))
                {
                    await SyncDirectoryBaseAsync(ViewModel.NewDirectoryPath);
                }
                else
                {
                    await ViewModel.UpdateMetadataFromPathAsync(
                        true, ViewModel.BaseSourcePath, VersionService, BackupManager);
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Error refreshing comparison sources.");
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        private async void OnConfigurationSaved(object sender, EventArgs e)
        {
            await Dispatcher.InvokeAsync(RefreshSourcesAsync).Task.Unwrap();
        }


        private string GetPreferredClientKey() =>
            $"{AppSettings?.PreferredClient}:{GetPreferredInitialDirectory()}";

        private void SetPathWithSync(bool isBase, string path)
        {
            if (ViewModel == null) return;

            var match = ViewModel.AvailableBackups.FirstOrDefault(b =>
                string.Equals(ViewModel.IsDirectoryMode ? ViewModel.ApplySyncSuffix(b.Path) : b.Path,
                    path, StringComparison.OrdinalIgnoreCase));
            bool wasUpdatingSources = _isUpdatingSources;
            _isUpdatingSources = true;
            try
            {
                if (isBase)
                {
                    ViewModel.SelectedBaseBackup = match;
                    if (match == null && ViewModel.IsDirectoryMode) ViewModel.OldDirectoryPath = path;
                }
                else
                {
                    ViewModel.SelectedTargetBackup = match;
                    if (match == null && ViewModel.IsDirectoryMode) ViewModel.NewDirectoryPath = path;
                }
            }
            finally
            {
                _isUpdatingSources = wasUpdatingSources;
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
            if (ViewModel == null || !ViewModel.IsDirectoryMode || string.IsNullOrEmpty(targetPath)
                || !string.IsNullOrEmpty(ViewModel.OldDirectoryPath)) return;

            var (isPbe, _) = BackupManager.GetPathIdentification(targetPath);
            var suggestedBackup = ViewModel.AvailableBackups
                .Where(b => !b.IsMainClient && b.IsPbe == isPbe)
                .OrderByDescending(b => b.CreationDate)
                .FirstOrDefault();

            if (suggestedBackup == null) return;

            _isUpdatingSources = true;
            try
            {
                ViewModel.SelectedBaseBackup = suggestedBackup;
            }
            finally
            {
                _isUpdatingSources = false;
            }
            await ViewModel.UpdateMetadataFromPathAsync(
                true,
                ViewModel.ApplySyncSuffix(suggestedBackup.Path),
                VersionService,
                BackupManager);
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
                string targetPath = ViewModel.TargetSourceRoot;
                string basePath = ViewModel.BaseSourceRoot;
                var previousBaseBackup = ViewModel.SelectedBaseBackup
                    ?? ViewModel.AvailableBackups.FirstOrDefault(b =>
                        string.Equals(ViewModel.ApplySyncSuffix(b.Path), basePath, StringComparison.OrdinalIgnoreCase));
                bool baseBackupRemoved = ViewModel.IsDirectoryMode
                    && previousBaseBackup != null
                    && !previousBaseBackup.IsMainClient
                    && !backups.Any(b => string.Equals(b.Path, previousBaseBackup.Path, StringComparison.OrdinalIgnoreCase));

                // Collection resets clear ComboBox selections; preserve paths until rebinding completes.
                _isUpdatingSources = true;
                try
                {
                    ViewModel.AvailableBackups.Clear();
                    foreach (var backup in backups) { ViewModel.AvailableBackups.Add(backup); }

                    if (!string.IsNullOrEmpty(targetPath)) SetPathWithSync(false, targetPath);
                    if (baseBackupRemoved) ViewModel.OldDirectoryPath = null;
                    else if (!string.IsNullOrEmpty(basePath)) SetPathWithSync(true, basePath);
                }
                finally
                {
                    _isUpdatingSources = false;
                }

            }
            catch (Exception ex) { LogService.LogError(ex, "Error loading backups."); }
        }

        private async void BaseQuickSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUpdatingSources && sender is ComboBox comboBox && comboBox.SelectedItem is BackupModel backup)
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
            if (!_isUpdatingSources && sender is ComboBox comboBox && comboBox.SelectedItem is BackupModel backup)
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
                ViewModel.DirectorySyncSuffix = GetRelativeSubDirectory(newPath);
                SetPathWithSync(false, newPath);
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
            catch (Exception ex) { LogService.LogError(ex, "Comparison error."); CustomMessageBoxService.ShowError("Error", "Comparison error:\n" + ex.Message, Window.GetWindow(this)); }
            finally { ViewModel.IsComparing = false; }
        }
    }
}
