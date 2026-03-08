using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Views.Dialogs;

namespace AssetsManager.Views.Controls.Monitor
{
    public partial class FileWatcherControl : UserControl
    {
        // Public properties for dependency injection
        public MonitorService MonitorService { get; set; }
        public IServiceProvider ServiceProvider { get; set; }
        public DiffViewService DiffViewService { get; set; }
        public JsonDataService JsonDataService { get; set; }
        public AppSettings AppSettings { get; set; }
        public LogService LogService { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }

        // The state model for this view (Container Pattern: Owner)
        private readonly FileWatcherModel _viewModel;
        public FileWatcherModel ViewModel => _viewModel;

        public FileWatcherControl()
        {
            InitializeComponent();
            
            _viewModel = new FileWatcherModel();
            DataContext = _viewModel;

            this.Loaded += FileWatcherControl_Loaded;
        }

        private void FileWatcherControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (MonitorService == null) return;
            RefreshList();
        }

        private void RefreshList()
        {
            if (MonitorService == null) return;

            string filter = txtSearch.Text.Trim().ToLower();
            var filtered = MonitorService.MonitoredItems
                .Where(u => string.IsNullOrEmpty(filter) || 
                            u.Alias.ToLower().Contains(filter) || 
                            u.Url.ToLower().Contains(filter))
                .ToList();

            ViewModel.MonitoredUrls.ReplaceRange(filtered);
            MonitoredItemsListView.ItemsSource = ViewModel.MonitoredUrls;
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshList();
        }

        private void AddUrl_Click(object sender, RoutedEventArgs e)
        {
            var inputDialog = new InputDialog();
            inputDialog.Initialize("Add Monitor", "Enter the URL to monitor:", string.Empty);
            inputDialog.Owner = Window.GetWindow(this);

            if (inputDialog.ShowDialog() == true)
            {
                string url = inputDialog.InputText;
                if (!string.IsNullOrWhiteSpace(url) && Uri.IsWellFormedUriString(url, UriKind.Absolute))
                {
                    if (!AppSettings.MonitoredJsonFiles.Contains(url))
                    {
                        AppSettings.MonitoredJsonFiles.Add(url);
                        AppSettings.Save();
                        MonitorService.LoadMonitoredUrls();
                        RefreshList();
                    }
                }
                else
                {
                    CustomMessageBoxService.ShowError("Error", "Invalid URL format.", Window.GetWindow(this));
                }
            }
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (CustomMessageBoxService.ShowYesNo("Clear All", "Are you sure you want to remove all monitored URLs?", Window.GetWindow(this)) == true)
            {
                AppSettings.MonitoredJsonFiles.Clear();
                AppSettings.Save();
                MonitorService.LoadMonitoredUrls();
                RefreshList();
            }
        }

        private async void RefreshAll_Click(object sender, RoutedEventArgs e)
        {
            if (JsonDataService == null) return;

            ViewModel.IsBusy = true;
            try
            {
                await JsonDataService.CheckJsonDataUpdatesAsync();
                LogService.LogSuccess("All monitored URLs have been refreshed.");
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Failed to refresh URLs.");
            }
            finally
            {
                ViewModel.IsBusy = false;
            }
        }

        private async void ViewChanges_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var monitoredUrl = button?.Tag as MonitoredUrl;

            if (monitoredUrl != null && monitoredUrl.HasChanges)
            {
                await DiffViewService.ShowFileDiffAsync(monitoredUrl.OldFilePath, monitoredUrl.NewFilePath, Window.GetWindow(this));
            }
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var monitoredUrl = button?.Tag as MonitoredUrl;

            if (monitoredUrl != null)
            {
                if (AppSettings.MonitoredJsonFiles.Remove(monitoredUrl.Url))
                {
                    AppSettings.Save();
                    MonitorService.LoadMonitoredUrls();
                    RefreshList();
                }
            }
        }
    }
}
