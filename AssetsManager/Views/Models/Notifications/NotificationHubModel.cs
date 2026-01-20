using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using AssetsManager.Services.Core;

namespace AssetsManager.Views.Models.Notifications
{
    public class NotificationHubModel : INotifyPropertyChanged
    {
        private readonly NotificationService _notificationService;
        private bool _isOpen;
        private int _unreadCount;

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<NotificationModel> Notifications => _notificationService.GetNotifications();

        public bool IsOpen
        {
            get => _isOpen;
            set
            {
                if (_isOpen != value)
                {
                    _isOpen = value;
                    OnPropertyChanged();
                }
            }
        }

        public int UnreadCount
        {
            get => _unreadCount;
            set
            {
                if (_unreadCount != value)
                {
                    _unreadCount = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasUnread));
                }
            }
        }

        public bool HasUnread => UnreadCount > 0;

        public NotificationHubModel(NotificationService notificationService)
        {
            _notificationService = notificationService;

            // Suscribirse a eventos del servicio
            _notificationService.NotificationAdded += OnNotificationAdded;
            _notificationService.CountsChanged += UpdateCounts;

            UpdateCounts();
        }

        private void OnNotificationAdded(NotificationModel note)
        {
            UpdateCounts();
        }

        private void UpdateCounts()
        {
            UnreadCount = Notifications.Count(n => !n.IsRead);
        }

        // Métodos de acción para ser llamados desde el code-behind
        public void TogglePanel() => IsOpen = !IsOpen;
        public void ClearAll() => _notificationService.ClearAll();
        public void MarkAllRead() => _notificationService.MarkAllAsRead();
        public void RemoveNotification(NotificationModel note) => _notificationService.RemoveNotification(note);

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
