using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Shared;

namespace AssetsManager.Views.Models.Monitor
{
    public class HistoryModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public PaginationModel<HistoryEntry> ComparisonsPaginator { get; }
        public PaginationModel<HistoryEntry> WatcherPaginator { get; }
        public PaginationModel<HistoryEntry> DifferencesPaginator { get; }

        private int _selectedTabIndex = 0; // 0: Comparisons, 1: Watcher, 2: Differences

        public HistoryModel()
        {
            ComparisonsPaginator = new PaginationModel<HistoryEntry> { PageSize = 10 };
            WatcherPaginator = new PaginationModel<HistoryEntry> { PageSize = 10 };
            DifferencesPaginator = new PaginationModel<HistoryEntry> { PageSize = 10 };
        }

        public IPaginationModel Paginator
        {
            get
            {
                return SelectedTabIndex switch
                {
                    0 => ComparisonsPaginator,
                    1 => WatcherPaginator,
                    2 => DifferencesPaginator,
                    _ => ComparisonsPaginator
                };
            }
        }

        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                if (_selectedTabIndex != value)
                {
                    _selectedTabIndex = value;
                    OnPropertyChanged(nameof(SelectedTabIndex));
                    OnPropertyChanged(nameof(Paginator));
                }
            }
        }

        public void LoadHistory(IEnumerable<HistoryEntry> historyEntries)
        {
            var entries = historyEntries?.ToList() ?? new List<HistoryEntry>();

            ComparisonsPaginator.SetFullList(entries.Where(e => e.Type == HistoryEntryType.WadArchive));
            WatcherPaginator.SetFullList(entries.Where(e => e.Type == HistoryEntryType.WatcherUpdate));
            DifferencesPaginator.SetFullList(entries.Where(e => e.Type == HistoryEntryType.IndividualFile));
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
