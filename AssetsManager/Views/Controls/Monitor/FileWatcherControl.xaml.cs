using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using AssetsManager.Views.Dialogs;
using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Views.Models;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Collections.Specialized;
using AssetsManager.Utils;
using System.Threading.Tasks;

namespace AssetsManager.Views.Controls.Monitor
{
    public partial class FileWatcherControl : UserControl
    {
        private List<MonitoredUrl> _allMonitoredUrls;
        public MonitorService MonitorService { get; set; }
        public IServiceProvider ServiceProvider { get; set; }
        public DiffViewService DiffViewService { get; set; }
        public JsonDataService JsonDataService { get; set; }
        public AppSettings AppSettings { get; set; }
        public LogService LogService { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }

        public ObservableCollection<MonitoredUrl> MonitoredItems => MonitorService.MonitoredItems;

        public FileWatcherControl()
        {
            InitializeComponent();
            this.Loaded += FileWatcherControl_Loaded;
        }

        private void FileWatcherControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (MonitorService != null)
            {
                _allMonitoredUrls = MonitorService.MonitoredItems.ToList();
                MonitoredItemsListView.ItemsSource = _allMonitoredUrls;
                MonitorService.MonitoredItems.CollectionChanged += MonitoredItems_CollectionChanged;
            }

            txtSearch.ApplyTemplate();
            if (txtSearch.Template.FindName("ClearTextButton", txtSearch) is Button clearButton)
            {
                clearButton.Click += (s, args) =>
                {
                    txtSearch.Text = string.Empty;
                };
            }
        }

        private void MonitoredItems_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            _allMonitoredUrls = MonitorService.MonitoredItems.ToList();
            FilterMonitoredItems(txtSearch.Text);
        }

        private void FilterMonitoredItems(string searchText)
        {
            if (_allMonitoredUrls == null) return;

            var lowerSearchText = searchText.ToLower();
            var filteredItems = _allMonitoredUrls
                .Where(item => item.Alias.ToLower().Contains(lowerSearchText) || item.Url.ToLower().Contains(lowerSearchText))
                .ToList();

            MonitoredItemsListView.ItemsSource = filteredItems;
        }

        private async void AddUrl_Click(object sender, RoutedEventArgs e)
        {
            if (ServiceProvider == null || JsonDataService == null || AppSettings == null || CustomMessageBoxService == null) return;

            var dialog = ServiceProvider.GetRequiredService<InputDialog>();
            dialog.Initialize("Add Urls", "Enter urls (one per line):", "", isMultiLine: true);
            dialog.Owner = Window.GetWindow(this);

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
            {
                await AddMonitoredUrlAsync(dialog.InputText);
            }
        }

        private void ViewChanges_Click(object sender, RoutedEventArgs e)
        {
            if (DiffViewService != null && sender is FrameworkElement element && element.DataContext is MonitoredUrl url)
            {
                _ = DiffViewService.ShowFileDiffAsync(url.OldFilePath, url.NewFilePath, Application.Current.MainWindow);
            }
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is MonitoredUrl urlToRemove && AppSettings != null && CustomMessageBoxService != null)
            {
                if (CustomMessageBoxService.ShowYesNo("Remove URL", $"Are you sure you want to remove '{urlToRemove.Alias}'?") == true)
                {
                    if (urlToRemove == null) return;

                    var item = MonitorService.MonitoredItems.FirstOrDefault(x => x.Url == urlToRemove.Url);
                    if (item != null)
                    {
                        MonitorService.MonitoredItems.Remove(item);
                        AppSettings.MonitoredJsonFiles.Remove(item.Url);
                        AppSettings.JsonDataModificationDates.Remove(item.Url);
                        AppSettings.SaveSettings(AppSettings);
                    }
                }
            }
        }

        public async Task AddMonitoredUrlAsync(string urlsAsText)
        {
            var initialUrls = urlsAsText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                        .Select(url => url.Trim())
                                        .Where(url => !string.IsNullOrWhiteSpace(url))
                                        .ToList();

            if (!initialUrls.Any())
            {
                return;
            }

            var finalUrlsToAdd = new List<string>();
            foreach (var url in initialUrls)
            {
                if (!IsValidUrl(url))
                {
                    CustomMessageBoxService.ShowWarning("Invalid URL", $"The URL '{url}' is not valid and will be ignored.");
                    continue;
                }

                if (url.EndsWith("/"))
                {
                    var fileInfos = await JsonDataService.GetFileUrlsFromDirectoryAsync(url);
                    if (!fileInfos.Any())
                    {
                        CustomMessageBoxService.ShowWarning("Empty Directory", $"Could not find any .json files in '{url}'.");
                    }
                    else
                    {
                        finalUrlsToAdd.AddRange(fileInfos.Select(fi => fi.Url));
                    }
                }
                else
                {
                    finalUrlsToAdd.Add(url);
                }
            }

            int addedCount = 0;
            foreach (var url in finalUrlsToAdd.Distinct())
            {
                if (!AppSettings.MonitoredJsonFiles.Contains(url))
                {
                    AppSettings.MonitoredJsonFiles.Add(url);
                    AppSettings.JsonDataModificationDates[url] = DateTime.MinValue;
                    addedCount++;
                }
            }

            if (addedCount > 0)
            {
                CustomMessageBoxService.ShowInfo("URLs Added", $"Added {addedCount} new URL(s) to be monitored.");
                AppSettings.SaveSettings(AppSettings);
                MonitorService.LoadMonitoredUrls();
            }
            else
            {
                CustomMessageBoxService.ShowInfo("No New URLs", "All specified URL(s) are already being monitored or were invalid.");
            }
        }

        private bool IsValidUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out Uri uriResult)
                && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            FilterMonitoredItems(txtSearch.Text);

            if (txtSearch.Template.FindName("ClearTextButton", txtSearch) is Button clearButton)
            {
                clearButton.Visibility = string.IsNullOrEmpty(txtSearch.Text) ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }
}