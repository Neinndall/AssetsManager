using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Notifications;

namespace AssetsManager.Views.Dialogs
{
    public partial class NotificationHubWindow : Window
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
                this.Hide();
                if (ViewModel != null) ViewModel.IsOpen = false;

                // Return focus to the owner window when toggling off
                if (owner != null) owner.Activate();
                return;
            }

            this.Owner = owner;
            this.Show();
            this.Activate();
            if (ViewModel != null) ViewModel.IsOpen = true;
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Hide(); 
            if (ViewModel != null) ViewModel.IsOpen = false;

            // Return focus to the owner window to prevent background apps (like terminal) from jumping to front
            if (this.Owner != null)
            {
                this.Owner.Activate();
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
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
    }
}
