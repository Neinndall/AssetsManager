using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AssetsManager.Views.Models.Monitor
{
    public class BackupsControlModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private ObservableCollection<BackupModel> _allBackups;
        private bool _isBusy;

        public BackupsControlModel()
        {
            AllBackups = new ObservableCollection<BackupModel>();
        }

        public ObservableCollection<BackupModel> AllBackups
        {
            get => _allBackups;
            set { _allBackups = value; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { if (_isBusy != value) { _isBusy = value; OnPropertyChanged(); } }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class BackupModel : INotifyPropertyChanged
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public DateTime CreationDate { get; set; }
        public long Size { get; set; }
        public string SizeDisplay { get; set; }
        public bool IsCurrentSessionBackup { get; set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
