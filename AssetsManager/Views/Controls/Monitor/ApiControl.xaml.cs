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
using System.Windows.Media;

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
                CustomMessageBoxService.ShowError("Error", "RiotApiService no está disponible.", null);
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
                CustomMessageBoxService.ShowError("Error", "RiotApiService no está disponible.", null);
                return;
            }

            Status.IsBusy = true;
            var salesCatalog = await RiotApiService.GetSalesCatalogAsync();
            Status.IsBusy = false;

            if (salesCatalog != null)
            {
                Status.Player = salesCatalog.Player;
                var salesItems = salesCatalog.Catalog.Where(i => i.InventoryType == "CHAMPION_SKIN" && i.Sale != null);
                Status.SetFullSalesCatalog(salesItems);
                LogService.LogSuccess("Sales data retrieved and displayed successfully.");
            }
            else
            {
                CustomMessageBoxService.ShowError("Error", "Could not retrieve sales data.", null);
            }
        }

        private void PreviousPage_Click(object sender, RoutedEventArgs e)
        {
            Status.PreviousPage();
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            Status.NextPage();
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
    }
}
