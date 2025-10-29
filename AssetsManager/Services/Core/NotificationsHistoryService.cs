using AssetsManager.Views.Models;
using System;
using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace AssetsManager.Services.Core
{
    public class NotificationsHistoryService
    {
        public ObservableCollection<NotificationsModel> Notifications { get; }
        private DispatcherTimer _timer;

        public NotificationsHistoryService()
        {
            Notifications = new ObservableCollection<NotificationsModel>();

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMinutes(1);
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            foreach (var notification in Notifications)
            {
                notification.OnPropertyChanged(nameof(notification.IsNew));
            }
        }

        public void AddNotification(string message)
        {
            var notification = new NotificationsModel
            {
                Timestamp = DateTime.Now,
                Message = message
            };
            Notifications.Add(notification);
        }
    }
}