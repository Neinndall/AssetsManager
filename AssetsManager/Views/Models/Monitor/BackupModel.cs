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
        private int _totalBackupsCount;
        private string _totalStorageSize;
        private string _activeClientEnvironment;

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

        public int TotalBackupsCount
        {
            get => _totalBackupsCount;
            set { if (_totalBackupsCount != value) { _totalBackupsCount = value; OnPropertyChanged(); } }
        }

        public string TotalStorageSize
        {
            get => _totalStorageSize;
            set { if (_totalStorageSize != value) { _totalStorageSize = value; OnPropertyChanged(); } }
        }

        public string ActiveClientEnvironment
        {
            get => _activeClientEnvironment;
            set { if (_activeClientEnvironment != value) { _activeClientEnvironment = value; OnPropertyChanged(); } }
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
        public string Version { get; set; }
        public bool IsPbe { get; set; }
        public DateTime CreationDate { get; set; }
        public bool IsMainClient { get; set; }
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
