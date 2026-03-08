using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging; // Added for RenderTargetBitmap
using Microsoft.WindowsAPICodePack.Dialogs; // Added for CommonSaveFileDialog
using AssetsManager.Utils;
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Views.Models.Shared;

namespace AssetsManager.Views.Controls.Monitor
{
    public partial class ApiControl : UserControl
    {
        // Public properties for service injection
        public RiotApiService RiotApiService { get; set; }
        public AppSettings AppSettings { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public LogService LogService { get; set; }
        public DirectoriesCreator DirectoriesCreator { get; set; }

        private DispatcherTimer _lcuConnectionTimer;

        // The state model for this view (Container Pattern: Owner)
        private readonly ApiModel _viewModel;
        public ApiModel ViewModel => _viewModel;

        public ApiControl()
        {
            InitializeComponent();
            
            _viewModel = new ApiModel();
            DataContext = _viewModel;

            this.Loaded += ApiControl_Loaded;
            this.Unloaded += ApiControl_Unloaded;

            // Initialize the timer
            _lcuConnectionTimer = new DispatcherTimer();
            _lcuConnectionTimer.Interval = TimeSpan.FromSeconds(2);
            _lcuConnectionTimer.Tick += LcuConnectionTimer_Tick;
        }

        private async void ApiControl_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateAuthenticationStatus();

            bool isCurrentlyConnected = await RiotApiService.ReadLockfileAsync(false); // Silenciosa
            UpdateConnectionStatus(isCurrentlyConnected);
            if (isCurrentlyConnected)
            {
                _lcuConnectionTimer.Start();
            }

            await LoadSalesFromCacheAsync();
            await LoadMythicShopFromCacheAsync();
        }

