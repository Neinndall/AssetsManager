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
        public PaginationModel<HistoryEntry> Paginator { get; }

        public event PropertyChangedEventHandler PropertyChanged;

        public HistoryModel()
        {
            Paginator = new PaginationModel<HistoryEntry> { PageSize = 10 };
        }

        public void LoadHistory(IEnumerable<HistoryEntry> historyEntries)
        {
            Paginator.SetFullList(historyEntries);
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
