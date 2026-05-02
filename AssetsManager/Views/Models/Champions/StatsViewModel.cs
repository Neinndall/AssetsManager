using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Data;
using AssetsManager.Services.Champions;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Champions;

namespace AssetsManager.Views.Models.Champions
{
    public class StatsViewModel : INotifyPropertyChanged
    {
        private readonly ChampionDataService _championDataService;
        private readonly LogService _logService;
        
        private ObservableCollection<ChampionInfo> _champions = new ObservableCollection<ChampionInfo>();
        public ObservableCollection<ChampionInfo> Champions
        {
            get => _champions;
            set { _champions = value; OnPropertyChanged(); }
        }

        private ChampionInfo _selectedChampion;
        public ChampionInfo SelectedChampion
        {
            get => _selectedChampion;
            set { _selectedChampion = value; OnPropertyChanged(); }
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        private string _searchText;
        public string SearchText
        {
            get => _searchText;
            set
            {
                _searchText = value;
                OnPropertyChanged();
                _championsView.Refresh();
            }
        }

        private ICollectionView _championsView;

        public StatsViewModel(ChampionDataService championDataService, LogService logService)
        {
            _championDataService = championDataService;
            _logService = logService;
            _championsView = CollectionViewSource.GetDefaultView(Champions);
            _championsView.Filter = FilterChampions;
        }

        private bool FilterChampions(object obj)
        {
            if (string.IsNullOrWhiteSpace(SearchText)) return true;
            if (obj is ChampionInfo champ)
            {
                return champ.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                       champ.Id.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        public async Task LoadChampionsAsync()
        {
            if (Champions.Count > 0) return;

            IsLoading = true;
            try
            {
                var list = await _championDataService.GetChampionsAsync();
                foreach (var champ in list)
                {
                    Champions.Add(champ);
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to load champions in ViewModel.");
            }
            finally
            {
                IsLoading = false;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
