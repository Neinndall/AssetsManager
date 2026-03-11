using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using System.Windows;
using System.Threading;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
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

            if (AppSettings != null)
            {
                AppSettings.ConfigurationSaved += OnConfigurationSaved;
            }

            MonitorService.CategoryCheckStarted += OnCategoryCheckStarted;
            MonitorService.CategoryCheckCompleted += OnCategoryCheckCompleted;

            LoadTrackerData();
        }

        private void OnConfigurationSaved(object sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() => LoadTrackerData());
        }

        private void LoadTrackerData()
        {
            MonitorService.LoadAssetCategories();
            ViewModel.Categories.ReplaceRange(MonitorService.AssetCategories);

            CategoryComboBox.ItemsSource = ViewModel.Categories;

            if (ViewModel.Categories.Any())
            {
                CategoryComboBox.SelectedItem = ViewModel.Categories.First();
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
                ViewModel.SelectedCategory.PropertyChanged += OnCategoryPropertyChanged;
            }

            RefreshAssetList();
        }

        private void OnCategoryPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AssetCategory.Start))
            {
                RefreshAssetList();
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

            AssetsItemsControl.ItemsSource = ViewModel.Assets;
        }

        private async void DownloadButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (AssetDownloader == null)
            {
                CustomMessageBoxService.ShowError("Error", "Download service is not available.", Window.GetWindow(this));
                return;
            }

            var assetsToDownload = ViewModel.Assets.Where(a => a.Status == "OK").ToList();

            if (!assetsToDownload.Any())
            {
                CustomMessageBoxService.ShowInfo("Info", "No assets to download.", Window.GetWindow(this));
                return;
            }

            using (var folderBrowserDialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = $"Select folder to save the assets"
            })
            {
                if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    string destinationPath = folderBrowserDialog.FileName;
                    int downloadedCount = 0;
                    try
                    {
                        ViewModel.IsBusy = true;
                        foreach (var asset in assetsToDownload)
                        {
                            string fileName = Path.GetFileName(new Uri(asset.Url).AbsolutePath);
                            string fullDestinationPath = Path.Combine(destinationPath, fileName);
                            await AssetDownloader.DownloadAssetToCustomPathAsync(asset.Url, fullDestinationPath);
                            downloadedCount++;
                        }
                        
                        LogService.LogInteractiveSuccess($"Successfully saved {downloadedCount} assets to {Path.GetFileName(destinationPath)}.", destinationPath, Path.GetFileName(destinationPath));
                    }
                    catch (Exception ex)
                    {
                        CustomMessageBoxService.ShowError("Error", "An error occurred during download.", Window.GetWindow(this));
                        LogService.LogError(ex, "An error occurred during bulk asset download.");
                    }
                    finally
                    {
                        ViewModel.IsBusy = false;
                    }
                }
            }
        }

        private void LoadMoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (MonitorService == null || ViewModel.SelectedCategory == null) return;

            ViewModel.IsBusy = true;
            try
            {
                var newAssets = MonitorService.GenerateMoreAssets(ViewModel.Assets, ViewModel.SelectedCategory, 5);
                ViewModel.Assets.AddRange(newAssets);
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
            if (MonitorService == null || ViewModel.SelectedCategory == null) return;

            var assetsToCheck = ViewModel.Assets.Where(a => a.Status == "Pending" || a.Status == "Not Found").ToList();
            if (!assetsToCheck.Any())
            {
                var result = CustomMessageBoxService.ShowYesNo("Info", "There are no pending or failed assets to check. Do you want to load more?", Window.GetWindow(this));
                if (result != true) return;

                LoadMoreButton_Click(this, new RoutedEventArgs());
                assetsToCheck = ViewModel.Assets.Where(a => a.Status == "Pending" || a.Status == "Not Found").ToList();
                if (!assetsToCheck.Any()) return;
            }

            ViewModel.IsBusy = true;

            foreach (var asset in assetsToCheck)
            {
                asset.Status = "Checking";
            }

            try
            {
                await MonitorService.CheckAssetsAsync(assetsToCheck, ViewModel.SelectedCategory, CancellationToken.None);
                var foundCount = assetsToCheck.Count(a => a.Status == "OK");
                CustomMessageBoxService.ShowInfo("Info", $"Check finished. Found {foundCount} new assets.", Window.GetWindow(this));
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "An error occurred during asset check.");
            }
            finally
            {
                ViewModel.IsBusy = false;
            }
        }

        private void AssetTrackerControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (MonitorService != null)
            {
                MonitorService.CategoryCheckStarted -= OnCategoryCheckStarted;
                MonitorService.CategoryCheckCompleted -= OnCategoryCheckCompleted;
            }

            if (AppSettings != null)
            {
                AppSettings.ConfigurationSaved -= OnConfigurationSaved;
            }
        }

        private void OnCategoryCheckStarted(AssetCategory category)
        {
            if (category == ViewModel.SelectedCategory)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
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
                Application.Current.Dispatcher.Invoke(() =>
                {
                    RefreshAssetList();
                });
            }
        }

        private async void SaveAssetButton_Click(object sender, RoutedEventArgs e)
        {
            if (AssetDownloader == null) return;

            var button = sender as Button;
            var asset = button?.Tag as TrackedAsset;
            if (asset == null) return;

            var saveFileDialog = new SaveFileDialog { FileName = asset.DisplayName };
            string extension = Path.GetExtension(asset.Url);
            if (!string.IsNullOrEmpty(extension)) saveFileDialog.Filter = $"Asset File (*{extension})|*{extension}|All files (*.*)|*.*";

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    await AssetDownloader.DownloadAssetToCustomPathAsync(asset.Url, saveFileDialog.FileName);
                    LogService.LogInteractiveSuccess($"Asset saved successfully", saveFileDialog.FileName, asset.DisplayName);
                }
                catch (Exception ex)
                {
                    LogService.LogError(ex, $"Failed to save asset '{asset.DisplayName}'.");
                }
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

            var result = CustomMessageBoxService.ShowYesNo("Warning", $"Are you sure you want to remove ALL found assets?", Window.GetWindow(this));
            if (result == true)
            {
                MonitorService.RemoveAllFoundAssets(ViewModel.SelectedCategory);
                RefreshAssetList();
            }
        }
    }
}
