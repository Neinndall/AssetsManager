using System;
using System.Collections.ObjectModel;
using AssetsManager.Views.Models.Notifications;

namespace AssetsManager.Services.Core
{
    public class NotificationService
    {
        // Evento para avisar al ViewModel que hay una nueva notificación (para Toasts o actualizar lista)
        public event Action<NotificationModel> NotificationAdded;
        
        // Evento para actualizar el contador de no leídos
        public event Action CountsChanged;

        private readonly ObservableCollection<NotificationModel> _notifications;

        public NotificationService()
        {
            _notifications = new ObservableCollection<NotificationModel>();
        }

        public void AddNotification(string title, string message, NotificationType type = NotificationType.Info, Action onClick = null)
        {
            var notification = new NotificationModel(title, message, type)
            {
                OnClickAction = onClick
            };

            // Insertamos al principio para que salga la más reciente arriba
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _notifications.Insert(0, notification);
            });

            NotificationAdded?.Invoke(notification);
            CountsChanged?.Invoke();
        }

        public ObservableCollection<NotificationModel> GetNotifications()
        {
            return _notifications;
        }

        public void MarkAllAsRead()
        {
            foreach (var note in _notifications)
            {
                note.IsRead = true;
            }
            CountsChanged?.Invoke();
        }

        public void ClearAll()
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _notifications.Clear();
            });
            CountsChanged?.Invoke();
        }

        public void RemoveNotification(NotificationModel notification)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _notifications.Remove(notification);
            });
            CountsChanged?.Invoke();
        }
    }
}
