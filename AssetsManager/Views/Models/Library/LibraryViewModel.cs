using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AssetsManager.Services.Library;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Views.Models.Library
{
    public class LibraryViewModel : INotifyPropertyChanged
    {
        private readonly LibraryIndexService _libraryIndexService;
        private readonly AppSettings _settings;
        
        private ObservableRangeCollection<LibraryAsset> _filteredAssets = new ObservableRangeCollection<LibraryAsset>();
        private string _searchQuery;
        private string _selectedCategory = "All";
        private bool _isIndexing;
        private int _progressValue;
        private string _progressText;
        private int _totalAssetsCount;

        private CancellationTokenSource _indexingCts;

        public ObservableRangeCollection<LibraryAsset> FilteredAssets
        {
            get => _filteredAssets;
            set { _filteredAssets = value; OnPropertyChanged(); }
        }

        public string SearchQuery
        {
            get => _searchQuery;
            set 
            { 
                if (_searchQuery != value)
                {
                    _searchQuery = value; 
                    OnPropertyChanged();
                    _ = ApplyFiltersAsync();
                }
            }
        }

        public string SelectedCategory
        {
            get => _selectedCategory;
            set 
            { 
                if (_selectedCategory != value)
                {
                    _selectedCategory = value; 
                    OnPropertyChanged();
                    _ = ApplyFiltersAsync();
                }
            }
        }

        public bool IsIndexing
        {
            get => _isIndexing;
            set { _isIndexing = value; OnPropertyChanged(); }
        }

        public int ProgressValue
        {
            get => _progressValue;
            set { _progressValue = value; OnPropertyChanged(); }
        }

        public string ProgressText
        {
            get => _progressText;
            set { _progressText = value; OnPropertyChanged(); }
        }

        public int TotalAssetsCount
        {
            get => _totalAssetsCount;
            set { _totalAssetsCount = value; OnPropertyChanged(); }
        }

        public List<string> Categories { get; } = new List<string> 
        { 
            "All", "Champions", "Skins", "UI/HUD", "Maps", "Audio", "VFX", "Animations", "General" 
        };

        public LibraryViewModel(LibraryIndexService libraryIndexService, AppSettings settings)
        {
            _libraryIndexService = libraryIndexService;
            _settings = settings;

            _libraryIndexService.IndexingProgressChanged += (current, total, wad) => 
            {
                ProgressValue = (int)((double)current / total * 100);
                ProgressText = $"Indexing {wad} ({current}/{total})";
            };

            _libraryIndexService.IndexingCompleted += () => 
            {
                IsIndexing = false;
                _ = LoadInitialDataAsync();
            };
        }

        public async Task LoadInitialDataAsync()
        {
            var index = await _libraryIndexService.GetOrLoadIndexAsync();
            TotalAssetsCount = index?.Assets?.Count ?? 0;
            
            if (TotalAssetsCount == 0 && !IsIndexing)
            {
                ProgressText = "Library is empty. Click 'Rebuild Index' to start.";
            }
            
            await ApplyFiltersAsync();
        }

        public async Task RebuildIndexAsync()
        {
            if (IsIndexing) return;

            string gamePath = _settings.LolPbeDirectory ?? _settings.LolLiveDirectory;
            if (string.IsNullOrEmpty(gamePath))
            {
                ProgressText = "Error: No game path configured in settings.";
                return;
            }

            IsIndexing = true;
            ProgressValue = 0;
            _indexingCts = new CancellationTokenSource();

            try
            {
                await _libraryIndexService.RebuildIndexAsync(gamePath, _indexingCts.Token);
            }
            catch (OperationCanceledException)
            {
                ProgressText = "Indexing cancelled.";
            }
            catch (Exception ex)
            {
                ProgressText = $"Error: {ex.Message}";
            }
            finally
            {
                IsIndexing = false;
            }
        }

        public void CancelIndexing()
        {
            _indexingCts?.Cancel();
        }

        private async Task ApplyFiltersAsync()
        {
            // Debounce or small delay could be added here if needed
            var results = await _libraryIndexService.SearchAssetsAsync(SearchQuery, SelectedCategory);
            
            // Limit results for UI performance (e.g., first 1000)
            // Real performance will depend on virtualization
            FilteredAssets.ReplaceRange(results.Take(2000).ToList());
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
