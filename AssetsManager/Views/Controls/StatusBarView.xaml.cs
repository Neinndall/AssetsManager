using System;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Views.Models.Controls;

namespace AssetsManager.Views.Controls
{
    public partial class StatusBarView : UserControl
    {
        public MainWindow ParentWindow { get; set; }
        public StatusBarViewModel ViewModel { get; } = new StatusBarViewModel();

        public StatusBarView()
        {
            InitializeComponent();
            DataContext = ViewModel;
        }

        public void ShowNotification(bool show, string message = "Updates have been detected. Click to dismiss.")
        {
            Dispatcher.Invoke(() =>
            {
                ViewModel.ShowNotification(show, message);
            });
        }

        public void ClearStatusBar()
        {
            ShowNotification(false);
        }

        private void NotificationButton_Click(object sender, RoutedEventArgs e)
        {
            ParentWindow?.OnNotificationHubRequested(this, EventArgs.Empty);
            e.Handled = true;
        }

        private void ProgressSummaryButton_Click(object sender, RoutedEventArgs e)
        {
            ParentWindow?.HandleProgressSummaryClicked();
            e.Handled = true;
        }
    }
}
