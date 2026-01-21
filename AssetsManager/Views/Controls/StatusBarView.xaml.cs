using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AssetsManager.Views.Models.Controls;

namespace AssetsManager.Views.Controls
{
    public partial class StatusBarView : UserControl
    {
        public StatusBarViewModel ViewModel { get; } = new StatusBarViewModel();

        // Event to notify MainWindow when notification is clicked
        public event EventHandler NotificationClicked;
        public event EventHandler ProgressSummaryClicked;

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
            // Note: ProgressUIManager clears StatusText separately, but we can reset notifications here
            ShowNotification(false);
        }

        private void NotificationButton_Click(object sender, RoutedEventArgs e)
        {
            // Notify parent
            NotificationClicked?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        private void ProgressSummaryButton_Click(object sender, RoutedEventArgs e)
        {
            ProgressSummaryClicked?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
}