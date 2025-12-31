using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Dialogs;
using System.Collections.Generic;

namespace AssetsManager.Views.Controls.Monitor
{
    public partial class HistoryViewControl : UserControl
    {
        public AppSettings AppSettings { get; set; }
        public LogService LogService { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public DiffViewService DiffViewService { get; set; }

        private HistoryModel _viewModel;

        public HistoryViewControl()
        {
            InitializeComponent();
            this.Loaded += HistoryViewControl_Loaded;
        }

        private void HistoryViewControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (AppSettings != null)
            {
                _viewModel = new HistoryModel();
                this.DataContext = _viewModel;
                _viewModel.LoadHistory(AppSettings.DiffHistory);
            }
        }

        private async void btnViewDiff_Click(object sender, RoutedEventArgs e)
        {
            var selectedEntry = (sender as FrameworkElement)?.DataContext as JsonDiffHistoryEntry ?? DiffHistoryListView.SelectedItem as JsonDiffHistoryEntry;

            if (selectedEntry != null)
            {
                await DiffViewService.ShowFileDiffAsync(selectedEntry.OldFilePath, selectedEntry.NewFilePath, Window.GetWindow(this));
            }
            else
            {
                CustomMessageBoxService.ShowWarning("Warning", "Please select a history entry to view.", Window.GetWindow(this));
            }
        }

        private void btnRemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            var entryToRemove = (sender as FrameworkElement)?.DataContext as JsonDiffHistoryEntry;
            var itemsToRemove = entryToRemove != null 
                ? new List<JsonDiffHistoryEntry> { entryToRemove }
                : DiffHistoryListView.SelectedItems.Cast<JsonDiffHistoryEntry>().ToList();

            if (itemsToRemove.Count > 0)
            {
                string message = itemsToRemove.Count == 1
                    ? $"Are you sure you want to delete the history entry for '{itemsToRemove.First().FileName}' from {itemsToRemove.First().Timestamp}? This will delete the backup files and cannot be undone."
                    : $"Are you sure you want to delete the {itemsToRemove.Count} selected history entries? This will delete their backup files and cannot be undone.";

                if (CustomMessageBoxService.ShowYesNo("Info", message, Window.GetWindow(this)) == true)
                {
                    try
                    {
                        foreach (var selectedEntry in itemsToRemove)
                        {
                            string historyDirectoryPath = Path.GetDirectoryName(selectedEntry.OldFilePath);

                            if (!string.IsNullOrEmpty(historyDirectoryPath) && Directory.Exists(historyDirectoryPath))
                            {
                                Directory.Delete(historyDirectoryPath, true);
                            }

                            AppSettings.DiffHistory.Remove(selectedEntry);
                        }

                        AppSettings.SaveSettings(AppSettings);
                        _viewModel.LoadHistory(AppSettings.DiffHistory);
                    }
                    catch (Exception ex)
                    {
                        LogService.LogError(ex, "Error deleting history entries.");
                        CustomMessageBoxService.ShowInfo("Error", "Could not delete one or more history entries. Please check the logs for details.", Window.GetWindow(this), CustomMessageBoxIcon.Error);
                    }
                }
            }
            else
            {
                CustomMessageBoxService.ShowInfo("Info", "Please select one or more history entries to delete.", Window.GetWindow(this), CustomMessageBoxIcon.Warning);
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