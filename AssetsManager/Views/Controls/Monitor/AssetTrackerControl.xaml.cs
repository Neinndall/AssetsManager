using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.Monitor;
using AssetsManager.Views.Models;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows;

using System.Threading;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace AssetsManager.Views.Controls.Monitor
{
    public partial class AssetTrackerControl : UserControl
    {
        // Public properties for dependency injection from the container
        public MonitorService MonitorService { get; set; }
        public AssetDownloader AssetDownloader { get; set; }
        public LogService LogService { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }

        // Internal state properties
        public ObservableCollection<AssetCategory> Categories { get; set; }
        public AssetCategory SelectedCategory { get; set; }
        public ObservableCollection<TrackedAsset> Assets { get; set; }

        public AssetTrackerControl()
        {
            InitializeComponent();
            this.Loaded += AssetTrackerControl_Loaded;
            this.Unloaded += AssetTrackerControl_Unloaded;
            Categories = new ObservableCollection<AssetCategory>();
            Assets = new ObservableCollection<TrackedAsset>();
        }

        private void AssetTrackerControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (MonitorService == null) return;

            MonitorService.CategoryCheckStarted += OnCategoryCheckStarted;
            MonitorService.CategoryCheckCompleted += OnCategoryCheckCompleted;

            MonitorService.LoadAssetCategories();

            Categories.Clear();
            foreach (var category in MonitorService.AssetCategories)
            {
                Categories.Add(category);
            }

            CategoryComboBox.ItemsSource = Categories;

            if (Categories.Any())
            {
                CategoryComboBox.SelectedItem = Categories.First();
            }
        }

        private void CategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Update the internal SelectedCategory property from the ComboBox's selection
            SelectedCategory = CategoryComboBox.SelectedItem as AssetCategory;
            RefreshAssetList();
        }

        private void RefreshAssetList()
        {
            Assets.Clear();
            if (SelectedCategory == null || MonitorService == null) return;

            // The service now returns the exact list we need to display
            var assetsFromService = MonitorService.GetAssetListForCategory(SelectedCategory);

            foreach (var asset in assetsFromService)
            {
                Assets.Add(asset);
            }

            AssetsItemsControl.ItemsSource = Assets;
        }

        private async void DownloadButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (AssetDownloader == null)
            {
                CustomMessageBoxService.ShowError("Error", "Download service is not available.");
                return;
            }

            var assetsToDownload = Assets.Where(a => a.Status == "OK").ToList();

            if (!assetsToDownload.Any())
            {
                CustomMessageBoxService.ShowInfo("Info", "No assets to download.");
                return;
            }

            using (var folderBrowserDialog = new CommonOpenFileDialog())
            {
                folderBrowserDialog.IsFolderPicker = true;
                folderBrowserDialog.Title = $"Select folder to save the assets";
                if (folderBrowserDialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    string destinationPath = folderBrowserDialog.FileName;
                    int downloadedCount = 0;
                    try
                    {
                        DownloadButton.IsEnabled = false;
                        foreach (var asset in assetsToDownload)
                        {
                            string fileName = Path.GetFileName(new Uri(asset.Url).AbsolutePath);
                            string fullDestinationPath = Path.Combine(destinationPath, fileName);
                            await AssetDownloader.DownloadAssetToCustomPathAsync(asset.Url, fullDestinationPath);
                            downloadedCount++;
                        }
                        CustomMessageBoxService.ShowSuccess("Success", $"Successfully saved {downloadedCount} assets.");
                        LogService.LogInteractiveSuccess($"Successfully saved {downloadedCount} assets to '{destinationPath}'.", destinationPath);
                    }
                    catch (Exception ex)
                    {
                        CustomMessageBoxService.ShowError("Error", "An error occurred during download. Please check the logs for details.");
                        LogService.LogError(ex, "An error occurred during bulk asset download.");
                    }
                    finally
                    {
                        DownloadButton.IsEnabled = true;
                    }
                }
            }
        }

        private void LoadMoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (MonitorService == null || SelectedCategory == null) return;

            LoadMoreButton.IsEnabled = false;
            try
            {
                var newAssets = MonitorService.GenerateMoreAssets(Assets, SelectedCategory, 5); // Generate 5 more
                foreach (var asset in newAssets)
                {
                    Assets.Add(asset);
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "An error occurred while loading more assets.");
                CustomMessageBoxService.ShowError("Error", "An error occurred while loading more assets. Please check the logs.");
            }
            finally
            {
                LoadMoreButton.IsEnabled = true;
            }
        }

        private async void CheckButton_Click(object sender, RoutedEventArgs e)
        {
            if (MonitorService == null || SelectedCategory == null) return;

            var assetsToCheck = Assets.Where(a => a.Status == "Pending" || a.Status == "Not Found").ToList();
            if (!assetsToCheck.Any())
            {
                var result = CustomMessageBoxService.ShowYesNo("Info", "There are no pending or failed assets to check. Do you want to load more?");
                if (result != true) return;

                LoadMoreButton_Click(this, new RoutedEventArgs());
                // After loading more, we re-evaluate what to check from the updated Assets collection
                assetsToCheck = Assets.Where(a => a.Status == "Pending" || a.Status == "Not Found").ToList();
                if (!assetsToCheck.Any()) return; // Nothing more was loaded or found
            }

            CheckButton.IsEnabled = false;
            LoadMoreButton.IsEnabled = false;

            foreach (var asset in assetsToCheck)
            {
                asset.Status = "Checking";
            }

            try
            {
                await MonitorService.CheckAssetsAsync(assetsToCheck, SelectedCategory, CancellationToken.None);

                var foundCount = assetsToCheck.Count(a => a.Status == "OK");
                CustomMessageBoxService.ShowInfo("Info", $"Check finished. Found {foundCount} new assets out of {assetsToCheck.Count} checked.");
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "An error occurred during asset check.");
                CustomMessageBoxService.ShowError("Error", "An error occurred during asset check. Please check the logs.");
            }
            finally
            {
                CheckButton.IsEnabled = true;
                LoadMoreButton.IsEnabled = true;
            }
        }

        private void AssetTrackerControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (MonitorService != null)
            {
                MonitorService.CategoryCheckStarted -= OnCategoryCheckStarted;
                MonitorService.CategoryCheckCompleted -= OnCategoryCheckCompleted;
            }
        }

        private void OnCategoryCheckStarted(AssetCategory category)
        {
            if (category == SelectedCategory)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var asset in Assets.Where(a => a.Status == "Pending"))
                    {
                        asset.Status = "Checking";
                    }
                });
            }
        }

        private void OnCategoryCheckCompleted(AssetCategory category)
        {
            if (category == SelectedCategory)
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

            string extension = Path.GetExtension(asset.Url);

            var saveFileDialog = new SaveFileDialog
            {
                FileName = asset.DisplayName
            };

            if (!string.IsNullOrEmpty(extension))
            {
                saveFileDialog.Filter = $"Asset File (*{extension})|*{extension}|All files (*.*)|*.*";
                saveFileDialog.DefaultExt = extension;
            }
            else
            {
                saveFileDialog.Filter = "All files (*.*)|*.*";
            }

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    await AssetDownloader.DownloadAssetToCustomPathAsync(asset.Url, saveFileDialog.FileName);
                    CustomMessageBoxService.ShowSuccess("Success", $"Asset '{asset.DisplayName}' saved successfully.");
                }
                catch (Exception ex)
                {
                    LogService.LogError(ex, $"Failed to save asset '{asset.DisplayName}'.");
                    CustomMessageBoxService.ShowError("Error", $"Failed to save asset. Check logs for details.");
                }
            }
        }

        private void RemoveAssetButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var assetToRemove = button?.Tag as TrackedAsset;
            if (assetToRemove == null || SelectedCategory == null) return;

            var result = CustomMessageBoxService.ShowYesNo("Info", $"Are you sure you want to remove '{assetToRemove.DisplayName}'? This action is permanent and the asset will not appear again in this category.");
            if (result == true)
            {
                MonitorService.RemoveAsset(SelectedCategory, assetToRemove);
                RefreshAssetList();
            }
        }
    }
}