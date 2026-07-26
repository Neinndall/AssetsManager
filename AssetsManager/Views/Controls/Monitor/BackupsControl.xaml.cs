using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Dialogs;

namespace AssetsManager.Views.Controls.Monitor
{
    public partial class BackupsControl : UserControl
    {
        // Public properties for dependency injection from the container
        public BackupManager BackupManager { get; set; }
        public VersionService VersionService { get; set; }
        public LogService LogService { get; set; }
        public AppSettings AppSettings { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public TaskCancellationManager TaskCancellationManager { get; set; }
        public IServiceProvider ServiceProvider { get; set; }

        // The state model for this view (Container Pattern: Owner)
        private readonly BackupsControlModel _viewModel;
        private readonly List<BackupModel> _loadedBackups = new();
        private CancellationTokenSource _loadCancellation;
        public BackupsControlModel ViewModel => _viewModel;

        public BackupsControl()
        {
            InitializeComponent();
            
            _viewModel = new BackupsControlModel();
            DataContext = _viewModel;

            this.Loaded += BackupsControl_Loaded;
            this.Unloaded += BackupsControl_Unloaded;
        }

        private async void BackupsControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (AppSettings != null)
            {
                // Defensive pattern to avoid duplicate subscriptions on reload
                AppSettings.ConfigurationSaved -= OnConfigurationSaved;
                AppSettings.ConfigurationSaved += OnConfigurationSaved;
                ApplyPreferredClientFilter();
            }
            try
            {
                await LoadBackupsAsync();
            }
            catch (Exception ex)
            {
                CustomMessageBoxService.ShowError("Error", $"Error loading backups: {ex.Message}", Window.GetWindow(this));
            }
        }

        private void BackupsControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (AppSettings != null)
            {
                AppSettings.ConfigurationSaved -= OnConfigurationSaved;
            }
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            _loadCancellation = null;

            // Clear heavy data from memory when not in use
            if (ViewModel != null)
            {
                ViewModel.AllBackups.Clear();
            }
            _loadedBackups.Clear();
        }

        private async void OnConfigurationSaved(object sender, EventArgs e)
        {
            Task loadTask = await Dispatcher.InvokeAsync(() =>
            {
                ApplyPreferredClientFilter();
                return LoadBackupsAsync();
            });
            await loadTask;
        }

        private async Task LoadBackupsAsync()
        {
            if (BackupManager == null) return;

            try
            {
                _loadCancellation?.Cancel();
                _loadCancellation?.Dispose();
                _loadCancellation = new CancellationTokenSource();
                var backups = await BackupManager.GetBackupsAsync(_loadCancellation.Token);

                _loadedBackups.Clear();
                _loadedBackups.AddRange(backups);
                ApplyFilterAndSort();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Error loading backups.");
            }
        }

        private async void DeleteSelectedBackups_Click(object sender, RoutedEventArgs e)
        {
            var selectedBackups = ViewModel.AllBackups.Where(b => b.IsSelected && b.CanModify).ToList();

            if (!selectedBackups.Any())
            {
                CustomMessageBoxService.ShowWarning("Protected Selection", "Select one or more BACKUP snapshots. MAIN installations cannot be deleted.", Window.GetWindow(this));
                return;
            }

            var result = CustomMessageBoxService.ShowYesNo("Delete Backup", $"Are you sure you want to delete the selected backup? This action is irreversible.", Window.GetWindow(this));
            if (result == true)
            {
                int deletedCount = 0;
                foreach (var backup in selectedBackups)
                {
                    if (BackupManager.DeleteBackup(backup.Path))
                    {
                        ViewModel.AllBackups.Remove(backup);
                        deletedCount++;
                    }
                }

                if(deletedCount > 0)
                {
                    await LoadBackupsAsync();
                    CustomMessageBoxService.ShowInfo("Success", $"Successfully deleted {deletedCount} backup(s).", Window.GetWindow(this));
                }
                else
                {
                    CustomMessageBoxService.ShowError("Error", "Could not delete the selected backups.", Window.GetWindow(this));
                }
            }
        }

