using System;
using System.ComponentModel;

namespace AssetsManager.Views.Models.Monitor
{
    public class BackupModel : INotifyPropertyChanged
    {
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public string Name { get; set; }
        public string Path { get; set; }
        public DateTime CreationDate { get; set; }
        public bool IsCurrentSessionBackup { get; set; }

        public string StatusText => IsCurrentSessionBackup ? "Current Session" : "Previous Session";
        public string StatusColor => IsCurrentSessionBackup ? "#FF4CAF50" : "#FF9E9E9E"; // Green for current, Grey for previous

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}