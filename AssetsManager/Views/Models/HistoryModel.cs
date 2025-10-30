using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using AssetsManager.Utils;

namespace AssetsManager.Views.Models
{
    public class HistoryModel : INotifyPropertyChanged
    {
        private const int PageSize = 10;
        private List<JsonDiffHistoryEntry> _allHistoryEntries;

        public ObservableCollection<JsonDiffHistoryEntry> PaginatedHistory { get; } = new ObservableCollection<JsonDiffHistoryEntry>();

        private int _currentPage = 1;
        public int CurrentPage
        {
            get => _currentPage;
            set
            {
                if (_currentPage != value)
                {
                    _currentPage = value;
                    OnPropertyChanged(nameof(CurrentPage));
                    UpdatePaginatedView();
                }
            }
        }

        private int _totalPages;
        public int TotalPages
        {
            get => _totalPages;
            private set
            {
                if (_totalPages != value)
                {
                    _totalPages = value;
                    OnPropertyChanged(nameof(TotalPages));
                }
            }
        }

        public bool CanGoToPrevPage => CurrentPage > 1;
        public bool CanGoToNextPage => CurrentPage < TotalPages;

        public event PropertyChangedEventHandler PropertyChanged;

        public void LoadHistory(IEnumerable<JsonDiffHistoryEntry> historyEntries)
        {
            _allHistoryEntries = historyEntries.ToList();
            if (!_allHistoryEntries.Any())
            {
                CurrentPage = 1;
                TotalPages = 0;
                PaginatedHistory.Clear();
                OnPropertyChanged(nameof(CanGoToPrevPage));
                OnPropertyChanged(nameof(CanGoToNextPage));
            }
            else
            {
                CurrentPage = 1;
                UpdatePaginatedView();
            }
        }

        private void UpdatePaginatedView()
        {
            TotalPages = (int)Math.Ceiling(_allHistoryEntries.Count / (double)PageSize);
            OnPropertyChanged(nameof(CanGoToPrevPage));
            OnPropertyChanged(nameof(CanGoToNextPage));

            var pagedItems = _allHistoryEntries.Skip((CurrentPage - 1) * PageSize).Take(PageSize);

            PaginatedHistory.Clear();
            foreach (var item in pagedItems)
            {
                PaginatedHistory.Add(item);
            }
        }

        public void NextPage()
        {
            if (CanGoToNextPage)
            {
                CurrentPage++;
            }
        }

        public void PrevPage()
        {
            if (CanGoToPrevPage)
            {
                CurrentPage--;
            }
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}