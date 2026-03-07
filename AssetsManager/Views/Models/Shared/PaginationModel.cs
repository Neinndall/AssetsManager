using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Views.Models.Shared
{
    public class PaginationModel<T> : INotifyPropertyChanged
    {
        private List<T> _fullList = new List<T>();

        public ObservableRangeCollection<T> PagedItems { get; } = new ObservableRangeCollection<T>();

        private int _currentPage = 1;
        public int CurrentPage
        {
            get => _currentPage;
            set
            {
                if (SetProperty(ref _currentPage, value))
                {
                    UpdatePaging();
                }
            }
        }

        private int _pageSize = 5;
        public int PageSize
        {
            get => _pageSize;
            set
            {
                if (SetProperty(ref _pageSize, value))
                {
                    UpdatePaging();
                }
            }
        }

        private int _totalPages;
        public int TotalPages
        {
            get => _totalPages;
            private set
            {
                if (SetProperty(ref _totalPages, value))
                {
                    OnPropertyChanged(nameof(CanGoToNextPage));
                    OnPropertyChanged(nameof(CanGoToPreviousPage));
                    OnPropertyChanged(nameof(PageInfo));
                }
            }
        }

        public bool CanGoToPreviousPage => CurrentPage > 1;
        public bool CanGoToNextPage => CurrentPage < TotalPages;
        public string PageInfo => $"Page {CurrentPage} / {TotalPages}";

        public void SetFullList(IEnumerable<T> fullList)
        {
            _fullList = fullList?.ToList() ?? new List<T>();
            CurrentPage = 1;
            UpdatePaging();
        }

        public void UpdatePaging()
        {
            if (_fullList == null || _fullList.Count == 0)
            {
                TotalPages = 0;
                CurrentPage = 0;
                PagedItems.Clear();
            }
            else
            {
                TotalPages = (int)Math.Ceiling((double)_fullList.Count / PageSize);
                if (CurrentPage > TotalPages) CurrentPage = TotalPages;
                if (CurrentPage < 1) CurrentPage = 1;

                var pagedItems = _fullList
                    .Skip((CurrentPage - 1) * PageSize)
                    .Take(PageSize);

                PagedItems.ReplaceRange(pagedItems);
            }

            OnPropertyChanged(nameof(CanGoToNextPage));
            OnPropertyChanged(nameof(CanGoToPreviousPage));
            OnPropertyChanged(nameof(PageInfo));
            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(TotalPages));
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

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<TValue>(ref TValue field, TValue value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<TValue>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
