using System.Windows;
using System.Windows.Controls;
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

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (this.Owner != null)
            {
                this.Owner.Activate();
            }

            this.Hide(); 
            if (ViewModel != null) ViewModel.IsOpen = false;
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            SystemCommands.MinimizeWindow(this);
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
