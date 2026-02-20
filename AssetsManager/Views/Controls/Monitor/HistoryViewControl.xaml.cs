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

        private HistoryModel _viewModel;

        public HistoryViewControl()
        {
            InitializeComponent();
            this.Loaded += HistoryViewControl_Loaded;
            this.Unloaded += HistoryViewControl_Unloaded;
        }

        private void HistoryViewControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (AppSettings != null)
            {
                AppSettings.ConfigurationSaved += OnConfigurationSaved;
                
                _viewModel = new HistoryModel();
                this.DataContext = _viewModel;
                _viewModel.LoadHistory(AppSettings.DiffHistory);
            }
        }

        private void HistoryViewControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (AppSettings != null)
            {
                AppSettings.ConfigurationSaved -= OnConfigurationSaved;
            }
        }

        private void OnConfigurationSaved(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (_viewModel != null && AppSettings != null)
                {
                    _viewModel.LoadHistory(AppSettings.DiffHistory);
                }
            });
        }

        private void btnEditName_Click(object sender, RoutedEventArgs e)
        {
            var selectedEntry = (sender as FrameworkElement)?.DataContext as HistoryEntry;

            if (selectedEntry != null)
            {
                var inputDialog = new InputDialog();
                inputDialog.Initialize("Rename Comparison", "Enter a new name for this comparison:", selectedEntry.FileName);
                inputDialog.Owner = Window.GetWindow(this);

                if (inputDialog.ShowDialog() == true)
                {
                    string newName = inputDialog.InputText;
                    if (!string.IsNullOrWhiteSpace(newName) && newName != selectedEntry.FileName)
                    {
                        selectedEntry.FileName = newName;
                        AppSettings.Save();
                        LogService.LogSuccess($"Renamed: {newName}");
                    }
                }
            }
        }

        private async void btnViewDiff_Click(object sender, RoutedEventArgs e)
        {
            var selectedEntry = (sender as FrameworkElement)?.DataContext as HistoryEntry ?? DiffHistoryListView.SelectedItem as HistoryEntry;

            if (selectedEntry != null)
            {
                if (selectedEntry.Type == HistoryEntryType.WadComparison)
                {
                    var (data, path) = await ComparisonHistoryService.LoadComparisonAsync(selectedEntry.ReferenceId);
                    if (data != null)
                    {
                        var resultWindow = ServiceProvider.GetRequiredService<WadComparisonResultWindow>();
                        resultWindow.Initialize(data.Diffs, data.OldLolPath, data.NewLolPath, path, selectedEntry.ReferenceId);
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
            var entryToRemove = (sender as FrameworkElement)?.DataContext as HistoryEntry;
            var itemsToRemove = entryToRemove != null 
                ? new List<HistoryEntry> { entryToRemove }
                : DiffHistoryListView.SelectedItems.Cast<HistoryEntry>().ToList();

            if (itemsToRemove.Count > 0)
            {
                string message = itemsToRemove.Count == 1
                    ? $"Are you sure you want to delete the history entry for '{itemsToRemove.First().FileName}' from {itemsToRemove.First().Timestamp}? This will delete the backup files and cannot be undone."
                    : $"Are you sure you want to delete the {itemsToRemove.Count} selected history entries? This will delete their backup files and cannot be undone.";

                if (CustomMessageBoxService.ShowYesNo("Remove Backup", message, Window.GetWindow(this)) == true)
                {
                    try
                    {
                        foreach (var selectedEntry in itemsToRemove)
                        {
                            if (selectedEntry.Type == HistoryEntryType.WadComparison)
                            {
                                ComparisonHistoryService.DeleteComparison(selectedEntry);
                            }
                            else
                            {
                                // Manual cleanup for legacy FileDiffs
                                string historyDirectoryPath = Path.GetDirectoryName(selectedEntry.OldFilePath);
                                if (!string.IsNullOrEmpty(historyDirectoryPath) && Directory.Exists(historyDirectoryPath))
                                {
                                    Directory.Delete(historyDirectoryPath, true);
                                }
                                AppSettings.DiffHistory.Remove(selectedEntry);
                                AppSettings.Save();
                            }
                        }
                        
                        // UI will refresh automatically via ConfigurationSaved event
                    }
                    catch (Exception ex)
                    {
                        LogService.LogError(ex, "Error deleting history entries.");
                        CustomMessageBoxService.ShowError("Error", "Could not delete one or more history entries. Please check the logs for details.", Window.GetWindow(this));
                    }
                }
            }
            else
            {
                CustomMessageBoxService.ShowInfo("Information", "Please select one or more history entries to delete.", Window.GetWindow(this), CustomMessageBoxIcon.Warning);
            }
        }

        private void PrevPage_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.Paginator.PreviousPage();
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.Paginator.NextPage();
        }
    }
}