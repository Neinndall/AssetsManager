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
        private List<CatalogItem> _fullSalesCatalog = new List<CatalogItem>();

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

        public ObservableCollection<CatalogItem> SalesCatalog { get; set; } = new ObservableCollection<CatalogItem>();

        // Paging properties
        private int _currentPage = 1;
        public int CurrentPage { get => _currentPage; set { SetProperty(ref _currentPage, value); UpdatePaging(); } }

        private int _pageSize = 12;
        public int PageSize { get => _pageSize; set { SetProperty(ref _pageSize, value); UpdatePaging(); } }

        private int _totalPages;
        public int TotalPages { get => _totalPages; set { SetProperty(ref _totalPages, value); OnPropertyChanged(nameof(TotalPages)); OnPropertyChanged(nameof(CanGoToNextPage)); OnPropertyChanged(nameof(CanGoToPreviousPage)); OnPropertyChanged(nameof(PageInfo)); } }

        public bool CanGoToNextPage => CurrentPage < TotalPages;
        public bool CanGoToPreviousPage => CurrentPage > 1;
        public string PageInfo => $"{CurrentPage} / {TotalPages}";

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
        }

        public void SetFullSalesCatalog(IEnumerable<CatalogItem> items)
        {
            _fullSalesCatalog = items.ToList();
            CurrentPage = 1; // Reset to first page
            UpdatePaging();
        }

        public void UpdatePaging()
        {
            TotalPages = (int)Math.Ceiling((double)_fullSalesCatalog.Count / PageSize);
            if (TotalPages == 0) TotalPages = 1;
            if (CurrentPage > TotalPages) CurrentPage = TotalPages;
            if (CurrentPage < 1) CurrentPage = 1;

            var pagedItems = _fullSalesCatalog
                .Skip((CurrentPage - 1) * PageSize)
                .Take(PageSize);

            SalesCatalog.Clear();
            foreach (var item in pagedItems)
            {
                SalesCatalog.Add(item);
            }

            OnPropertyChanged(nameof(CanGoToNextPage));
            OnPropertyChanged(nameof(CanGoToPreviousPage));
            OnPropertyChanged(nameof(PageInfo));
        }

        public void NextPage()
        {
            if (CanGoToNextPage)
            {
                CurrentPage++;
            }
        }

        public void PreviousPage()
        {
            if (CanGoToPreviousPage)
            {
                CurrentPage--;
            }
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