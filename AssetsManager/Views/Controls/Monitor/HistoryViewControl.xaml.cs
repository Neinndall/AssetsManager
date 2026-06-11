using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using AssetsManager.Views.Dialogs;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace AssetsManager.Views.Controls.Monitor
{
    public partial class HistoryViewControl : UserControl
    {
        public AppSettings AppSettings { get; set; }
        public LogService LogService { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public DiffViewService DiffViewService { get; set; }
        public ComparisonHistoryService ComparisonHistoryService { get; set; }
        public IServiceProvider ServiceProvider { get; set; }

        private readonly HistoryModel _viewModel;
        public HistoryModel ViewModel => _viewModel;

        public HistoryViewControl()
        {
            InitializeComponent();
            
            _viewModel = new HistoryModel();
            DataContext = _viewModel;

            this.Loaded += HistoryViewControl_Loaded;
            this.Unloaded += HistoryViewControl_Unloaded;
        }

        private void HistoryViewControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (AppSettings != null)
            {
                // Defensive pattern to avoid duplicate subscriptions
                AppSettings.ConfigurationSaved -= OnConfigurationSaved;
                AppSettings.ConfigurationSaved += OnConfigurationSaved;

                RefreshHistory();
            }
        }

        private void HistoryViewControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (AppSettings != null)
            {
                AppSettings.ConfigurationSaved -= OnConfigurationSaved;
            }

            // Clear history from memory when not in use
            if (ViewModel != null)
            {
                ViewModel.ComparisonsPaginator.SetFullList(null);
                ViewModel.WatcherPaginator.SetFullList(null);
                ViewModel.DifferencesPaginator.SetFullList(null);
            }
        }

        private void OnConfigurationSaved(object sender, EventArgs e)
        {
            _ = Dispatcher.InvokeAsync(() => RefreshHistory());
        }

        private void RefreshHistory()
        {
            if (AppSettings != null)
            {
                string filter = txtSearch.Text.Trim().ToLower();
                var entries = AppSettings.DiffHistory
                    .Where(e => string.IsNullOrEmpty(filter) || 
                                (e.FileName != null && e.FileName.ToLower().Contains(filter)) ||
                                (e.DisplayName != null && e.DisplayName.ToLower().Contains(filter)) ||
                                (e.Version != null && e.Version.ToLower().Contains(filter)) ||
                                (e.ReferenceId != null && e.ReferenceId.ToLower().Contains(filter)))
                    .OrderByDescending(e => e.Timestamp);

                _viewModel.LoadHistory(entries);
            }
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshHistory();
        }

        private async void btnSyncBackups_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int recoveredCount = await ComparisonHistoryService.SyncOrphanedArchivesAsync();
                
                if (recoveredCount > 0)
                {
                    LogService.LogSuccess($"Successfully synchronized {recoveredCount} orphaned history entry(s).");
                }
                else
                {
                    LogService.Log("No orphaned history entries found to synchronize.");
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Error synchronizing history.");
                CustomMessageBoxService.ShowError("Error", "Could not synchronize history.", Window.GetWindow(this));
            }
        }

        private void btnEditName_Click(object sender, RoutedEventArgs e)
        {
            var selectedEntry = (sender as FrameworkElement)?.DataContext as HistoryEntry;

            if (selectedEntry != null)
            {
                var inputDialog = new InputDialog();
                inputDialog.Initialize("Rename Entry", "Enter a new name for this entry:", selectedEntry.FileName);
                inputDialog.Owner = Window.GetWindow(this);

                if (inputDialog.ShowDialog() == true)
                {
                    string newName = inputDialog.InputText;
                    if (!string.IsNullOrWhiteSpace(newName) && newName != selectedEntry.FileName)
                    {
                        selectedEntry.FileName = newName;
                        AppSettings.Save();
                        LogService.LogSuccess("Renamed successfully.");
                    }
                }
            }
        }

        private async void btnViewDiff_Click(object sender, RoutedEventArgs e)
        {
            // Resolve entry from button's DataContext
            var selectedEntry = (sender as FrameworkElement)?.DataContext as HistoryEntry;

            if (selectedEntry != null)
            {
                if (selectedEntry.Type == HistoryEntryType.WadArchive || selectedEntry.Type == HistoryEntryType.WadFile)
                {
                    var (data, path) = await ComparisonHistoryService.LoadComparisonAsync(selectedEntry.ReferenceId);
                    if (data != null)
                    {
                        var resultWindow = ServiceProvider.GetRequiredService<WadComparisonResultWindow>();
                        resultWindow.Initialize(data.Diffs, data.OldLolPath, data.NewLolPath, path);
                        resultWindow.Owner = Window.GetWindow(this);
                        resultWindow.Show();
                    }
                    else
                    {
                        CustomMessageBoxService.ShowWarning("Backup Missing", $"The backup for '{selectedEntry.FileName}' was not found. It may have been deleted or moved manually.", Window.GetWindow(this));
                    }
                }
                else
                {
                    await DiffViewService.ShowFileDiffAsync(selectedEntry.OldFilePath, selectedEntry.NewFilePath, Window.GetWindow(this));
                }
            }
            else
            {
                CustomMessageBoxService.ShowWarning("Warning", "Please select a history entry to view.", Window.GetWindow(this));
            }
        }

        private void btnRemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            // In segmented mode, we use the specific entry from the button that was clicked
            var entryToRemove = (sender as FrameworkElement)?.DataContext as HistoryEntry;

            if (entryToRemove != null)
            {
                string message = $"Are you sure you want to delete the history entry for '{entryToRemove.FileName}' from {entryToRemove.Timestamp}? This will delete the backup files and cannot be undone.";

                if (CustomMessageBoxService.ShowYesNo("Remove Backup", message, Window.GetWindow(this)) == true)
                {
                    try
                    {
                        if (entryToRemove.Type == HistoryEntryType.WadArchive || entryToRemove.Type == HistoryEntryType.WadFile)
                        {
                            ComparisonHistoryService.DeleteComparison(entryToRemove);
                        }
                        else
                        {
                            // Manual cleanup for legacy FileDiffs (like Watcher updates or others)
                            string historyDirectoryPath = Path.GetDirectoryName(entryToRemove.OldFilePath);
                            if (!string.IsNullOrEmpty(historyDirectoryPath) && Directory.Exists(historyDirectoryPath))
                            {
                                Directory.Delete(historyDirectoryPath, true);
                            }
                            AppSettings.DiffHistory.Remove(entryToRemove);
                            AppSettings.Save();
                        }
                        
                        RefreshHistory();
                    }
                    catch (Exception ex)
                    {
                        LogService.LogError(ex, "Error deleting history entry.");
                        CustomMessageBoxService.ShowError("Error", "Could not delete the history entry.", Window.GetWindow(this));
                    }
                }
            }
            else
            {
                CustomMessageBoxService.ShowInfo("Information", "Please click the delete button on the entry you want to remove.", Window.GetWindow(this), CustomMessageBoxIcon.Warning);
            }
        }
    }
}
