using System;
using System.IO;
using System.Linq;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Notifications;
using Serilog;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Core
{
    public sealed class NotificationServiceTests : IDisposable
    {
        private readonly string _rootPath;
        private readonly DirectoriesCreator _directories;
        private readonly LogService _logService;

        public NotificationServiceTests()
        {
            _rootPath = Path.Combine(Path.GetTempPath(), "AssetsManager.NotificationTests", Guid.NewGuid().ToString("N"));
            _directories = new DirectoriesCreator(_rootPath);
            _logService = new LogService(new LoggerConfiguration().CreateLogger());
        }

        [Fact]
        public void ExecutingActionMarksNotificationReadAndPersistsTheCounterState()
        {
            var service = CreateService();
            int countChanges = 0;
            int actionExecutions = 0;
            service.CountsChanged += () => countChanges++;

            service.AddNotification(
                "Update ready",
                "A build is ready to install.",
                NotificationType.Info,
                () => actionExecutions++,
                NotificationCategory.Updates,
                "Install");

            NotificationModel notification = Assert.Single(service.GetNotifications());
            service.ExecuteNotificationAction(notification);

            Assert.True(notification.IsRead);
            Assert.Equal(1, actionExecutions);
            Assert.Equal(2, countChanges);

            var restoredService = CreateService();
            NotificationModel restored = Assert.Single(restoredService.GetNotifications());
            Assert.True(restored.IsRead);
            Assert.Equal(NotificationCategory.Updates, restored.Category);
        }

        [Fact]
        public void InteractiveActionsAreNotRestoredWithoutTheirRuntimeDelegate()
        {
            var service = CreateService();

            service.AddNotification(
                "Open report",
                "The report is ready.",
                NotificationType.Success,
                () => { },
                NotificationCategory.Updates,
                "View report");

            string json = File.ReadAllText(_directories.NotificationsHistoryPath);
            Assert.DoesNotContain(nameof(NotificationModel.ActionText), json, StringComparison.Ordinal);

            var restoredService = CreateService();
            NotificationModel restored = Assert.Single(restoredService.GetNotifications());
            Assert.Null(restored.ActionText);
            Assert.Null(restored.OnClickAction);
        }

        [Fact]
        public void CategoryFilterUpdatesItsTitleAndVisibleCount()
        {
            var service = CreateService();
            service.AddNotification("System", "System message");
            service.AddNotification(
                "Update",
                "Update message",
                category: NotificationCategory.Updates);

            using var model = new NotificationHubModel(service);

            Assert.Equal("ALL NOTIFICATIONS", model.SelectedCategoryTitle);
            Assert.Equal(2, model.FilteredNotificationCount);

            model.SetCategoryFilter(NotificationCategory.Updates);

            Assert.Equal("UPDATE NOTIFICATIONS", model.SelectedCategoryTitle);
            Assert.Equal(1, model.FilteredNotificationCount);
            Assert.Equal(NotificationCategory.Updates, model.FilteredNotifications.Cast<NotificationModel>().Single().Category);
        }

        [Fact]
        public void DisposingHubModelReleasesItsCollectionFilter()
        {
            var service = CreateService();
            var model = new NotificationHubModel(service);

            model.Dispose();

            Assert.Null(model.FilteredNotifications.Filter);
        }

        [Fact]
        public void NewsArticleMetadataPersistsAcrossRestartForReadSync()
        {
            var service = CreateService();

            service.AddNotification(
                "Riot News",
                "New dev update",
                NotificationType.Info,
                category: NotificationCategory.News,
                newsArticleUrl: "https://www.leagueoflegends.com/en-us/news/dev/new-dev-update",
                newsPublishedAt: new DateTime(2026, 8, 6, 12, 0, 0));

            NotificationModel notification = Assert.Single(service.GetNotifications());
            Assert.Equal(NotificationCategory.News, notification.Category);
            Assert.Equal("https://www.leagueoflegends.com/en-us/news/dev/new-dev-update", notification.NewsArticleUrl);
            Assert.Equal(new DateTime(2026, 8, 6, 12, 0, 0), notification.NewsPublishedAt);

            var restoredService = CreateService();
            NotificationModel restored = Assert.Single(restoredService.GetNotifications());
            Assert.Equal(notification.NewsArticleUrl, restored.NewsArticleUrl);
            Assert.Equal(notification.NewsPublishedAt, restored.NewsPublishedAt);
        }

        [Fact]
        public void MarkingReadRaisesReadEventForEveryNewsNotification()
        {
            var service = CreateService();
            int readEvents = 0;
            service.NotificationsMarkedAsRead += _ => readEvents++;

            service.AddNotification(
                "Riot News",
                "New dev update",
                category: NotificationCategory.News,
                newsArticleUrl: "https://www.leagueoflegends.com/en-us/news/dev/new-dev-update");
            service.AddNotification(
                "System",
                "System message");

            Assert.Equal(0, readEvents);

            service.MarkAllAsRead();

            Assert.Equal(2, readEvents);
        }

        [Fact]
        public void ExecutingReadNewsNotificationRaisesReadEventOnce()
        {
            var service = CreateService();
            int readEvents = 0;
            service.NotificationsMarkedAsRead += _ => readEvents++;

            service.AddNotification(
                "Riot News",
                "New dev update",
                category: NotificationCategory.News,
                newsArticleUrl: "https://www.leagueoflegends.com/en-us/news/dev/new-dev-update");

            NotificationModel notification = Assert.Single(service.GetNotifications());
            service.ExecuteNotificationAction(notification);
            Assert.Equal(1, readEvents);

            service.ExecuteNotificationAction(notification);
            Assert.Equal(1, readEvents);
        }

        [Fact]
        public void DismissingNewsNotificationRaisesReadEventOnceAndRemovesIt()
        {
            var service = CreateService();
            int readEvents = 0;
            service.NotificationsMarkedAsRead += _ => readEvents++;

            service.AddNotification(
                "Riot News",
                "New dev update",
                category: NotificationCategory.News,
                newsArticleUrl: "https://www.leagueoflegends.com/en-us/news/dev/new-dev-update");

            NotificationModel notification = Assert.Single(service.GetNotifications());
            service.RemoveNotification(notification);
            Assert.Equal(1, readEvents);
            Assert.True(notification.IsRead);
            Assert.Empty(service.GetNotifications());
        }

        [Fact]
        public void DismissingNonNewsNotificationDoesNotRaiseReadEvent()
        {
            var service = CreateService();
            int readEvents = 0;
            service.NotificationsMarkedAsRead += _ => readEvents++;

            service.AddNotification("System", "System message");

            NotificationModel notification = Assert.Single(service.GetNotifications());
            service.RemoveNotification(notification);
            Assert.Equal(0, readEvents);
            Assert.Empty(service.GetNotifications());
        }

        [Fact]
        public void ClearingNewsCategoryRaisesReadEventForEachNewsArticle()
        {
            var service = CreateService();
            int readEvents = 0;
            service.NotificationsMarkedAsRead += _ => readEvents++;

            service.AddNotification(
                "Riot News",
                "New dev update",
                category: NotificationCategory.News,
                newsArticleUrl: "https://www.leagueoflegends.com/en-us/news/dev/new-dev-update");
            service.AddNotification(
                "Riot News",
                "New patch notes",
                category: NotificationCategory.News);
            service.AddNotification("System", "System message");

            service.RemoveAllByCategory(NotificationCategory.News);

            Assert.Equal(1, readEvents);
            NotificationModel remaining = Assert.Single(service.GetNotifications());
            Assert.Equal(NotificationCategory.System, remaining.Category);
        }

        private NotificationService CreateService()
        {
            return new NotificationService(_directories, _logService);
        }

        public void Dispose()
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, true);
            }
        }
    }
}
