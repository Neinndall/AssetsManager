using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using AssetsManager.Services.Core;

namespace AssetsManager.Views.Models.Notifications
{
    public class NotificationHubModel : INotifyPropertyChanged, IDisposable
    {
        private readonly NotificationService _notificationService;
        private bool _isOpen;
        private int _unreadCount;
        private NotificationCategory? _selectedCategory;

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<NotificationModel> Notifications => _notificationService.GetNotifications();
        public ICollectionView FilteredNotifications { get; }

        public NotificationCategory? SelectedCategory
        {
            get => _selectedCategory;
            set
            {
                if (_selectedCategory != value)
                {
                    _selectedCategory = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsAllSelected));
                    OnPropertyChanged(nameof(IsSystemSelected));
                    OnPropertyChanged(nameof(IsWatcherSelected));
                    OnPropertyChanged(nameof(IsComparatorSelected));
                    OnPropertyChanged(nameof(IsUpdatesSelected));
                    OnPropertyChanged(nameof(IsIssuesSelected));
                    FilteredNotifications.Refresh();
                    NotifyFilteredStateChanged();
                }
            }
        }

        public bool IsAllSelected => _selectedCategory == null;
        public bool IsSystemSelected => _selectedCategory == NotificationCategory.System;
        public bool IsWatcherSelected => _selectedCategory == NotificationCategory.Watcher;
        public bool IsComparatorSelected => _selectedCategory == NotificationCategory.Comparator;
        public bool IsUpdatesSelected => _selectedCategory == NotificationCategory.Updates;
        public bool IsIssuesSelected => _selectedCategory == NotificationCategory.Issues;
        public string SelectedCategoryTitle => _selectedCategory switch
        {
            NotificationCategory.System => "SYSTEM NOTIFICATIONS",
            NotificationCategory.Watcher => "WATCHER NOTIFICATIONS",
            NotificationCategory.Comparator => "COMPARATOR NOTIFICATIONS",
            NotificationCategory.Updates => "UPDATE NOTIFICATIONS",
            NotificationCategory.Issues => "ISSUE NOTIFICATIONS",
            _ => "ALL NOTIFICATIONS"
        };
        public int FilteredNotificationCount => FilteredNotifications.Cast<object>().Count();
        public bool HasFilteredNotifications => FilteredNotificationCount > 0;

        public bool IsOpen
        {
            get => _isOpen;
            set
            {
                if (_isOpen != value)
                {
                    _isOpen = value;
                    OnPropertyChanged();
                }
            }
        }

        public int UnreadCount
        {
            get => _unreadCount;
            set
            {
                if (_unreadCount != value)
                {
                    _unreadCount = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasUnread));
                }
            }
        }

        public bool HasUnread => UnreadCount > 0;

        public int UnreadSystemCount => Notifications.Count(n => !n.IsRead && n.Category == NotificationCategory.System);
        public int UnreadWatcherCount => Notifications.Count(n => !n.IsRead && n.Category == NotificationCategory.Watcher);
        public int UnreadComparatorCount => Notifications.Count(n => !n.IsRead && n.Category == NotificationCategory.Comparator);
        public int UnreadUpdatesCount => Notifications.Count(n => !n.IsRead && n.Category == NotificationCategory.Updates);
        public int UnreadIssuesCount => Notifications.Count(n => !n.IsRead && n.Category == NotificationCategory.Issues);
        public int SelectedUnreadCount => _selectedCategory switch
        {
            NotificationCategory.System => UnreadSystemCount,
            NotificationCategory.Watcher => UnreadWatcherCount,
            NotificationCategory.Comparator => UnreadComparatorCount,
            NotificationCategory.Updates => UnreadUpdatesCount,
            NotificationCategory.Issues => UnreadIssuesCount,
            _ => UnreadCount
        };
        public bool HasSelectedUnread => SelectedUnreadCount > 0;

        public NotificationHubModel(NotificationService notificationService)
        {
            _notificationService = notificationService;

            FilteredNotifications = new ListCollectionView(Notifications);
            FilteredNotifications.Filter = FilterNotificationByCategory;

            _notificationService.NotificationAdded += OnNotificationAdded;
            _notificationService.CountsChanged += UpdateCounts;

            UpdateCounts();
        }

        private bool FilterNotificationByCategory(object obj)
        {
            if (obj is not NotificationModel note) return false;
            if (_selectedCategory == null) return true;
            return note.Category == _selectedCategory.Value;
        }

        private void OnNotificationAdded(NotificationModel note)
        {
            UpdateCounts();
        }

        private void UpdateCounts()
        {
            UnreadCount = Notifications.Count(n => !n.IsRead);
            OnPropertyChanged(nameof(UnreadSystemCount));
            OnPropertyChanged(nameof(UnreadWatcherCount));
            OnPropertyChanged(nameof(UnreadComparatorCount));
            OnPropertyChanged(nameof(UnreadUpdatesCount));
            OnPropertyChanged(nameof(UnreadIssuesCount));
            FilteredNotifications.Refresh();
            NotifyFilteredStateChanged();
        }

        public void SetCategoryFilter(NotificationCategory? category)
        {
            SelectedCategory = category;
        }

        public void TogglePanel() => IsOpen = !IsOpen;
        public void ClearAll() => _notificationService.ClearAll();
        public void MarkAllRead() => _notificationService.MarkAllAsRead();
        public void RemoveNotification(NotificationModel note) => _notificationService.RemoveNotification(note);

        public void ExecuteNotificationAction(NotificationModel note)
        {
            _notificationService.ExecuteNotificationAction(note);
        }

        private void NotifyFilteredStateChanged()
        {
            OnPropertyChanged(nameof(SelectedCategoryTitle));
            OnPropertyChanged(nameof(FilteredNotificationCount));
            OnPropertyChanged(nameof(HasFilteredNotifications));
            OnPropertyChanged(nameof(SelectedUnreadCount));
            OnPropertyChanged(nameof(HasSelectedUnread));
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Dispose()
        {
            if (_notificationService != null)
            {
                _notificationService.NotificationAdded -= OnNotificationAdded;
                _notificationService.CountsChanged -= UpdateCounts;
            }
            FilteredNotifications.Filter = null;
            PropertyChanged = null;
        }
    }
}
