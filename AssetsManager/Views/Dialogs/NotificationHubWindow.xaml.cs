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

        public void ShowHub(Window owner)
        {
            if (this.IsVisible)
            {
                // If minimized, restore and focus
                if (this.WindowState == WindowState.Minimized)
                {
                    this.WindowState = WindowState.Normal;
                    this.Activate();
                    return;
                }

                // If visible but NOT minimized, toggle (Hide)
                if (owner != null) owner.Activate();
                this.Hide();
                if (ViewModel != null) ViewModel.IsOpen = false;
                return;
            }

            this.Owner = owner;
            this.Show();
            this.Activate();
            if (ViewModel != null) ViewModel.IsOpen = true;
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

        // Override the close behavior to Hide instead of Close
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            if (this.Owner != null)
            {
                this.Owner.Activate();
            }
            this.Hide();
            if (ViewModel != null) ViewModel.IsOpen = false;
        }
    }
}
