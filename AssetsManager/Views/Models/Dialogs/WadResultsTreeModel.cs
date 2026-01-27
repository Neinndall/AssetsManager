using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AssetsManager.Views.Models.Wad;

namespace AssetsManager.Views.Models.Dialogs
{
    public class WadResultsTreeModel : INotifyPropertyChanged
    {
        private ObservableCollection<WadGroupViewModel> _wadGroups;
        private bool _isBusy;
        private string _loadingText = "LOADING DATA";

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); }
        }

        public string LoadingText
        {
            get => _loadingText;
            set { _loadingText = value; OnPropertyChanged(); }
        }

        public ObservableCollection<WadGroupViewModel> WadGroups
        {
            get => _wadGroups;
            set { _wadGroups = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}