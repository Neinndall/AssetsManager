using System;
using System.IO;
using System.Linq;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Notifications;
using Serilog;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Core
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
                NotificationCategory.Comparator,
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
