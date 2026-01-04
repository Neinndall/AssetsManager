using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace AssetsManager.Views.Models.Controls
{
    public class StatusBarViewModel : INotifyPropertyChanged
    {
        private readonly List<string> _notificationMessages = new List<string>();
        private readonly DispatcherTimer _notificationCycleTimer;
        private int _currentNotificationIndex = 0;
        private int _notificationCount;
        private string _currentNotificationMessage = "";
        private string _allNotificationsTooltip = "";
        private string _statusText = "";
        private string _progressPercentage = null; // Default null -> Hidden via Converter

        public event PropertyChangedEventHandler PropertyChanged;

        public StatusBarViewModel()
        {
            _notificationCycleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
            _notificationCycleTimer.Tick += NotificationCycleTimer_Tick;
        }

        public string StatusText
        {
            get => _statusText;
            set
            {
                if (_statusText != value)
                {
                    _statusText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ProgressPercentage
        {
            get => _progressPercentage;
            set
            {
                if (_progressPercentage != value)
                {
                    _progressPercentage = value;
                    OnPropertyChanged();
                }
            }
        }

        public int NotificationCount
        {
            get => _notificationCount;
            private set
            {
                if (_notificationCount != value)
                {
                    _notificationCount = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CurrentNotificationMessage
        {
            get => _currentNotificationMessage;
            private set
            {
                if (_currentNotificationMessage != value)
                {
                    _currentNotificationMessage = value;
                    OnPropertyChanged();
                }
            }
        }

        public string AllNotificationsTooltip
        {
            get => _allNotificationsTooltip;
            private set
            {
                if (_allNotificationsTooltip != value)
                {
                    _allNotificationsTooltip = value;
                    OnPropertyChanged();
                }
            }
        }

        public void ShowNotification(bool show, string message = "Updates have been detected. Click to dismiss.")
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

            // Update Count
            NotificationCount = _notificationMessages.Count;

            if (_notificationMessages.Count > 1)
            {
                if (!_notificationCycleTimer.IsEnabled)
                    _notificationCycleTimer.Start();
            }
            else
            {
                _notificationCycleTimer.Stop();
                if (_notificationMessages.Count == 1)
                {
                    _currentNotificationIndex = 0;
                }
            }

            UpdateNotificationText();
        }

        public void ClearNotifications()
        {
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
                CurrentNotificationMessage = "";
                AllNotificationsTooltip = "";
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

            CurrentNotificationMessage = counterPrefix + currentMessage;
            AllNotificationsTooltip = string.Join("\n", _notificationMessages);
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
