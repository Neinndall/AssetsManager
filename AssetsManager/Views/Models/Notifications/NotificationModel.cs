using System;

namespace AssetsManager.Views.Models.Notifications
{
    public enum NotificationType
    {
        Info,
        Success,
        Warning,
        Error
    }

    public class NotificationModel
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public string Title { get; set; }
        public string Message { get; set; }
        public NotificationType Type { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsRead { get; set; }
        public Action OnClickAction { get; set; }

        public NotificationModel(string title, string message, NotificationType type)
        {
            Title = title;
            Message = message;
            Type = type;
            Timestamp = DateTime.Now;
            IsRead = false;
        }
    }
}
