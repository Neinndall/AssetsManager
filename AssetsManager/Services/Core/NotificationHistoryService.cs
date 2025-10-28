
using AssetsManager.Views.Models;
using System;
using System.Collections.ObjectModel;

namespace AssetsManager.Services.Core
{
    public class NotificationHistoryService
    {
        public ObservableCollection<NotificationModel> Notifications { get; }

        public NotificationHistoryService()
        {
            Notifications = new ObservableCollection<NotificationModel>();
        }

        public void AddNotification(string message)
        {
            var notification = new NotificationModel
            {
                Timestamp = DateTime.Now,
                Message = message
            };
            Notifications.Add(notification);
        }
    }
}
