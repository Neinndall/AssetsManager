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
        }

        private void AssetWatcherControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (MonitorService == null) return;
            RefreshList();
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

            ViewModel.MonitoredAssets.ReplaceRange(filtered);
            MonitoredAssetsListView.ItemsSource = ViewModel.MonitoredAssets;
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshList();
        }

        private void AddAsset_Click(object sender, RoutedEventArgs e)
        {
            var inputDialog = new InputDialog();
            inputDialog.Initialize("Add Asset Monitor", "Enter the asset path (e.g. Plugins/wadname/path/to/file):", string.Empty);
            inputDialog.Owner = Window.GetWindow(this);

            if (inputDialog.ShowDialog() == true)
            {
                string path = inputDialog.InputText;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    TryAddAsset(path);
                }
            }
        }

        private void TryAddAsset(string fullPath)
        {
            try
            {
                var parts = fullPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                {
                    CustomMessageBoxService.ShowError("Error", "Invalid path format. Expected: Source/WadPath/InternalPath", Window.GetWindow(this));
                    return;
                }

                AssetSourceType sourceType = parts[0].Equals("Plugins", StringComparison.OrdinalIgnoreCase) 
                    ? AssetSourceType.Plugins 
                    : AssetSourceType.Game;

                int wadIndex = -1;
                for (int i = 1; i < parts.Length; i++)
                {
                    if (parts[i].EndsWith(".wad", StringComparison.OrdinalIgnoreCase) || 
                        parts[i].EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
                    {
                        wadIndex = i;
                        break;
                    }
                }

                if (wadIndex == -1)
                {
                    CustomMessageBoxService.ShowError("Error", "Could not identify the .wad container in the path.", Window.GetWindow(this));
                    return;
                }

                string wadName = string.Join("/", parts.Skip(1).Take(wadIndex));
                string internalPath = string.Join("/", parts.Skip(wadIndex + 1));
                string alias = parts.Last();

                if (AppSettings.MonitoredAssets.Any(a => a.AssetPath == fullPath))
                {
                    CustomMessageBoxService.ShowWarning("Warning", "This asset is already being monitored.", Window.GetWindow(this));
                    return;
                }

                var newAsset = new MonitoredAsset
                {
                    Alias = alias,
                    AssetPath = fullPath,
                    WadName = parts[wadIndex], 
                    InternalPath = internalPath,
                    SourceType = sourceType,
                    Status = AssetStatus.Pending,
                    StatusColor = (SolidColorBrush)Application.Current.FindResource("TextMuted"),
                    LastKnownHash = 0 
                };

                AppSettings.MonitoredAssets.Add(newAsset);
                AppSettings.Save();
                MonitorService.LoadMonitoredAssets();
                RefreshList();
                
                LogService.LogSuccess($"Added asset to monitor: {alias}");
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Failed to add asset monitor.");
            }
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
                string ext = Path.GetExtension(asset.Alias).ToLowerInvariant();
                
                if (SupportedFileTypes.IsImage(ext))
                {
                    try 
                    {
                        BitmapSource oldImg = null;
                        if (File.Exists(asset.OldFilePath) && new FileInfo(asset.OldFilePath).Length > 0)
                        {
                            using var fs = File.OpenRead(asset.OldFilePath);
                            oldImg = TextureUtils.LoadTexture(fs, ext);
                        }

                        using var fsNew = File.OpenRead(asset.NewFilePath);
                        BitmapSource newImg = TextureUtils.LoadTexture(fsNew, ext);

                        var imgDiffWin = new ImageDiffWindow(oldImg, newImg, "Previous Version", "Current Version");
                        imgDiffWin.Owner = Window.GetWindow(this);
                        imgDiffWin.ShowDialog();
                    }
                    catch (Exception ex)
                    {
                        LogService.LogError(ex, "Error loading images for comparison");
                        CustomMessageBoxService.ShowError("Error", "Could not load images for comparison.", Window.GetWindow(this));
                    }
                }
                else
                {
                    await DiffViewService.ShowFileDiffAsync(asset.OldFilePath, asset.NewFilePath, Window.GetWindow(this));
                }

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
