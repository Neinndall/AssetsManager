using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Services.Core;

namespace AssetsManager.Views.Models.Controls
{
    public class LogViewModel : INotifyPropertyChanged
    {
        private readonly NotificationService _notificationService;
        private int _notificationCount;
        private bool _isLogVisible = true;
        private bool _hasActiveStatus;

        public event PropertyChangedEventHandler PropertyChanged;

        public LogViewModel()
        {
            // Resolve service manually since this ViewModel is created by the View
            _notificationService = App.ServiceProvider.GetService<NotificationService>();
            
            if (_notificationService != null)
            {
                _notificationService.CountsChanged += UpdateCounts;
                UpdateCounts();
            }
        }

        private void UpdateCounts()
        {
            if (_notificationService == null) return;
            NotificationCount = _notificationService.GetNotifications().Count(n => !n.IsRead);
        }

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
                }
            }
        }

        public Visibility LogVisibility => IsLogVisible ? Visibility.Visible : Visibility.Collapsed;

        public void ToggleLog()
        {
            IsLogVisible = !IsLogVisible;
        }

        public void SetLogVisibility(bool isVisible)
        {
            IsLogVisible = isVisible;
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
