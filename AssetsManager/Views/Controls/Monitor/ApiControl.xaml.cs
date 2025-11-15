using AssetsManager.Services.Api;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging; // Added for RenderTargetBitmap
using Microsoft.WindowsAPICodePack.Dialogs; // Added for CommonSaveFileDialog

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

        // The state model for this view
        public ApiModel Status { get; } = new ApiModel();

        public ApiControl()
        {
            InitializeComponent();
            this.DataContext = this;
            this.Loaded += ApiControl_Loaded;
        }

        private async void ApiControl_Loaded(object sender, RoutedEventArgs e)
        {
            // Check authentication state when the control is loaded and AppSettings should be available
            CheckAuthenticationState();
            await LoadSalesFromCacheAsync();
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
                        Status.Player = salesCatalog.Player;
                        var salesItems = salesCatalog.Catalog.Where(i => i.InventoryType == "CHAMPION_SKIN" && i.Sale != null);
                        Status.SetFullSalesCatalog(salesItems);
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Failed to load sales data from cache.");
            }
        }

        private async void GetTokenButton_Click(object sender, RoutedEventArgs e)
        {
            if (RiotApiService == null)
            {
                CustomMessageBoxService.ShowError("Error", "RiotApiService no está disponible.", Window.GetWindow(this));
                return;
            }

            Status.IsBusy = true;
            Status.StatusText = "Status: Authenticating...";
            Status.IsAuthenticated = false;

            bool lockfileRead = await RiotApiService.ReadLockfileAsync();
            if (!lockfileRead)
            {
                Status.StatusText = "Status: Error - Could not read lockfile. Is the game client running?";
                Status.StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentRed");
                Status.IsBusy = false;
                return;
            }

            await RiotApiService.AquireJwtAsync();
            CheckAuthenticationState(); // Update status after acquiring JWT
            Status.IsBusy = false;
        }

        private async void RequestsSales_Click(object sender, RoutedEventArgs e)
        {
            if (RiotApiService == null)
            {
                CustomMessageBoxService.ShowError("Error", "RiotApiService no está disponible.", Window.GetWindow(this));
                return;
            }

            Status.IsBusy = true;
            var salesCatalog = await RiotApiService.GetSalesCatalogAsync();
            Status.IsBusy = false;

            if (salesCatalog != null)
            {
                Status.Player = salesCatalog.Player;
                var salesItems = salesCatalog.Catalog.Where(i => i.InventoryType == "CHAMPION_SKIN" && i.Sale != null);

                if (salesItems.Any())
                {
                    Status.SetFullSalesCatalog(salesItems);
                    LogService.LogSuccess("Sales data retrieved and displayed successfully.");
                }
                else
                {
                    Status.SetFullSalesCatalog(Enumerable.Empty<CatalogItem>()); // Clear the view
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
            if (Status.SalesCatalog.Count == 0)
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
            try
            {
                var items = Status.SalesCatalog; // Use the currently displayed page
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
                var renderWidth = SalesItemsControl.ActualWidth;
                grid.Width = renderWidth;

                // To get the desired height, we need to run the layout pass
                grid.Measure(new Size(renderWidth, double.PositiveInfinity));
                grid.Arrange(new Rect(0, 0, grid.DesiredSize.Width, grid.DesiredSize.Height));

                var renderHeight = grid.DesiredSize.Height;

                if (renderWidth <= 0 || renderHeight <= 0)
                {
                    LogService.LogWarning("The size of the off-screen control for PNG capture is invalid.");
                    return;
                }

                // 4. Render
                RenderTargetBitmap rtb = new RenderTargetBitmap((int)renderWidth, (int)renderHeight, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(grid);

                // 5. Encode and Save
                PngBitmapEncoder pngEncoder = new PngBitmapEncoder();
                pngEncoder.Frames.Add(BitmapFrame.Create(rtb));

                using (MemoryStream ms = new MemoryStream())
                {
                    pngEncoder.Save(ms);
                    ms.Seek(0, SeekOrigin.Begin);

                    await Task.Run(async () =>
                    {
                        using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                        {
                            await ms.CopyToAsync(fileStream);
                        }
                    });
                }

                LogService.LogInteractiveSuccess($"Sales data saved as PNG to {filePath}", filePath, Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, $"Failed to save sales data as PNG to {filePath}.");
                CustomMessageBoxService.ShowError("Error", $"An error occurred while saving: {ex.Message}", Window.GetWindow(this));
            }
        }

        private void PreviousPage_Click(object sender, RoutedEventArgs e)
        {
            Status.Paginator.PreviousPage();
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            Status.Paginator.NextPage();
        }

        private void CheckAuthenticationState()
        {
            if (AppSettings != null && !string.IsNullOrEmpty(AppSettings.ApiSettings.Token.Jwt) && AppSettings.ApiSettings.Token.Expiration > DateTime.UtcNow)
            {
                Status.StatusText = "Status: Authenticated";
                Status.StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentGreen");
                Status.ButtonContent = "Refresh Token";
                Status.IsAuthenticated = true;
            }
            else
            {
                Status.StatusText = "Status: Not Authenticated";
                Status.StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentRed");
                Status.ButtonContent = "Get Token";
                Status.IsAuthenticated = false;
            }
            // Update the computed properties in the model
            Status.Update(this.AppSettings);
        }

        private void RequestsMythicShop_Click(object sender, RoutedEventArgs e)
        {
            LogService.Log("Requests MythicShop button clicked. Functionality not yet implemented.");
            CustomMessageBoxService.ShowInfo("Mythic Shop", "Mythic Shop functionality is not yet implemented.", Window.GetWindow(this));
        }

        private void SaveMythicShopButton_Click(object sender, RoutedEventArgs e)
        {
            LogService.Log("Save MythicShop button clicked. Functionality not yet implemented.");
            CustomMessageBoxService.ShowInfo("Mythic Shop", "Mythic Shop save functionality is not yet implemented.", Window.GetWindow(this));
        }
    }
}
