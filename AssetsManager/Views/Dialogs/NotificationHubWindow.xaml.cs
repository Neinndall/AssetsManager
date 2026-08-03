using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Notifications;
using AssetsManager.Views.Helpers;

namespace AssetsManager.Views.Dialogs
{
    public partial class NotificationHubWindow : HudWindow
    {
        public NotificationHubModel ViewModel => DataContext as NotificationHubModel;

        public NotificationHubWindow(NotificationService notificationService)
        {
            InitializeComponent();
            this.DataContext = new NotificationHubModel(notificationService);
        }

        private void MarkAllRead_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.MarkAllRead();
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ClearAll();
        }

        private void RemoveNotification_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is NotificationModel note)
            {
                ViewModel?.RemoveNotification(note);
            }
        }

        private void NotificationCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is NotificationModel note)
            {
                ViewModel?.ExecuteNotificationAction(note);
            }
        }

        private void NotificationAction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is NotificationModel note)
            {
                ViewModel?.ExecuteNotificationAction(note);
                e.Handled = true;
            }
        }

        private void FilterAll_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.SetCategoryFilter(null);
        }

        private void FilterSystem_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.SetCategoryFilter(NotificationCategory.System);
        }

        private void FilterWatcher_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.SetCategoryFilter(NotificationCategory.Watcher);
        }

        private void FilterTracker_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.SetCategoryFilter(NotificationCategory.Tracker);
        }

        private void FilterUpdates_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.SetCategoryFilter(NotificationCategory.Updates);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            ViewModel?.Dispose();
        }
    }
}
