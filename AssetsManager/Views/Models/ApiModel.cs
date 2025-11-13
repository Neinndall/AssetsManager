using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows;
using System.Collections.Generic;
using AssetsManager.Utils;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace AssetsManager.Views.Models
{
    public class ApiModel : INotifyPropertyChanged
    {
        private ApiSettings _apiSettings;

        private string _statusText;
        public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

        private Brush _statusColor;
        public Brush StatusColor { get => _statusColor; set => SetProperty(ref _statusColor, value); }

        private bool _isAuthenticated;
        public bool IsAuthenticated { get => _isAuthenticated; set => SetProperty(ref _isAuthenticated, value); }

        private bool _isBusy;
        public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }

        private string _buttonContent;
        public string ButtonContent { get => _buttonContent; set => SetProperty(ref _buttonContent, value); }

        private PlayerInfo _player;
        public PlayerInfo Player { get => _player; set => SetProperty(ref _player, value); }

        public PaginationModel<CatalogItem> Paginator { get; }

        public ObservableCollection<CatalogItem> SalesCatalog => Paginator.PagedItems;

        // Computed properties for display
        public string RegionText => $"Region: {_apiSettings?.Token?.Region ?? "N/A"}";
        public string PlatformText => $"Platform: {_apiSettings?.Token?.Platform ?? "N/A"}";
        public string SummonerIdText => $"Summoner ID: {_apiSettings?.Token?.SummonerId ?? 0}";
        public string PuuidText => $"PUUID: {_apiSettings?.Token?.Puuid ?? "N/A"}";
        public string IssuedAtText => $"Issued: {_apiSettings?.Token?.IssuedAt.ToLocalTime().ToString("HH:mm:ss") ?? "N/A"}";
        public string ExpirationText => $"Expires: {_apiSettings?.Token?.Expiration.ToLocalTime().ToString("HH:mm:ss") ?? "N/A"}";

        public ApiModel()
        {
            // Set default initial state
            StatusText = "Status: Not Authenticated";
            StatusColor = (SolidColorBrush)Application.Current.FindResource("AccentRed");
            ButtonContent = "Get Token";
            IsAuthenticated = false;
            IsBusy = false;

            Paginator = new PaginationModel<CatalogItem> { PageSize = 12 };
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