        private async Task LoadMythicShopFromCacheAsync()
        {
            if (DirectoriesCreator == null || !Directory.Exists(DirectoriesCreator.ApiCachePath))
            {
                return;
            }

            try
            {
                var mythicShopFilePath = Path.Combine(DirectoriesCreator.ApiCachePath, "mythic_shop.json");

                if (File.Exists(mythicShopFilePath))
                {
                    var jsonContent = await File.ReadAllTextAsync(mythicShopFilePath);
                    var mythicShopResponse = JsonSerializer.Deserialize<MythicShopResponse>(jsonContent);

                    if (mythicShopResponse != null)
                    {
                        ProcessMythicShopData(mythicShopResponse);
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Failed to load mythic shop data from cache.");
            }
        }

        private async Task LoadSalesFromCacheAsync()
        {
            if (DirectoriesCreator == null || !Directory.Exists(DirectoriesCreator.ApiCachePath))
            {
                return;
            }

            try
            {
                var salesFilePath = Path.Combine(DirectoriesCreator.ApiCachePath, "sales.json");

                if (File.Exists(salesFilePath))
                {
                    var jsonContent = await File.ReadAllTextAsync(salesFilePath);
                    var salesCatalog = JsonSerializer.Deserialize<SalesCatalog>(jsonContent);

                    if (salesCatalog != null)
                    {
                        var salesItems = salesCatalog.Catalog.Where(i => i.InventoryType == "CHAMPION_SKIN" && i.Sale != null && i.SubInventoryType != "RECOLOR");
                        ViewModel?.SetFullSalesCatalog(salesItems);
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Failed to load sales data from cache.");
            }
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (RiotApiService == null)
            {
                CustomMessageBoxService.ShowError("Error", "RiotApiService is not available.", Window.GetWindow(this));
                return;
            }

            ViewModel?.IsBusy = true;
            ViewModel?.StatusText = "Status: Connecting to LCU...";

            if (AppSettings != null)
            {
                AppSettings.ApiSettings.Token = new TokenInfo();
                UpdateAuthenticationStatus(); // Update UI to show "Not Authenticated" immediately
            }

            bool lockfileRead = await RiotApiService.ReadLockfileAsync(true); // Con log de error
            
            UpdateConnectionStatus(lockfileRead); // This will set ViewModel?.IsConnected

            if (lockfileRead)
            {
                _lcuConnectionTimer.Stop(); // Stop any existing timer
                _lcuConnectionTimer.Start(); // Start monitoring
            }
            else
            {
                _lcuConnectionTimer.Stop(); // Stop if connection failed
            }

            ViewModel?.IsBusy = false;
        }

        private void UpdateConnectionStatus(bool isConnected)
        {
            if (isConnected)
            {
                ViewModel?.StatusText = "Status: LCU Connected";
                ViewModel?.StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentGreen");
                ViewModel?.ButtonContent = "Reconnect";
                ViewModel?.IsConnected = true;
            }
            else
            {
                ViewModel?.StatusText = "Status: Disconnected";
                ViewModel?.StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentRed");
                ViewModel?.ButtonContent = "Connect";
                ViewModel?.IsConnected = false;
                _lcuConnectionTimer.Stop(); // Stop the timer if disconnected

                if (AppSettings != null)
                {
                    AppSettings.ApiSettings.Token = new TokenInfo();
                    UpdateAuthenticationStatus(); 
                }
            }
        }

        private void UpdateAuthenticationStatus()
        {
            if (AppSettings != null && !string.IsNullOrEmpty(AppSettings.ApiSettings.Token.Jwt) && AppSettings.ApiSettings.Token.Expiration > DateTime.UtcNow)
            {
                ViewModel?.AuthenticationStatusText = "Status: Authenticated";
                ViewModel?.AuthenticationStatusColor = (SolidColorBrush)Application.Current.FindResource("AccentGreen");
                ViewModel?.IsAuthenticated = true;
            }
            else
            {
                ViewModel?.AuthenticationStatusText = "Status: Not Authenticated";
                ViewModel?.AuthenticationStatusColor = (SolidColorBrush)Application.Current.FindResource("AccentRed");
                ViewModel?.IsAuthenticated = false;
            }
            ViewModel?.Update(this.AppSettings); // Ensure computed properties (RegionText, etc.) are refreshed
        }

        private async void RequestsSales_Click(object sender, RoutedEventArgs e)
        {
            if (RiotApiService == null)
            {
                CustomMessageBoxService.ShowError("Error", "RiotApiService is not available.", Window.GetWindow(this));
                return;
            }

            ViewModel?.IsBusy = true;
            var salesCatalog = await RiotApiService.GetSalesCatalogAsync();
            ViewModel?.IsBusy = false;

            if (salesCatalog != null)
            {
                UpdateAuthenticationStatus();
                var salesItems = salesCatalog.Catalog.Where(i => i.InventoryType == "CHAMPION_SKIN" && i.Sale != null && i.SubInventoryType != "RECOLOR");

                if (salesItems.Any())
                {
                    ViewModel?.SetFullSalesCatalog(salesItems);
                    LogService.LogSuccess("Sales data retrieved and displayed successfully!");
                }
                else
                {
                    ViewModel?.SetFullSalesCatalog(Enumerable.Empty<CatalogItem>()); // Clear the view
                    LogService.LogWarning("Sales data was retrieved, but it contains no valid skin sales information (this is common for PBE).");
                }
            }
            else
            {
                CustomMessageBoxService.ShowError("Error", "Could not retrieve sales data.", Window.GetWindow(this));
            }
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.SalesCatalog.Count == 0)
            {
                CustomMessageBoxService.ShowInfo("Info", "No sales data to save.", Window.GetWindow(this));
                return;
            }

            var dialog = new CommonSaveFileDialog
            {
                Title = "Save sales data",
                DefaultFileName = "sales",
                InitialDirectory = DirectoriesCreator.AssetsDownloadedPath,
                DefaultExtension = ".png" // Default to PNG
            };
            dialog.Filters.Add(new CommonFileDialogFilter("PNG Image", "*.png"));

            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                string filePath = dialog.FileName;
                string extension = Path.GetExtension(filePath).ToLowerInvariant();

                try
                {
                    if (extension == ".png")
                    {
                        await SaveSalesAsPngAsync(filePath);
                    }
                    else
                    {
                        CustomMessageBoxService.ShowError("Error", "Unsupported file format selected. Only PNG is supported.", Window.GetWindow(this));
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogError(ex, $"Failed to save sales data to {filePath}.");
                    CustomMessageBoxService.ShowError("Error", $"An error occurred while saving: {ex.Message}", Window.GetWindow(this));
                }
            }
        }

        private async Task SaveSalesAsPngAsync(string filePath)
        {
            var items = ViewModel?.SalesCatalog; // Use the currently displayed page
            if (items == null || !items.Any())
            {
                CustomMessageBoxService.ShowInfo("Info", "No sales data to save.", Window.GetWindow(this));
                return;
            }

            // 1. Create the container
            var grid = new Grid
            {
                Background = this.Background, // Use the control's own background
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };

            var uniformGridFactory = new FrameworkElementFactory(typeof(UniformGrid));
            uniformGridFactory.SetValue(UniformGrid.ColumnsProperty, 3);
            var itemsPanelTemplate = new ItemsPanelTemplate(uniformGridFactory);

            // 2. Create the ItemsControl
            var itemsControl = new ItemsControl
            {
                ItemsSource = items,
                ItemTemplate = SalesItemsControl.ItemTemplate,
                ItemsPanel = itemsPanelTemplate
            };

            grid.Children.Add(itemsControl);

            // 3. Set width and measure
            grid.Width = SalesItemsControl.ActualWidth;
            grid.Measure(new Size(grid.Width, double.PositiveInfinity));
            grid.Arrange(new Rect(0, 0, grid.DesiredSize.Width, grid.DesiredSize.Height));

            // 4. Call the utility to save the element
            await ImageExportUtils.SaveAsPngAsync(grid, filePath, LogService);
        }

        private void PreviousPage_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.Paginator.PreviousPage();
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.Paginator.NextPage();
        }

        private async void RequestsMythicShop_Click(object sender, RoutedEventArgs e)
        {
            if (RiotApiService == null)
            {
                CustomMessageBoxService.ShowError("Error", "RiotApiService is not available.", Window.GetWindow(this));
                return;
            }

            ViewModel?.IsBusy = true;
            var mythicShopResponse = await RiotApiService.GetMythicShopResponseAsync();
            ViewModel?.IsBusy = false;

            if (mythicShopResponse == null)
            {
                CustomMessageBoxService.ShowError("Error", "Could not retrieve Mythic Shop data.", Window.GetWindow(this));
                return;
            }

            UpdateAuthenticationStatus();
            ProcessMythicShopData(mythicShopResponse);
            LogService.LogSuccess("Mythic Shop data retrieved and displayed successfully.");
        }

        private void ProcessMythicShopData(MythicShopResponse mythicShopResponse)
        {
            if (mythicShopResponse?.Data == null)
            {
                LogService.LogWarning("Mythic Shop data is null or invalid.");
                return;
            }

            try
            {
                ViewModel?.MythicShopCategories.Clear();
                var categories = new Dictionary<string, MythicShopCategory>();
                var categoryOrder = new[] { "FEATURED", "BIWEEKLY", "WEEKLY", "DAILY" };

                // Initialize categories to maintain order
                foreach (var catName in categoryOrder)
                {
                    categories[catName] = new MythicShopCategory { CategoryName = catName };
                }

                foreach (var section in mythicShopResponse.Data)
                {
                    var sectionNameParts = section.Name.Split('_');
                    if (sectionNameParts.Length > 2)
                    {
                        var categoryKey = sectionNameParts[2].ToUpper();
                        if (categories.TryGetValue(categoryKey, out var categoryViewModel))
                        {
                            var itemsToAdd = new List<MythicShopModel>();
                            foreach (var entry in section.CatalogEntries)
                            {
                                var purchaseUnit = entry.PurchaseUnits.FirstOrDefault();
                                if (purchaseUnit == null) continue;

                                var payment = purchaseUnit.PaymentOptions.FirstOrDefault()?.Payments.FirstOrDefault();
                                if (payment == null) continue;

                                itemsToAdd.Add(new MythicShopModel
                                {
                                    Name = purchaseUnit.Fulfillment.Name,
                                    Price = payment.Delta,
                                    EndTime = FormatUtils.FormatTimeRemaining(entry.EndTime)
                                });
                            }
                            categoryViewModel.Items.AddRange(itemsToAdd);
                        }
                    }
                }

                // Add to observable collection in the correct order using ReplaceRange
                var finalCategories = categoryOrder
                    .Where(catName => categories.ContainsKey(catName) && categories[catName].Items.Any())
                    .Select(catName => categories[catName])
                    .ToList();

                ViewModel?.MythicShopCategories.ReplaceRange(finalCategories);
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Failed to process Mythic Shop data.");
                CustomMessageBoxService.ShowError("Error", $"An error occurred while processing Mythic Shop data: {ex.Message}", Window.GetWindow(this));
            }
        }

        private async void SaveMythicShopButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.MythicShopCategories.Count == 0)
            {
                CustomMessageBoxService.ShowInfo("Info", "No Mythic Shop data to save.", Window.GetWindow(this));
                return;
            }

            var dialog = new CommonSaveFileDialog
            {
                Title = "Save Mythic Shop data",
                DefaultFileName = "mythic_shop",
                InitialDirectory = DirectoriesCreator.AssetsDownloadedPath,
                DefaultExtension = ".png"
            };
            dialog.Filters.Add(new CommonFileDialogFilter("PNG Image", "*.png"));

            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                string filePath = dialog.FileName;
                try
                {
                    await SaveMythicShopAsPngAsync(filePath);
                }
                catch (Exception ex)
                {
                    LogService.LogError(ex, $"Failed to save Mythic Shop data to {filePath}.");
                    CustomMessageBoxService.ShowError("Error", $"An error occurred while saving: {ex.Message}", Window.GetWindow(this));
                }
            }
        }

