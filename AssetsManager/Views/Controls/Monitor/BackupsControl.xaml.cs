using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Views.Models.Settings;

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
                ViewModel.AllBackups.Clear();
                foreach (var backup in backups)
                {
                    ViewModel.AllBackups.Add(backup);
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Error loading backups.");
            }
        }

        private void DeleteSelectedBackups_Click(object sender, RoutedEventArgs e)
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
            string sourceLolPath;
            string destinationBackupPath;
            var selectedBackup = ViewModel.AllBackups.FirstOrDefault(b => b.IsSelected);
            string clientName;

            if (selectedBackup != null)
            {
                // REFRESH MODE: Use active client as source, selected backup as destination
                bool isPbe = selectedBackup.DisplayName.Contains("PBE");
                sourceLolPath = isPbe ? AppSettings.LolPbeDirectory : AppSettings.LolLiveDirectory;
                destinationBackupPath = selectedBackup.Path;
                clientName = isPbe ? "PBE" : "LIVE";

                // Confirm overwrite
                var confirm = CustomMessageBoxService.ShowYesNo("Overwrite Backup", 
                    $"This will overwrite the selected backup with the current {clientName} data. Are you sure?", 
                    Window.GetWindow(this));
                if (confirm != true) return;
            }
            else
            {
                // NEW BACKUP MODE: Use preferred client as source, create timestamped destination
                sourceLolPath = AppSettings.PreferredBackupClient == PreferredClient.LIVE 
                    ? AppSettings.LolLiveDirectory 
                    : AppSettings.LolPbeDirectory;
                clientName = AppSettings.PreferredBackupClient == PreferredClient.LIVE ? "LIVE" : "PBE";

                string cleanSource = sourceLolPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                string baseName = cleanSource;
                if (baseName.EndsWith("_old", StringComparison.OrdinalIgnoreCase))
                {
                    baseName = baseName.Substring(0, baseName.Length - 4);
                }
                
                string dateSuffix = DateTime.Now.ToString("yyyyMMdd_HHmm");
                destinationBackupPath = $"{baseName}_old_{dateSuffix}";
            }

            // 2. Validate Source
            if (string.IsNullOrEmpty(sourceLolPath))
            {
                CustomMessageBoxService.ShowWarning("Warning", $"LoL {clientName} directory is not configured. Please set it in Settings > Default Paths.", Window.GetWindow(this));
                return;
            }

            if (!System.IO.Directory.Exists(sourceLolPath))
            {
                CustomMessageBoxService.ShowError("Error", $"The source directory does not exist: {sourceLolPath}", Window.GetWindow(this));
                return;
            }

            ViewModel.IsBusy = true;
            try
            {
                var cancellationToken = TaskCancellationManager.PrepareNewOperation();
                await BackupManager.CreateLolPbeDirectoryBackupAsync(sourceLolPath, destinationBackupPath, cancellationToken);
                
                if (!cancellationToken.IsCancellationRequested)
                {
                    LogService.LogSuccess($"Backup of {System.IO.Path.GetFileName(sourceLolPath)} completed successfully.");
                    CustomMessageBoxService.ShowInfo("Backup", $"Backup completed successfully as:\n{System.IO.Path.GetFileName(destinationBackupPath)}", Window.GetWindow(this));
                }
                await LoadBackupsAsync();
            }
            catch (OperationCanceledException)
            {
                LogService.LogWarning("LoL backup was cancelled.");
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Error creating LoL backup");
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
