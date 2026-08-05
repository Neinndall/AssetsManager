using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Notifications;

namespace AssetsManager.Services.Core
{
    public class NotificationService
    {
        private const int MaxHistoryCount = 100;
        private readonly string _historyFilePath;
        private readonly LogService _logService;

        // Event for ViewModel to handle new notifications (Toasts or UI updates)
        public event Action<NotificationModel> NotificationAdded;

        // Event for unread counters
        public event Action CountsChanged;

        private readonly ObservableCollection<NotificationModel> _notifications;

        public NotificationService(DirectoriesCreator directoriesCreator, LogService logService)
        {
            ArgumentNullException.ThrowIfNull(directoriesCreator);
            ArgumentNullException.ThrowIfNull(logService);

            _historyFilePath = directoriesCreator.NotificationsHistoryPath;
            _logService = logService;
            _notifications = new ObservableCollection<NotificationModel>();

            LoadHistory();
        }

        public void AddNotification(
            string title,
            string message,
            NotificationType type = NotificationType.Info,
            Action onClick = null,
            NotificationCategory category = NotificationCategory.System,
            string actionText = null)
        {
            var notification = new NotificationModel(title, message, type, category)
            {
                OnClickAction = onClick,
                ActionText = actionText
            };

            RunOnUiThread(() =>
            {
                _notifications.Insert(0, notification);
                TrimExcessHistory();
                NotificationAdded?.Invoke(notification);
                CountsChanged?.Invoke();
                SaveHistory();
            });
        }

        public NotificationModel AddProgressNotification(
            string title,
            string message,
            NotificationCategory category = NotificationCategory.System,
            Action onClick = null)
        {
            var notification = new NotificationModel(title, message, NotificationType.Info, category)
            {
                OnClickAction = onClick,
                HasProgress = true,
                ProgressPercentage = 0,
                ProgressText = "0%",
                IsIndeterminate = true
            };

            RunOnUiThread(() =>
            {
                _notifications.Insert(0, notification);
                TrimExcessHistory();
                NotificationAdded?.Invoke(notification);
                CountsChanged?.Invoke();
                SaveHistory();
            });

            return notification;
        }

        public ObservableCollection<NotificationModel> GetNotifications()
        {
            return _notifications;
        }

        public void MarkAllAsRead()
        {
            RunOnUiThread(() =>
            {
                foreach (var note in _notifications)
                {
                    note.IsRead = true;
                }

                CountsChanged?.Invoke();
                SaveHistory();
            });
        }

        public void ExecuteNotificationAction(NotificationModel notification)
        {
            if (notification == null) return;

            RunOnUiThread(() =>
            {
                if (!notification.IsRead)
                {
                    notification.IsRead = true;
                    CountsChanged?.Invoke();
                    SaveHistory();
                }

                try
                {
                    notification.ExecuteAction();
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Failed to execute notification action '{notification.Id}'.");
                }
            });
        }

        public void RemoveAllByCategory(NotificationCategory category)
        {
            RunOnUiThread(() =>
            {
                for (int i = _notifications.Count - 1; i >= 0; i--)
                {
                    if (_notifications[i].Category == category)
                    {
                        _notifications.RemoveAt(i);
                    }
                }
                CountsChanged?.Invoke();
                SaveHistory();
            });
        }

        public void RemoveNotification(NotificationModel notification)
        {
            if (notification == null) return;

            RunOnUiThread(() =>
            {
                _notifications.Remove(notification);
                CountsChanged?.Invoke();
                SaveHistory();
            });
        }

        private void TrimExcessHistory()
        {
            while (_notifications.Count > MaxHistoryCount)
            {
                _notifications.RemoveAt(_notifications.Count - 1);
            }
        }

        private void SaveHistory()
        {
            string tempPath = _historyFilePath + ".tmp";

            try
            {
                string dir = Path.GetDirectoryName(_historyFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_notifications.ToList(), options);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _historyFilePath, true);
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to save notification history.");

                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception cleanupException)
                {
                    _logService.LogError(cleanupException, "Failed to clean the temporary notification history file.");
                }
            }
        }

        private void LoadHistory()
        {
            try
            {
                if (!File.Exists(_historyFilePath)) return;

                string json = File.ReadAllText(_historyFilePath);
                if (string.IsNullOrWhiteSpace(json)) return;

                var loadedNotes = JsonSerializer.Deserialize<List<NotificationModel>>(json);
                if (loadedNotes != null)
                {
                    _notifications.Clear();
                    foreach (var note in loadedNotes.Where(note => note != null).Take(MaxHistoryCount))
                    {
                        _notifications.Add(note);
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to load notification history.");
            }
        }

        private static void RunOnUiThread(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.InvokeAsync(action);
        }
    }
}
