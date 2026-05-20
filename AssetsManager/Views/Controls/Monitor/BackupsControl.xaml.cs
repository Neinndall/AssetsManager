using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

            // Clear heavy data from memory when not in use
            if (ViewModel != null)
            {
                ViewModel.AllBackups.Clear();
            }
        }

        private async void OnConfigurationSaved(object sender, EventArgs e)
        {
            await Dispatcher.InvokeAsync(async () => await LoadBackupsAsync());
        }

        private async Task LoadBackupsAsync()
        {
            if (BackupManager == null) return;

            try
            {
                var backups = await BackupManager.GetBackupsAsync();
                
                // Filter actual snapshots for metrics
                var snapshots = backups.Where(b => !b.IsMainClient).ToList();
                
                // Calculate metrics (only for actual backups)
                long totalSizeBytes = snapshots.Sum(b => b.Size);
                int count = snapshots.Count;
                string activeEnv = AppSettings.PreferredClient == PreferredClient.PBE ? "PBE" : "LIVE";

                ViewModel.AllBackups.Clear();
                foreach (var backup in backups)
                {
                    ViewModel.AllBackups.Add(backup);
                }

                // Update Dashboard Properties
                ViewModel.TotalBackupsCount = count;
                ViewModel.TotalStorageSize = FormatUtils.FormatSize(totalSizeBytes);
                ViewModel.ActiveClientEnvironment = activeEnv;
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Error loading backups.");
            }
        }

        private async void DeleteSelectedBackups_Click(object sender, RoutedEventArgs e)
        {
            var selectedBackups = ViewModel.AllBackups.Where(b => b.IsSelected).ToList();

            if (selectedBackups == null || !selectedBackups.Any())
            {
                CustomMessageBoxService.ShowWarning("Warning", "No backups selected to delete.", Window.GetWindow(this));
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

        private async void createLolBackupButton_Click(object sender, RoutedEventArgs e)
        {
            // 1. Determine Source and Destination
            string sourcePath;
            string destinationPath;
            string oldBackupPathToDelete = null;
            var selectedBackup = ViewModel.AllBackups.FirstOrDefault(b => b.IsSelected);
            string clientName;
            bool isCloning = false;

            if (selectedBackup != null)
            {
                // Show choice dialog: Overwrite or Clone?
                var actionDialog = ServiceProvider.GetRequiredService<BackupActionDialog>();
                actionDialog.Owner = Window.GetWindow(this);

                if (actionDialog.ShowDialog() != true) return;

                if (actionDialog.SelectedAction == BackupAction.Overwrite)
                {
                    // OVERWRITE MODE: Use active client as source, NEW timestamped destination
                    bool isPbe = selectedBackup.DisplayName.Contains("PBE");
                    sourcePath = isPbe ? AppSettings.LolPbeDirectory : AppSettings.LolLiveDirectory;
                    oldBackupPathToDelete = selectedBackup.Path;
                    clientName = isPbe ? "PBE" : "LIVE";

                    // Confirm overwrite
                    var confirm = CustomMessageBoxService.ShowYesNo("Overwrite Backup",
                        $"This will update the backup with the current {clientName} data. The old backup folder will be replaced. Are you sure?",
                        Window.GetWindow(this));
                    if (confirm != true) return;

                    // Generate NEW destination path for overwrite (Safe-Refresh)
                    string baseName = oldBackupPathToDelete;
                    if (baseName.Contains("_old_"))
                    {
                        baseName = baseName.Substring(0, baseName.LastIndexOf("_old_"));
                    }
                    string dateSuffix = DateTime.Now.ToString("yyyyMMdd_HHmm");
                    destinationPath = $"{baseName}_old_{dateSuffix}";

                    // Ensure they are different to prevent premature deletion by BackupManager
                    if (destinationPath == oldBackupPathToDelete)
                    {
                        destinationPath += "_updated";
                    }
                }
                else if (actionDialog.SelectedAction == BackupAction.Clone)
                {
                    // CLONE MODE: Use selected backup as source, create new destination
                    sourcePath = selectedBackup.Path;
                    isCloning = true;

                    string baseName = sourcePath;
                    if (baseName.Contains("_old_"))
                    {
                        baseName = baseName.Substring(0, baseName.LastIndexOf("_old_"));
                    }

                    string dateSuffix = DateTime.Now.ToString("yyyyMMdd_HHmm");
                    destinationPath = $"{baseName}_old_{dateSuffix}";
                    clientName = "BACKUP";
                }
                else return;
            }
            else
            {
                // NEW BACKUP MODE
                sourcePath = AppSettings.PreferredClient == PreferredClient.LIVE 
                    ? AppSettings.LolLiveDirectory 
                    : AppSettings.LolPbeDirectory;
                clientName = AppSettings.PreferredClient == PreferredClient.LIVE ? "LIVE" : "PBE";

                string cleanSource = sourcePath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                string baseName = cleanSource;
                if (baseName.EndsWith("_old", StringComparison.OrdinalIgnoreCase))
                {
                    baseName = baseName.Substring(0, baseName.Length - 4);
                }
                
                string dateSuffix = DateTime.Now.ToString("yyyyMMdd_HHmm");
                destinationPath = $"{baseName}_old_{dateSuffix}";
            }

            // 2. Validate Source
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

            ViewModel.IsBusy = true;
            try
            {
                var cancellationToken = TaskCancellationManager.PrepareNewOperation();
                
                if (isCloning)
                {
                    await BackupManager.CloneBackupAsync(sourcePath, destinationPath, cancellationToken);
                }
                else
                {
                    string logMsg = !string.IsNullOrEmpty(oldBackupPathToDelete) ? "Overwriting backup..." : "Creating backup...";
                    await BackupManager.CreateLolPbeDirectoryBackupAsync(sourcePath, destinationPath, cancellationToken, logMsg);
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

        private void ListViewItem_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is ListViewItem item && item.IsSelected)
            {
                item.IsSelected = false;
                e.Handled = true;
            }
        }
    }
}
