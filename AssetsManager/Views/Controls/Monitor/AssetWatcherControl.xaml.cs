using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Views.Dialogs;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Views.Controls.Monitor
{
    public partial class AssetWatcherControl : UserControl
    {
        // Public properties for dependency injection
        public MonitorService MonitorService { get; set; }
        public IServiceProvider ServiceProvider { get; set; }
        public DiffViewService DiffViewService { get; set; }
        public AssetWatcherService AssetWatcherService { get; set; }
        public AppSettings AppSettings { get; set; }
        public LogService LogService { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }

        // The state model for this view (Container Pattern: Owner)
        private readonly AssetWatcherModel _viewModel;
        public AssetWatcherModel ViewModel => _viewModel;

        public AssetWatcherControl()
        {
            InitializeComponent();
            
            _viewModel = new AssetWatcherModel();
            DataContext = _viewModel;

            this.Loaded += AssetWatcherControl_Loaded;
            this.Unloaded += AssetWatcherControl_Unloaded;
        }

        private void AssetWatcherControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (AppSettings != null)
            {
                // Defensive pattern to avoid duplicate subscriptions on reload
                AppSettings.ConfigurationSaved -= OnConfigurationSaved;
                AppSettings.ConfigurationSaved += OnConfigurationSaved;
            }

            RefreshList();
        }

        private void AssetWatcherControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (AppSettings != null)
            {
                AppSettings.ConfigurationSaved -= OnConfigurationSaved;
            }
        }

        private void OnConfigurationSaved(object sender, EventArgs e)
        {
            _ = Dispatcher.InvokeAsync(() => RefreshList());
        }

        private void RefreshList()
        {
            if (MonitorService == null) return;

            string filter = txtSearch.Text.Trim().ToLower();
            var filtered = MonitorService.MonitoredAssets
                .Where(u => string.IsNullOrEmpty(filter) || 
                            u.Alias.ToLower().Contains(filter) || 
                            u.WadName.ToLower().Contains(filter) ||
                            u.AssetPath.ToLower().Contains(filter))
                .ToList();

            ViewModel.Paginator.SetFullList(filtered);
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshList();
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (CustomMessageBoxService.ShowYesNo("Clear All", "Are you sure you want to remove all monitored assets?", Window.GetWindow(this)) == true)
            {
                AppSettings.MonitoredAssets.Clear();
                AppSettings.Save();
                MonitorService.LoadMonitoredAssets();
                RefreshList();
            }
        }

        private async void ViewChanges_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var asset = button?.Tag as MonitoredAsset;

            if (asset != null && asset.HasChanges)
            {
                await DiffViewService.ShowFileDiffAsync(asset.OldFilePath, asset.NewFilePath, Window.GetWindow(this));

                asset.HasChanges = false;
                asset.Status = AssetStatus.UpToDate;
                asset.StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentGreen");
                AppSettings.Save();
            }
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var asset = button?.Tag as MonitoredAsset;

            if (asset != null)
            {
                if (AppSettings.MonitoredAssets.Remove(asset))
                {
                    AppSettings.Save();
                    MonitorService.LoadMonitoredAssets();
                    RefreshList();
                }
            }
        }
    }
}
