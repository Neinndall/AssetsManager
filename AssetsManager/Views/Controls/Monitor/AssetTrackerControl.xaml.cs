using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using System.Windows;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using AssetsManager.Services.Core;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Views.Controls.Monitor
{
    public partial class AssetTrackerControl : UserControl
    {
        // Public properties for dependency injection from the container
        public MonitorService MonitorService { get; set; }
        public AssetDownloader AssetDownloader { get; set; }
        public LogService LogService { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public AppSettings AppSettings { get; set; }

        // The state model for this view (Container Pattern: Owner)
        private readonly AssetTrackerModel _viewModel;
        private CancellationTokenSource _lifetimeCancellation;
        private CancellationTokenSource _operationCancellation;
        public AssetTrackerModel ViewModel => _viewModel;

        public AssetTrackerControl()
        {
            InitializeComponent();
            
            _viewModel = new AssetTrackerModel();
            DataContext = _viewModel;

            this.Loaded += AssetTrackerControl_Loaded;
            this.Unloaded += AssetTrackerControl_Unloaded;
        }

        private void AssetTrackerControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (MonitorService == null) return;

            _lifetimeCancellation = new CancellationTokenSource();
            MonitorService.Catalog.Changed -= OnCatalogChanged;
            MonitorService.Catalog.Changed += OnCatalogChanged;

            if (AppSettings != null)
            {
                // Defensive pattern to avoid duplicates
                AppSettings.ConfigurationSaved -= OnConfigurationSaved;
                AppSettings.ConfigurationSaved += OnConfigurationSaved;
            }

            // Sync with MonitorService events using defensive pattern
            MonitorService.CategoryCheckStarted -= OnCategoryCheckStarted;
            MonitorService.CategoryCheckStarted += OnCategoryCheckStarted;
            
            MonitorService.CategoryCheckCompleted -= OnCategoryCheckCompleted;
            MonitorService.CategoryCheckCompleted += OnCategoryCheckCompleted;

            LoadTrackerData();
        }

        private void OnConfigurationSaved(object sender, EventArgs e)
        {
            _ = Dispatcher.InvokeAsync(() => { if (IsLoaded) LoadTrackerData(); });
        }

        private void LoadTrackerData()
        {
            string selectedId = (CategoryComboBox.SelectedItem as AssetCategory)?.Id ?? ViewModel.SelectedCategory?.Id;
            MonitorService.LoadAssetCategories();
            ViewModel.Categories.ReplaceRange(MonitorService.AssetCategories);

            CategoryComboBox.ItemsSource = ViewModel.Categories;

            CategoryComboBox.SelectedItem = ViewModel.Categories.FirstOrDefault(category => category.Id == selectedId) ?? ViewModel.Categories.FirstOrDefault();
        }

        private void OnCatalogChanged(object sender, EventArgs e)
        {
            _ = Dispatcher.InvokeAsync(() => { if (IsLoaded) RefreshAssetList(); });
        }

        private async void SyncCatalogButton_Click(object sender, RoutedEventArgs e)
        {
            if (MonitorService == null || ViewModel.IsBusy) return;
            string categoryId = ViewModel.SelectedCategory?.Id;
            await RunOperationAsync(async token => { await MonitorService.Catalog.SyncSourcesAsync(token, categoryId); });
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => _operationCancellation?.Cancel();

        private async Task RunOperationAsync(Func<CancellationToken, Task> action)
        {
            if (ViewModel.IsBusy || _lifetimeCancellation == null) return;
            ViewModel.IsBusy = true;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            _operationCancellation = operation;
            try { await action(operation.Token); }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
                LogService.LogDebug("Asset Tracker operation cancelled.");
            }
            catch (Exception ex) { LogService.LogError(ex, "Asset Tracker operation failed."); }
            finally
            {
                _operationCancellation = null;
                ViewModel.IsBusy = false;
                if (IsLoaded) RefreshAssetList();
            }
        }

        private void CategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Unsubscribe from previous category to avoid memory leaks
            if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is AssetCategory oldCategory)
            {
                oldCategory.PropertyChanged -= OnCategoryPropertyChanged;
            }

            // Update the model state from the ComboBox's selection
            ViewModel.SelectedCategory = CategoryComboBox.SelectedItem as AssetCategory;

            // Subscribe to new category property changes
            if (ViewModel.SelectedCategory != null)
            {
                ViewModel.SelectedCategory.PropertyChanged -= OnCategoryPropertyChanged;
                ViewModel.SelectedCategory.PropertyChanged += OnCategoryPropertyChanged;
            }

            RefreshAssetList();
        }

        private void OnCategoryPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AssetCategory.Start))
            {
                _ = Dispatcher.InvokeAsync(() => RefreshAssetList());
            }
        }

        private void RefreshAssetList()
        {
            if (ViewModel.SelectedCategory == null || MonitorService == null)
            {
                ViewModel.Assets.Clear();
                return;
            }

            var assetsFromService = MonitorService.GetAssetListForCategory(ViewModel.SelectedCategory);
            ViewModel.Assets.ReplaceRange(assetsFromService);
            ViewModel.Paginator.SetFullList(ViewModel.Assets, true);
            AssetsItemsControl.ItemsSource = ViewModel.Paginator.PagedItems;
            RangesText.Text = "Next new IDs: " + string.Join(", ", ViewModel.SelectedCategory.Ranges.Select(range => range.NextId));
            CatalogStatusText.Text = $"{assetsFromService.Count(asset => asset.CanDownload)} images available · {assetsFromService.Count(asset => !asset.CanDownload)} ready to check";
        }

        private async void DownloadButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (ViewModel.IsBusy) return;
            if (AssetDownloader == null)
            {
                CustomMessageBoxService.ShowError("Error", "Download service is not available.", Window.GetWindow(this));
                return;
            }

            var assetsToDownload = ViewModel.Assets.Where(a => a.CanDownload).DistinctBy(a => a.Url).ToList();

            if (!assetsToDownload.Any())
            {
                CustomMessageBoxService.ShowInfo("Info", "No assets to download.", Window.GetWindow(this));
                return;
            }

            var folderBrowserDialog = new OpenFolderDialog
            {
                Title = $"Select folder to save the assets"
            };

            if (folderBrowserDialog.ShowDialog() == true)
            {
                string destinationPath = folderBrowserDialog.FolderName;
                int downloadedCount = 0;
                await RunOperationAsync(async token =>
                {
                    foreach (var asset in assetsToDownload)
                    {
                        string fileName = Path.GetFileName(new Uri(asset.Url).AbsolutePath);
                        string fullDestinationPath = Path.Combine(destinationPath, fileName);
                        await AssetDownloader.DownloadAssetToCustomPathAsync(asset.Url, fullDestinationPath, token);
                        downloadedCount++;
                    }
                    LogService.LogInteractiveSuccess($"Successfully saved {downloadedCount} assets to {Path.GetFileName(destinationPath)}.", destinationPath, Path.GetFileName(destinationPath));
                });
            }
        }

        private void LoadMoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (MonitorService == null || ViewModel.SelectedCategory == null) return;

            ViewModel.IsBusy = true;
            try
            {
                var newAssets = MonitorService.GenerateMoreAssets(ViewModel.Assets, ViewModel.SelectedCategory, 10);
                ViewModel.Assets.AddRange(newAssets);
                ViewModel.Paginator.SetFullList(ViewModel.Assets);
                ViewModel.Paginator.GoToLastPage();
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "An error occurred while loading more assets.");
            }
            finally
            {
                ViewModel.IsBusy = false;
            }
        }

        private async void CheckButton_Click(object sender, RoutedEventArgs e)
        {
            if (MonitorService == null || ViewModel.SelectedCategory == null || ViewModel.IsBusy) return;

            var assetsToCheck = ViewModel.Assets.Where(a => a.State != TrackedAssetState.Available).ToList();
            if (!assetsToCheck.Any())
            {
                var result = CustomMessageBoxService.ShowYesNo("Info", "There are no pending or failed assets to check. Do you want to load more?", Window.GetWindow(this));
                if (result != true) return;

                LoadMoreButton_Click(this, new RoutedEventArgs());
                assetsToCheck = ViewModel.Assets.Where(a => a.State != TrackedAssetState.Available).ToList();
                if (!assetsToCheck.Any()) return;
            }

            var category = ViewModel.SelectedCategory;
            await RunOperationAsync(async token =>
            {
                await MonitorService.CheckAssetsAsync(assetsToCheck, category, token);
                var foundCount = assetsToCheck.Count(a => a.Status == "OK");
                if (IsLoaded) CustomMessageBoxService.ShowInfo("Info", $"Check finished. {foundCount} images are available.", Window.GetWindow(this));
            });
        }

        private void AssetTrackerControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (MonitorService != null)
            {
                MonitorService.Catalog.Changed -= OnCatalogChanged;
                MonitorService.CategoryCheckStarted -= OnCategoryCheckStarted;
                MonitorService.CategoryCheckCompleted -= OnCategoryCheckCompleted;
            }
            _lifetimeCancellation?.Cancel();
            _lifetimeCancellation?.Dispose();
            _lifetimeCancellation = null;

            if (AppSettings != null)
            {
                AppSettings.ConfigurationSaved -= OnConfigurationSaved;
            }

            // Cleanup current category subscription to prevent memory leaks
            if (ViewModel?.SelectedCategory != null)
            {
                ViewModel.SelectedCategory.PropertyChanged -= OnCategoryPropertyChanged;
            }
        }

        private void OnCategoryCheckStarted(AssetCategory category)
        {
            if (category == ViewModel.SelectedCategory)
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (!IsLoaded || category != ViewModel.SelectedCategory) return;
                    foreach (var asset in ViewModel.Assets.Where(a => a.Status == "Pending"))
                    {
                        asset.Status = "Checking";
                    }
                });
            }
        }

        private void OnCategoryCheckCompleted(AssetCategory category)
        {
            if (category == ViewModel.SelectedCategory)
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (IsLoaded) RefreshAssetList();
                });
            }
        }

        private async void SaveAssetButton_Click(object sender, RoutedEventArgs e)
        {
            if (AssetDownloader == null || ViewModel.IsBusy) return;

            var button = sender as Button;
            var asset = button?.Tag as TrackedAsset;
            if (asset == null) return;

            if (!asset.CanDownload) return;
            var saveFileDialog = new SaveFileDialog { FileName = Path.GetFileName(new Uri(asset.Url).AbsolutePath) };
            string extension = Path.GetExtension(new Uri(asset.Url).AbsolutePath);
            if (!string.IsNullOrEmpty(extension)) saveFileDialog.Filter = $"Asset File (*{extension})|*{extension}|All files (*.*)|*.*";

            if (saveFileDialog.ShowDialog() == true)
            {
                await RunOperationAsync(async token =>
                {
                    await AssetDownloader.DownloadAssetToCustomPathAsync(asset.Url, saveFileDialog.FileName, token);
                    LogService.LogInteractiveSuccess($"Asset saved successfully", saveFileDialog.FileName, asset.DisplayName);
                });
            }
        }

        private void RemoveAssetButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var assetToRemove = button?.Tag as TrackedAsset;
            if (assetToRemove == null || ViewModel.SelectedCategory == null) return;

            var result = CustomMessageBoxService.ShowYesNo("Information", $"Are you sure you want to remove '{assetToRemove.DisplayName}'?", Window.GetWindow(this));
            if (result == true)
            {
                MonitorService.RemoveAsset(ViewModel.SelectedCategory, assetToRemove);
                RefreshAssetList();
            }
        }

        private void RemoveFoundButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedCategory == null || !ViewModel.Assets.Any(a => a.Status == "OK")) return;

            var result = CustomMessageBoxService.ShowYesNo("Clear Tracker", $"Are you sure you want to remove ALL found assets?", Window.GetWindow(this));
            if (result == true)
            {
                MonitorService.RemoveAllFoundAssets(ViewModel.SelectedCategory);
                RefreshAssetList();
            }
        }
    }
}
