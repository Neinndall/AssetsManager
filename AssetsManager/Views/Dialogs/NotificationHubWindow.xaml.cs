using System;
using System.Windows;
using System.Windows.Controls;
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

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            ViewModel?.Dispose();
        }
    }
}
