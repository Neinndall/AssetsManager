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

        // Dependency Property for Notification Count
        public static readonly DependencyProperty NotificationCountProperty =
            DependencyProperty.Register("NotificationCount", typeof(int), typeof(StatusBarView), new PropertyMetadata(0));

        public int NotificationCount
        {
            get { return (int)GetValue(NotificationCountProperty); }
            set { SetValue(NotificationCountProperty, value); }
        }

        public StatusBarView()
        {
            InitializeComponent();
            DataContext = ViewModel;

            ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(StatusBarViewModel.NotificationCount))
                {
                    NotificationCount = ViewModel.NotificationCount;
                }
            };
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