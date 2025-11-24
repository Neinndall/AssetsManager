using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Views.Models.Shared;

namespace AssetsManager.Views.Controls.Monitor
{
    public partial class BackupsControl : UserControl, INotifyPropertyChanged
    {
        public BackupManager BackupManager { get; set; }
        public LogService LogService { get; set; }
        public AppSettings AppSettings { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<BackupModel> AllBackups { get; private set; }
        public PaginationModel<BackupModel> BackupsPaginator { get; }

        public BackupsControl()
        {
            InitializeComponent();
            AllBackups = new ObservableCollection<BackupModel>();
            BackupsPaginator = new PaginationModel<BackupModel>();
            this.DataContext = this;
            this.Loaded += BackupsControl_Loaded;
            this.Unloaded += BackupsControl_Unloaded;
        }

        private async void BackupsControl_Loaded(object sender, RoutedEventArgs e)
        {
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
            // Unsubscribe from events if any
        }

        private async Task LoadBackupsAsync()
        {
            try
            {
                var backups = await BackupManager.GetBackupsAsync();
                AllBackups.Clear();
                foreach (var backup in backups)
                {
                    AllBackups.Add(backup);
                }
                BackupsPaginator.SetFullList(AllBackups.ToList());
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Error loading backups.");
                throw;
            }
        }

        private int DeleteBackups(IEnumerable<BackupModel> backupsToDelete)
        {
            if (backupsToDelete == null || !backupsToDelete.Any()) return 0;

            var deletedCount = 0;
            foreach (var backup in backupsToDelete.ToList())
            {
                if (BackupManager.DeleteBackup(backup.Path))
                {
                    AllBackups.Remove(backup);
                    deletedCount++;
                }
            }

            if (deletedCount > 0)
            {
                BackupsPaginator.SetFullList(AllBackups.ToList());
            }
            return deletedCount;
        }

        private async void RefreshBackups_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await LoadBackupsAsync();
            }
            catch (Exception ex)
            {
                CustomMessageBoxService.ShowError("Error", $"Error loading backups: {ex.Message}", Window.GetWindow(this));
            }
        }

        private void DeleteSelectedBackups_Click(object sender, RoutedEventArgs e)
        {
            var selectedBackups = AllBackups.Where(b => b.IsSelected).ToList();

            if (selectedBackups == null || !selectedBackups.Any())
            {
                CustomMessageBoxService.ShowWarning("Warning", "No backups selected to delete.", Window.GetWindow(this));
                return;
            }

            var result = CustomMessageBoxService.ShowYesNo("Delete Backups", $"Are you sure you want to delete {selectedBackups.Count} selected backup(s)? This action is irreversible.", Window.GetWindow(this));
            if (result == true)
            {
                var deletedCount = DeleteBackups(selectedBackups);
                if(deletedCount > 0)
                {
                    CustomMessageBoxService.ShowInfo("Success", $"Successfully deleted {deletedCount} backup(s).", Window.GetWindow(this));
                }
                else
                {
                    CustomMessageBoxService.ShowError("Error", "Could not delete the selected backups. Please check the logs for more information.", Window.GetWindow(this));
                }
            }
        }

        private void PrevPage_Click(object sender, RoutedEventArgs e)
        {
            BackupsPaginator.PreviousPage();
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            BackupsPaginator.NextPage();
        }
        
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
