using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using AssetsManager.Utils;

namespace AssetsManager.Views.Models
{
    public class HistoryModel : INotifyPropertyChanged
    {
        public PaginationModel<JsonDiffHistoryEntry> Paginator { get; }

        public event PropertyChangedEventHandler PropertyChanged;

        public HistoryModel()
        {
            Paginator = new PaginationModel<JsonDiffHistoryEntry> { PageSize = 10 };
        }

        public void LoadHistory(IEnumerable<JsonDiffHistoryEntry> historyEntries)
        {
            Paginator.SetFullList(historyEntries);
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}