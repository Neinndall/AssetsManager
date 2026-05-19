using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
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

            bool isCurrentlyConnected = await RiotApiService.ReadLockfileAsync(false);
            UpdateConnectionStatus(isCurrentlyConnected);
            if (isCurrentlyConnected)
            {
                _lcuConnectionTimer.Start();
            }

            // Explicit calls for each module's cache
            await LoadSalesCacheAsync();
            await LoadMythicShopCacheAsync();
            await LoadPassRewardsCacheAsync();
        }

        private async Task LoadSalesCacheAsync()
        {
            string filePath = Path.Combine(DirectoriesCreator.ApiCachePath, "sales.json");
            if (!File.Exists(filePath)) return;

            try
            {
                string json = await File.ReadAllTextAsync(filePath);
                var catalog = JsonSerializer.Deserialize<SalesCatalog>(json);
                if (catalog?.Catalog != null)
                {
                    var salesItems = catalog.Catalog.Where(i => i.InventoryType == "CHAMPION_SKIN" && i.Sale != null && i.SubInventoryType != "RECOLOR").ToList();
                    ViewModel?.SetFullSalesCatalog(salesItems);
                    _ = ExtractSkinImagesInBackgroundAsync(salesItems, "sales");
                }
            }
            catch (Exception ex) { LogService.LogError(ex, "Failed to load sales cache."); }
        }

        private async Task LoadMythicShopCacheAsync()
        {
            string filePath = Path.Combine(DirectoriesCreator.ApiCachePath, "mythic_shop.json");
            if (!File.Exists(filePath)) return;

            try
            {
                string json = await File.ReadAllTextAsync(filePath);
                var response = JsonSerializer.Deserialize<MythicShopResponse>(json);
                if (response != null) ProcessMythicShopData(response);
            }
            catch (Exception ex) { LogService.LogError(ex, "Failed to load mythic shop cache."); }
        }

        private async Task LoadPassRewardsCacheAsync()
        {
            string progPath = Path.Combine(DirectoriesCreator.ApiCachePath, "pass_progression.json");
            string rewardsPath = Path.Combine(DirectoriesCreator.ApiCachePath, "pass_rewards.json");

            if (!File.Exists(progPath) || !File.Exists(rewardsPath)) return;

            try
            {
                var progJson = await File.ReadAllTextAsync(progPath);
                var rewardsJson = await File.ReadAllTextAsync(rewardsPath);

                var prog = JsonSerializer.Deserialize<ProgressionResponse>(progJson);
                var rewards = JsonSerializer.Deserialize<RewardsResponse>(rewardsJson);

                if (prog != null && rewards != null)
                {
                    await ProcessPassRewardsDataAsync(prog, rewards);
                }
            }
            catch (Exception ex) { LogService.LogError(ex, "Failed to load pass rewards cache."); }
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
            if (LogService != null) LogService.Log("Starting sales fetch process...");
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
                var salesItems = salesCatalog.Catalog.Where(i => i.InventoryType == "CHAMPION_SKIN" && i.Sale != null && i.SubInventoryType != "RECOLOR").ToList();

                if (salesItems.Any())
                {
                    ViewModel?.SetFullSalesCatalog(salesItems);

                    // Start background image extraction for Sales items
                    _ = ExtractSkinImagesInBackgroundAsync(salesItems, "sales");
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
                CustomMessageBoxService.ShowInfo("Information", "No sales data to save.", Window.GetWindow(this));
                return;
            }

            await HandleExportRequestAsync("sales", ViewModel.SalesCatalog, (DataTemplate)this.FindResource("StoreItemTemplate"), 8);
        }

        private async void SaveMythicShopButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.MythicShopCategories.Count == 0)
            {
                CustomMessageBoxService.ShowInfo("Information", "No Mythic Shop data to save.", Window.GetWindow(this));
                return;
            }

            await HandleExportRequestAsync("mythic_shop", ViewModel.MythicShopCategories, (DataTemplate)this.FindResource("MythicItemTemplate"), 8);
        }

        private async void SavePassRewardsButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.PassRewards.Count == 0)
            {
                CustomMessageBoxService.ShowInfo("Information", "No pass rewards data to save.", Window.GetWindow(this));
                return;
            }

            await HandleExportRequestAsync("pass_rewards", ViewModel.PassRewards, (DataTemplate)this.FindResource("PassRewardTemplate"), 8);
        }

        private async Task HandleExportRequestAsync(string defaultFileName, System.Collections.IEnumerable items, DataTemplate template, int columns)
        {
            var dialog = new CommonSaveFileDialog
            {
                Title = $"Save {defaultFileName.Replace("_", " ")} data",
                DefaultFileName = defaultFileName,
                InitialDirectory = DirectoriesCreator.AssetsDownloadedPath,
                DefaultExtension = ".png"
            };
            dialog.Filters.Add(new CommonFileDialogFilter("PNG Image", "*.png"));

            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                try
                {
                    await ExportGridToPngAsync(items, template, columns, dialog.FileName);
                }
                catch (Exception ex)
                {
                    LogService.LogError(ex, $"Failed to export {defaultFileName} to PNG.");
                    CustomMessageBoxService.ShowError("Error", $"An error occurred while saving: {ex.Message}", Window.GetWindow(this));
                }
            }
        }

        private async Task ExportGridToPngAsync(System.Collections.IEnumerable items, DataTemplate template, int columns, string filePath)
        {
            // 1. Container
            var rootPanel = new StackPanel
            {
                Background = (Brush)Application.Current.FindResource("SidebarBackground"), // Professional Dark Background
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                Orientation = Orientation.Vertical,
                Width = 1200 // Fixed width for consistent high-quality output
            };

            // 2. Logic for both flat lists and categorized lists (Mythic)
            bool isCategorized = items is IEnumerable<MythicShopCategory>;

            if (isCategorized)
            {
                foreach (var category in (IEnumerable<MythicShopCategory>)items)
                {
                    var header = new Border
                    {
                        Background = (Brush)Application.Current.FindResource("CardBackground"),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(16, 8, 16, 8),
                        Margin = new Thickness(20, 20, 20, 12),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        BorderBrush = (Brush)Application.Current.FindResource("BorderColor"),
                        BorderThickness = new Thickness(1)
                    };
                    header.Child = new TextBlock
                    {
                        Text = category.CategoryName,
                        FontSize = 14,
                        FontWeight = FontWeights.Bold,
                        Foreground = (Brush)Application.Current.FindResource("TextPrimary")
                    };
                    rootPanel.Children.Add(header);

                    rootPanel.Children.Add(CreateUniformGrid(category.Items, template, columns));
                }
            }
            else
            {
                // Add a small padding at the top for flat lists
                rootPanel.Children.Add(new Border { Height = 20 });
                rootPanel.Children.Add(CreateUniformGrid(items, template, columns));
            }

            // Add bottom padding
            rootPanel.Children.Add(new Border { Height = 20 });

            // 3. Measure & Arrange for off-screen rendering
            rootPanel.Measure(new Size(rootPanel.Width, double.PositiveInfinity));
            rootPanel.Arrange(new Rect(0, 0, rootPanel.DesiredSize.Width, rootPanel.DesiredSize.Height));

            // 4. Save using utility
            await ImageExportUtils.SaveAsPngAsync(rootPanel, filePath, LogService);
        }

        private ItemsControl CreateUniformGrid(System.Collections.IEnumerable items, DataTemplate template, int columns)
        {
            var uniformGridFactory = new FrameworkElementFactory(typeof(UniformGrid));
            uniformGridFactory.SetValue(UniformGrid.ColumnsProperty, columns);
            
            return new ItemsControl
            {
                ItemsSource = items,
                ItemTemplate = template,
                ItemsPanel = new ItemsPanelTemplate(uniformGridFactory),
                Margin = new Thickness(10, 0, 10, 0)
            };
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
            if (LogService != null) LogService.Log("Starting mythic shop fetch process...");
            if (RiotApiService == null)
            {
                CustomMessageBoxService.ShowError("Error", "RiotApiService is not available.", Window.GetWindow(this));
                return;
            }

            ViewModel.IsBusy = true;
            var mythicShopResponse = await RiotApiService.GetMythicShopResponseAsync();
            ViewModel.IsBusy = false;

            if (mythicShopResponse == null)
            {
                CustomMessageBoxService.ShowError("Error", "Could not retrieve Mythic Shop data.", Window.GetWindow(this));
                return;
            }

            UpdateAuthenticationStatus();
            ProcessMythicShopData(mythicShopResponse);
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

                // Start background image extraction for Mythic Shop items
                var allMythicItems = finalCategories.SelectMany(c => c.Items).ToList();
                _ = ExtractSkinImagesInBackgroundAsync(allMythicItems, "mythic");
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Failed to process Mythic Shop data.");
                CustomMessageBoxService.ShowError("Error", $"An error occurred while processing Mythic Shop data: {ex.Message}", Window.GetWindow(this));
            }
        }

        private async Task ExtractSkinImagesInBackgroundAsync<T>(IEnumerable<T> items, string subDir = "mythic")
        {
            await Task.Run(async () => 
            {
                foreach (var item in items)
                {
                    try
                    {
                        var nameProperty = item.GetType().GetProperty("Name");
                        if (nameProperty == null) continue;

                        string name = (string)nameProperty.GetValue(item);
                        if (string.IsNullOrEmpty(name)) continue;

                        // Unified lookup for skins, emotes, wards, and icons
                        var assetPath = await RiotApiService.GetMythicAssetPathAsync(name);
                        
                        if (!string.IsNullOrEmpty(assetPath))
                        {
                            var localPath = await RiotApiService.ExtractMythicIconAsync(assetPath, subDir);
                            if (!string.IsNullOrEmpty(localPath))
                            {
                                var imagePathProperty = item.GetType().GetProperty("ImagePath");
                                if (imagePathProperty != null)
                                {
                                    Dispatcher.Invoke(() => imagePathProperty.SetValue(item, localPath));
                                }
                            }
                        }
                    }
                    catch { /* Silent skip for individual items */ }
                }
            });
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

        private void TabPass_Click(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 2;
        }

        private async void RequestsPassRewards_Click(object sender, RoutedEventArgs e)
        {
            if (LogService != null) LogService.Log("Starting pass rewards fetch process...");
            if (RiotApiService == null)
            {
                CustomMessageBoxService.ShowError("Error", "RiotApiService is not available.", Window.GetWindow(this));
                return;
            }

            string eventId = ViewModel.ManualPassId?.Trim();

            if (string.IsNullOrEmpty(eventId))
            {
                ViewModel.IsBusy = true;
                ViewModel.StatusText = "Status: Finding active pass...";

                eventId = await RiotApiService.GetActivePassGroupIdAsync();
                if (string.IsNullOrEmpty(eventId))
                {
                    ViewModel.IsBusy = false;
                    CustomMessageBoxService.ShowError("Error", "Could not find an active pass event ID.", Window.GetWindow(this));
                    return;
                }
            }

            ViewModel.IsBusy = true;
            ViewModel.StatusText = "Status: Fetching pass data...";
            string progressionJson = await RiotApiService.GetPassRewardsProgressionAsync(eventId);
            string rewardsJson = await RiotApiService.GetPassRewardsRewardsAsync();

            if (string.IsNullOrEmpty(progressionJson) || string.IsNullOrEmpty(rewardsJson))
            {
                ViewModel.IsBusy = false;
                CustomMessageBoxService.ShowError("Error", "Could not retrieve pass progression or rewards data.", Window.GetWindow(this));
                return;
            }

            try
            {
                var progression = JsonSerializer.Deserialize<ProgressionResponse>(progressionJson);
                var rewardsResponse = JsonSerializer.Deserialize<RewardsResponse>(rewardsJson);

                if (progression != null && rewardsResponse != null)
                {
                    await ProcessPassRewardsDataAsync(progression, rewardsResponse);
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(ex, "Failed to process Pass Rewards data.");
                CustomMessageBoxService.ShowError("Error", $"An error occurred: {ex.Message}", Window.GetWindow(this));
            }

            ViewModel.IsBusy = false;
            ViewModel.StatusText = "Status: LCU Connected";
            UpdateAuthenticationStatus();
        }

        private async Task ProcessPassRewardsDataAsync(ProgressionResponse progression, RewardsResponse rewardsResponse)
        {
            var rewardGroupsMap = rewardsResponse.Data.ToDictionary(g => g.Id, g => g);
            var passRewards = new List<PassRewardModel>();
            var processedKeys = new HashSet<string>();

            foreach (var milestone in progression.Milestones)
            {
                if (milestone.Properties == null || !milestone.Properties.TryGetValue("REWARD_GROUP_ID", out var rewardGroupIdObj))
                    continue;

                string rewardGroupId = rewardGroupIdObj.ToString();
                string translatedLevel = TranslateMilestoneName(milestone.Name);
                string key = $"{translatedLevel}-{rewardGroupId}";

                if (processedKeys.Contains(key)) continue;

                if (rewardGroupsMap.TryGetValue(rewardGroupId, out var rewardGroup))
                {
                    foreach (var reward in rewardGroup.Rewards)
                    {
                        if (reward.Media == null || string.IsNullOrEmpty(reward.Media.IconUrl))
                            continue;

                        string title = string.Empty;
                        string details = string.Empty;

                        if (reward.Localizations != null)
                        {
                            title = reward.Localizations.Title;
                            details = reward.Localizations.Details;
                        }

                        passRewards.Add(new PassRewardModel
                        {
                            Level = translatedLevel,
                            Title = TransformTitle(title, reward.Quantity),
                            Details = details,
                            IconUrl = reward.Media.IconUrl,
                            Quantity = reward.Quantity,
                            IsFree = milestone.Name.Contains("Free", StringComparison.OrdinalIgnoreCase)
                        });

                        processedKeys.Add(key);
                        break; // Following GeneratorRewards logic: process only the first reward
                    }
                }
            }

            ViewModel.PassRewards.ReplaceRange(passRewards);

            // Batch extract icons efficiently
            var urlsToExtract = passRewards.Select(r => r.IconUrl).ToList();
            
            _ = Task.Run(async () => 
            {
                await RiotApiService.ExtractRewardIconsBatchAsync(urlsToExtract, (originalUrl, localPath) => 
                {
                    // Find all models using this URL (there might be duplicates across levels)
                    var targets = passRewards.Where(r => r.IconUrl == originalUrl).ToList();
                    
                    Dispatcher.Invoke(() => 
                    {
                        foreach (var target in targets)
                        {
                            target.IconUrl = localPath;
                        }
                    });
                });
            });
        }

        private string TranslateMilestoneName(string name)
        {
            if (name.Contains("Milestone", StringComparison.OrdinalIgnoreCase))
            {
                int index = name.IndexOf("Milestone", StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    // Get everything after "Milestone"
                    string levelPart = name.Substring(index + "Milestone".Length);
                    
                    // Use Regex to extract only the leading numbers
                    var match = Regex.Match(levelPart, @"^(\d+)");
                    if (match.Success)
                    {
                        string levelNumber = match.Groups[1].Value.TrimStart('0');
                        if (string.IsNullOrEmpty(levelNumber)) levelNumber = "0";
                        return $"Level {levelNumber}";
                    }
                }
            }
            return name;
        }

        private string TransformTitle(string title, long quantity)
        {
            if (string.IsNullOrEmpty(title)) return title;

            var match = Regex.Match(title, @"rewards_title_Reward_(\w+)_(\d+)");
            if (match.Success)
            {
                var type = match.Groups[1].Value;
                var amount = match.Groups[2].Value;

                if (type.Equals("BE", StringComparison.OrdinalIgnoreCase))
                {
                    return $"{amount} esencias azules";
                }
            }
            return title;
        }
    }
}
