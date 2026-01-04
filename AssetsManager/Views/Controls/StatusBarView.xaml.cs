using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AssetsManager.Views.Controls
{
    public partial class StatusBarView : UserControl
    {
        private readonly List<string> _notificationMessages = new List<string>();
        private readonly DispatcherTimer _notificationCycleTimer;
        private int _currentNotificationIndex = 0;

        // Event to notify MainWindow when notification is clicked
        public event EventHandler NotificationClicked;

        // Public accessors for ProgressUIManager
        public Button SummaryButton => ProgressSummaryButton;
        public TextBlock StatusText => StatusTextBlock;
        public TextBlock PercentageText => ProgressPercentageTextBlock;

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

            _notificationCycleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
            _notificationCycleTimer.Tick += NotificationCycleTimer_Tick;
        }

        public void ShowNotification(bool show, string message = "Updates have been detected. Click to dismiss.")
        {
            Dispatcher.Invoke(() =>
            {
                if (show)
                {
                    if (!_notificationMessages.Contains(message))
                    {
                        _notificationMessages.Add(message);
                        // Show the new message immediately if it's the first one, or add to cycle
                        if (_notificationMessages.Count == 1)
                        {
                            _currentNotificationIndex = 0;
                        }
                    }
                }
                else
                {
                    _notificationMessages.Clear();
                    _notificationCycleTimer.Stop();
                }

                // Update the DependencyProperty
                NotificationCount = _notificationMessages.Count;

                if (_notificationMessages.Count > 1)
                {
                    if (!_notificationCycleTimer.IsEnabled)
                        _notificationCycleTimer.Start();
                }
                else
                {
                    _notificationCycleTimer.Stop();
                    // If there is only 1 message, index must be 0
                    if (_notificationMessages.Count == 1)
                    {
                        _currentNotificationIndex = 0;
                    }
                }

                UpdateNotificationText();
            });
        }

        public void ClearStatusBar()
        {
            // Note: ProgressUIManager clears StatusText separately, but we can reset notifications here
            ShowNotification(false);
        }

        private void NotificationCycleTimer_Tick(object sender, EventArgs e)
        {
            _currentNotificationIndex++;
            if (_currentNotificationIndex >= _notificationMessages.Count)
            {
                _currentNotificationIndex = 0;
            }
            UpdateNotificationText();
        }

        private void UpdateNotificationText()
        {
            if (!_notificationMessages.Any())
            {
                NotificationMessageText.Text = "";
                NotificationToolTipText.Text = "";
                return;
            }

            if (_currentNotificationIndex >= _notificationMessages.Count)
            {
                _currentNotificationIndex = 0;
            }

            string currentMessage = _notificationMessages[_currentNotificationIndex];
            string counterPrefix = _notificationMessages.Count > 1 
                ? $"[{_currentNotificationIndex + 1}/{_notificationMessages.Count}] " 
                : "";

            NotificationMessageText.Text = counterPrefix + currentMessage;

            // Tooltip always shows all notifications separated by new lines
            NotificationToolTipText.Text = string.Join("\n", _notificationMessages);
        }

        private void NotificationButton_Click(object sender, RoutedEventArgs e)
        {
            // Notify parent
            NotificationClicked?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
}