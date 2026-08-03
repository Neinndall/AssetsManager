using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AssetsManager.Views.Models.Notifications
{
    public enum NotificationType
    {
        Info,
        Success,
        Warning,
        Error
    }

    public enum NotificationCategory
    {
        System,
        Watcher,
        Tracker,
        Updates
    }

    public class NotificationModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private bool _isRead;
        private double _progressPercentage;
        private string _progressText;
        private bool _hasProgress;
        private bool _isIndeterminate;

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; }
        public string Message { get; set; }
        public NotificationType Type { get; set; }
        public NotificationCategory Category { get; set; } = NotificationCategory.System;
        public DateTime Timestamp { get; set; }
        [JsonIgnore]
        public string ActionText { get; set; }

        [JsonIgnore]
        public Action OnClickAction { get; set; }

        public bool IsRead
        {
            get => _isRead;
            set
            {
                if (_isRead != value)
                {
                    _isRead = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasProgress
        {
            get => _hasProgress;
            set
            {
                if (_hasProgress != value)
                {
                    _hasProgress = value;
                    OnPropertyChanged();
                }
            }
        }

        public double ProgressPercentage
        {
            get => _progressPercentage;
            set
            {
                if (Math.Abs(_progressPercentage - value) > 0.01)
                {
                    _progressPercentage = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ProgressText
        {
            get => _progressText;
            set
            {
                if (_progressText != value)
                {
                    _progressText = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            set
            {
                if (_isIndeterminate != value)
                {
                    _isIndeterminate = value;
                    OnPropertyChanged();
                }
            }
        }

        public NotificationModel()
        {
            Timestamp = DateTime.Now;
            IsRead = false;
        }

        public NotificationModel(string title, string message, NotificationType type, NotificationCategory category = NotificationCategory.System)
        {
            Title = title;
            Message = message;
            Type = type;
            Category = category;
            Timestamp = DateTime.Now;
            IsRead = false;
        }

        public void ExecuteAction()
        {
            OnClickAction?.Invoke();
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
