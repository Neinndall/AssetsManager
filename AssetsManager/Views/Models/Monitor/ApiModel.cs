using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows;
using System.Collections.Generic;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework; // Added for ObservableRangeCollection
using AssetsManager.Views.Models.Shared;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Views.Models.Monitor
{
    public class ApiModel : INotifyPropertyChanged
    {
        private ApiSettings _apiSettings;

        private string _statusText;
        public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

        private Brush _statusColor;
        public Brush StatusColor { get => _statusColor; set => SetProperty(ref _statusColor, value); }

        private bool _isConnected;
        public bool IsConnected { get => _isConnected; set => SetProperty(ref _isConnected, value); }

        private bool _isBusy;
        public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }

        private string _buttonContent;
        public string ButtonContent { get => _buttonContent; set => SetProperty(ref _buttonContent, value); }

        private string _authenticationStatusText;
        public string AuthenticationStatusText { get => _authenticationStatusText; set => SetProperty(ref _authenticationStatusText, value); }

        private Brush _authenticationStatusColor;
        public Brush AuthenticationStatusColor { get => _authenticationStatusColor; set => SetProperty(ref _authenticationStatusColor, value); }

        private bool _isAuthenticated;
        public bool IsAuthenticated { get => _isAuthenticated; set => SetProperty(ref _isAuthenticated, value); }

        private string _authButtonContent;
        public string AuthButtonContent { get => _authButtonContent; set => SetProperty(ref _authButtonContent, value); }

        private string _manualPassId;
        public string ManualPassId { get => _manualPassId; set => SetProperty(ref _manualPassId, value); }

        private ObservableCollection<string> _availablePasses;
        public ObservableCollection<string> AvailablePasses { get => _availablePasses; set => SetProperty(ref _availablePasses, value); }

        private string _selectedPass;
        public string SelectedPass { get => _selectedPass; set => SetProperty(ref _selectedPass, value); }

        public PaginationModel<CatalogItem> Paginator { get; }

        public ObservableRangeCollection<CatalogItem> SalesCatalog => Paginator.PagedItems;

        public ObservableRangeCollection<MythicShopCategory> MythicShopCategories { get; }

        public ObservableRangeCollection<PassRewardModel> PassRewards { get; }

        // Computed properties for display (cached, invalidated by Update)
        private string _regionText;
        private string _platformText;
        private string _summonerIdText;
        private string _puuidText;
        private string _issuedAtText;
        private string _expirationText;

        public string RegionText => _regionText ??= $"Region: {_apiSettings?.Token?.Region ?? "N/A"}";
        public string PlatformText => _platformText ??= $"Platform: {_apiSettings?.Token?.Platform ?? "N/A"}";
        public string SummonerIdText => _summonerIdText ??= $"Summoner ID: {(_apiSettings?.Token?.SummonerId > 0 ? _apiSettings.Token.SummonerId.ToString() : "N/A")}";
        public string PuuidText => _puuidText ??= $"PUUID: {_apiSettings?.Token?.Puuid ?? "N/A"}";
        public string IssuedAtText => _issuedAtText ??= $"Issued: {(_apiSettings?.Token?.IssuedAt > DateTime.MinValue ? _apiSettings.Token.IssuedAt.ToLocalTime().ToString("HH:mm:ss") : "N/A")}";
        public string ExpirationText => _expirationText ??= $"Expires: {(_apiSettings?.Token?.Expiration > DateTime.MinValue ? _apiSettings.Token.Expiration.ToLocalTime().ToString("HH:mm:ss") : "N/A")}";

        public ApiModel()
        {
            // Set default initial state for connection
            StatusText = "Status: Disconnected";
            StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentRed");
            ButtonContent = "Connect";
            IsConnected = false;
            IsBusy = false;

            // Set default initial state for authentication
            AuthenticationStatusText = "Status: Not Authenticated";
            AuthenticationStatusColor = (SolidColorBrush)Application.Current.FindResource("AccentRed");
            IsAuthenticated = false;
            AuthButtonContent = "Authenticate";

            Paginator = new PaginationModel<CatalogItem> { PageSize = 12 };
            MythicShopCategories = new ObservableRangeCollection<MythicShopCategory>();
            PassRewards = new ObservableRangeCollection<PassRewardModel>();
            AvailablePasses = new ObservableCollection<string>();
        }

        public void SetFullSalesCatalog(IEnumerable<CatalogItem> items)
        {
            Paginator.SetFullList(items);
        }

        public void NextPage()
        {
            Paginator.NextPage();
        }

        public void PreviousPage()
        {
            Paginator.PreviousPage();
        }

        public void Update(AppSettings appSettings)
        {
            _apiSettings = appSettings?.ApiSettings;
            _regionText = null;
            _platformText = null;
            _summonerIdText = null;
            _puuidText = null;
            _issuedAtText = null;
            _expirationText = null;
            // Notify that all computed properties may have changed
            OnPropertyChanged(nameof(RegionText));
            OnPropertyChanged(nameof(PlatformText));
            OnPropertyChanged(nameof(SummonerIdText));
            OnPropertyChanged(nameof(PuuidText));
            OnPropertyChanged(nameof(IssuedAtText));
            OnPropertyChanged(nameof(ExpirationText));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

}
