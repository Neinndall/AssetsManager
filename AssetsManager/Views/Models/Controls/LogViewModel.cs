using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Material.Icons;

namespace AssetsManager.Views.Models.Controls
{
    public class LogViewModel : INotifyPropertyChanged
    {
        private int _notificationCount;
        private bool _isLogVisible = true;
        private bool _hasActiveStatus;
        private MaterialIconKind _toggleIconKind = MaterialIconKind.ChevronDown;

        public event PropertyChangedEventHandler PropertyChanged;

        public int NotificationCount
        {
            get => _notificationCount;
            set
            {
                if (_notificationCount != value)
                {
                    _notificationCount = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasActiveStatus
        {
            get => _hasActiveStatus;
            set
            {
                if (_hasActiveStatus != value)
                {
                    _hasActiveStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsLogVisible
        {
            get => _isLogVisible;
            set
            {
                if (_isLogVisible != value)
                {
                    _isLogVisible = value;
                    OnPropertyChanged();
                    UpdateIcon();
                }
            }
        }

        public Visibility LogVisibility => IsLogVisible ? Visibility.Visible : Visibility.Collapsed;

        public MaterialIconKind ToggleIconKind
        {
            get => _toggleIconKind;
            private set
            {
                if (_toggleIconKind != value)
                {
                    _toggleIconKind = value;
                    OnPropertyChanged();
                }
            }
        }

        public void ToggleLog()
        {
            IsLogVisible = !IsLogVisible;
        }

        public void SetLogVisibility(bool isVisible)
        {
            IsLogVisible = isVisible;
        }

        private void UpdateIcon()
        {
            ToggleIconKind = IsLogVisible ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronUp;
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            if (propertyName == nameof(IsLogVisible))
            {
                OnPropertyChanged(nameof(LogVisibility));
            }
        }
    }
}