        private async Task SaveMythicShopAsPngAsync(string filePath)
        {
            var categories = ViewModel?.MythicShopCategories;
            if (categories == null || !categories.Any())
            {
                CustomMessageBoxService.ShowInfo("Info", "No Mythic Shop data to save.", Window.GetWindow(this));
                return;
            }

            var rootPanel = new StackPanel
            {
                Background = this.Background,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                Orientation = Orientation.Vertical
            };

            var itemTemplate = this.FindResource("MythicItemTemplate") as DataTemplate;

            foreach (var category in categories)
            {
                var categoryTitle = new TextBlock
                {
                    Text = category.CategoryName,
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(10, 10, 0, 10),
                    Foreground = (SolidColorBrush)Application.Current.FindResource("TextPrimary")
                };
                rootPanel.Children.Add(categoryTitle);

                var uniformGridFactory = new FrameworkElementFactory(typeof(UniformGrid));
                uniformGridFactory.SetValue(UniformGrid.ColumnsProperty, 4);
                var itemsPanelTemplate = new ItemsPanelTemplate(uniformGridFactory);

                var itemsControl = new ItemsControl
                {
                    ItemsSource = category.Items,
                    ItemTemplate = itemTemplate,
                    ItemsPanel = itemsPanelTemplate
                };

                rootPanel.Children.Add(itemsControl);
            }

            rootPanel.Width = MythicShopCategoriesItemsControl.ActualWidth;
            rootPanel.Measure(new Size(rootPanel.Width, double.PositiveInfinity));
            rootPanel.Arrange(new Rect(0, 0, rootPanel.DesiredSize.Width, rootPanel.DesiredSize.Height));

            await ImageExportUtils.SaveAsPngAsync(rootPanel, filePath, LogService);
        }

        private async void LcuConnectionTimer_Tick(object sender, EventArgs e)
        {
            if (RiotApiService == null) return;

            // Only update the status if there's a change, to avoid constant UI refreshes
            // and potential race conditions if the user clicks "Connect" at the same time.
            bool isStillConnected = await RiotApiService.ReadLockfileAsync(false); // Silenciosa
            if (isStillConnected != ViewModel?.IsConnected)
            {
                UpdateConnectionStatus(isStillConnected);
            }        
        }

        private void ApiControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _lcuConnectionTimer.Stop();
            // Unsubscribe to prevent potential memory leaks if the control is frequently loaded/unloaded
            _lcuConnectionTimer.Tick -= LcuConnectionTimer_Tick;
        }

        private void TabSales_Click(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 0;
        }

        private void TabMythic_Click(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 1;
        }
    }
}