        private void FilterOrSort_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsLoaded) ApplyFilterAndSort();
        }

        private void ApplyFilterAndSort()
        {
            string filter = (EnvironmentFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ALL";
            string sort = (SortSelector?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "NEWEST";
            IEnumerable<BackupModel> query = _loadedBackups;
            if (filter == "PBE") query = query.Where(backup => backup.IsPbe);
            if (filter == "LIVE") query = query.Where(backup => !backup.IsPbe);
            query = sort switch
            {
                "OLDEST" => query.OrderBy(backup => backup.CreationDate),
                "LARGEST" => query.OrderByDescending(backup => backup.Size),
                "SMALLEST" => query.OrderBy(backup => backup.Size),
                _ => query.OrderByDescending(backup => backup.CreationDate)
            };
            List<BackupModel> filteredBackups = query.ToList();
            ViewModel.AllBackups.Clear();
            foreach (BackupModel backup in filteredBackups) ViewModel.AllBackups.Add(backup);

            List<BackupModel> snapshots = filteredBackups.Where(backup => !backup.IsMainClient).ToList();
            ViewModel.TotalBackupsCount = snapshots.Count;
            ViewModel.TotalStorageSize = FormatUtils.FormatSize(snapshots.Sum(backup => backup.Size));
            ViewModel.ActiveClientEnvironment = filter;
        }

        private void ApplyPreferredClientFilter()
        {
            string preferred = AppSettings.PreferredClient == PreferredClient.PBE ? "PBE" : "LIVE";
            EnvironmentFilter.SelectedItem = EnvironmentFilter.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), preferred, StringComparison.Ordinal));
        }

        private async void createLolBackupButton_Click(object sender, RoutedEventArgs e)
        {
            BackupModel selectedBackup = ViewModel.AllBackups.FirstOrDefault(backup => backup.IsSelected);
            if (selectedBackup == null || selectedBackup.IsMainClient)
            {
                await RunBackupOperationAsync(selectedBackup, BackupAction.None);
                return;
            }

            var actionDialog = ServiceProvider.GetRequiredService<BackupActionDialog>();
            actionDialog.Owner = Window.GetWindow(this);
            if (actionDialog.ShowDialog() == true)
                await RunBackupOperationAsync(selectedBackup, actionDialog.SelectedAction);
        }

        private async Task RunBackupOperationAsync(BackupModel selectedBackup, BackupAction action)
        {
            if (ViewModel.IsBusy) return;
            string sourcePath;
            string destinationPath;
            string oldBackupPathToDelete = null;
            string clientName;
            bool isCloning = false;

            if (action == BackupAction.Overwrite && selectedBackup?.CanModify == true)
            {
                sourcePath = selectedBackup.IsPbe ? AppSettings.LolPbeDirectory : AppSettings.LolLiveDirectory;
                oldBackupPathToDelete = selectedBackup.Path;
                clientName = selectedBackup.IsPbe ? "PBE" : "LIVE";
                destinationPath = CreateUniqueDestinationPath(GetBackupBasePath(selectedBackup.Path));
            }
            else if (action == BackupAction.Clone && selectedBackup?.CanModify == true)
            {
                sourcePath = selectedBackup.Path;
                isCloning = true;
                clientName = selectedBackup.IsPbe ? "PBE" : "LIVE";
                destinationPath = CreateUniqueDestinationPath(GetBackupBasePath(selectedBackup.Path));
            }
            else
            {
                bool usePbe = selectedBackup?.IsMainClient == true
                    ? selectedBackup.IsPbe
                    : AppSettings.PreferredClient != PreferredClient.LIVE;
                sourcePath = usePbe ? AppSettings.LolPbeDirectory : AppSettings.LolLiveDirectory;
                clientName = usePbe ? "PBE" : "LIVE";
                destinationPath = CreateUniqueDestinationPath(
                    sourcePath?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            if (string.IsNullOrEmpty(sourcePath))
            {
                CustomMessageBoxService.ShowWarning("Warning", $"Source directory ({clientName}) is not configured. Please set it in Settings > Default Paths.", Window.GetWindow(this));
                return;
            }

            if (!System.IO.Directory.Exists(sourcePath))
            {
                CustomMessageBoxService.ShowError("Error", $"The source directory does not exist: {sourcePath}", Window.GetWindow(this));
                return;
            }

            var inputDialog = ServiceProvider.GetRequiredService<InputDialog>();
            inputDialog.Initialize(
                "Backup Name",
                "Enter an optional descriptive name for this snapshot:",
                selectedBackup?.CanModify == true ? selectedBackup.DisplayName : $"{clientName} Snapshot");
            inputDialog.Owner = Window.GetWindow(this);
            if (inputDialog.ShowDialog() != true) return;
            string displayName = inputDialog.InputText?.Trim();
            if (displayName?.Length > 80) displayName = displayName[..80];

            ViewModel.IsBusy = true;
            try
            {
                var cancellationToken = TaskCancellationManager.PrepareNewOperation();
                BackupManager.BackupStorageEstimate estimate =
                    await BackupManager.GetStorageEstimateAsync(sourcePath, destinationPath, cancellationToken);
                string operation = action == BackupAction.Overwrite ? "Refresh" : isCloning ? "Clone" : "Create";
                string snapshotLabel = string.IsNullOrWhiteSpace(displayName)
                    ? $"{clientName} snapshot"
                    : $"'{displayName}'";
                string spaceMessage =
                    $"{operation} {snapshotLabel}?\n\n" +
                    $"Required: {FormatUtils.FormatSize(estimate.TotalBytes)} ({estimate.FileCount:N0} files)\n" +
                    $"Available: {FormatUtils.FormatSize(estimate.AvailableBytes)}";
                if (estimate.TotalBytes > estimate.AvailableBytes)
                {
                    CustomMessageBoxService.ShowError("Insufficient Storage", spaceMessage, Window.GetWindow(this));
                    return;
                }
                if (CustomMessageBoxService.ShowYesNo("Backup Storage", spaceMessage, Window.GetWindow(this)) != true)
                    return;

                if (isCloning)
                {
                    await BackupManager.CloneBackupAsync(sourcePath, destinationPath, cancellationToken, displayName);
                }
                else
                {
                    string logMsg = !string.IsNullOrEmpty(oldBackupPathToDelete) ? "Overwriting backup..." : "Creating backup...";
                    await BackupManager.CreateLolPbeDirectoryBackupAsync(sourcePath, destinationPath, cancellationToken, logMsg, displayName);
                }
                
                if (!cancellationToken.IsCancellationRequested)
                {
                    // SAFE-REFRESH: Delete old backup only after success
                    if (!string.IsNullOrEmpty(oldBackupPathToDelete) && destinationPath != oldBackupPathToDelete)
                    {
                        BackupManager.DeleteBackup(oldBackupPathToDelete, false);
                    }

                    LogService.LogSuccess("Backup completed successfully.");
                    CustomMessageBoxService.ShowInfo("Backup", $"Operation completed successfully as:\n{System.IO.Path.GetFileName(destinationPath)}", Window.GetWindow(this));
                }
                await LoadBackupsAsync();
            }
            catch (OperationCanceledException)
            {
                LogService.LogWarning("Backup operation was cancelled.");
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Error in backup operation");
                CustomMessageBoxService.ShowError("Error", $"An unexpected error occurred: {ex.Message}", Window.GetWindow(this));
            }
            finally
            {
                ViewModel.IsBusy = false;
            }
        }

        private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not BackupModel backup || !Directory.Exists(backup.Path)) return;
            Process.Start(new ProcessStartInfo { FileName = backup.Path, UseShellExecute = true });
        }

        private static string GetBackupBasePath(string path)
        {
            string basePath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            int marker = basePath.LastIndexOf("_old_", StringComparison.OrdinalIgnoreCase);
            return marker > 0 ? basePath[..marker] : basePath;
        }

        private static string CreateUniqueDestinationPath(string basePath)
        {
            string candidate = $"{basePath}_old_{DateTime.Now:yyyyMMdd_HHmmss}";
            string unique = candidate;
            for (int suffix = 2; Directory.Exists(unique); suffix++)
                unique = $"{candidate}_{suffix}";
            return unique;
        }

        private void ListViewItem_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            DependencyObject current = e.OriginalSource as DependencyObject;
            while (current != null && !ReferenceEquals(current, sender))
            {
                if (current is Button) return;
                current = VisualTreeHelper.GetParent(current);
            }
            if (sender is ListViewItem item && item.IsSelected)
            {
                item.IsSelected = false;
                e.Handled = true;
            }
        }
    }
}
