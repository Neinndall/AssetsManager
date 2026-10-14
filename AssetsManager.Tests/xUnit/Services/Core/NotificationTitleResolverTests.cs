using AssetsManager.Utils;
using AssetsManager.Views.Models.News;
using AssetsManager.Views.Models.Notifications;
using System.Text.Json;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Core
{
    public sealed class NotificationTitleResolverTests
    {
        [Fact]
        public void ResolvesSystemTitleWhenMinimizedToTray()
        {
            string title = NotificationTitleResolver.ResolveToastTitle(
                "The application has been minimized to the tray.",
                NotificationCategory.System,
                "System");

            Assert.Equal("System", title);
        }

        [Fact]
        public void ResolvesPbeMaintenanceTitleWhenUndergoingMaintenance()
        {
            string title = NotificationTitleResolver.ResolveToastTitle(
                "PBE is currently undergoing maintenance until 14:00",
                NotificationCategory.System,
                "PBE Status Update");

            Assert.Equal("PBE Maintenance", title);
        }

        [Fact]
        public void ResolvesPbeOnlineTitleWhenMaintenanceEnded()
        {
            string title = NotificationTitleResolver.ResolveToastTitle(
                "PBE Status: Maintenance ended.",
                NotificationCategory.System,
                "PBE Status Update");

            Assert.Equal("PBE Server Online", title);
        }

        [Fact]
        public void ResolvesAssetTrackerTitle()
        {
            string title = NotificationTitleResolver.ResolveToastTitle(
                "New assets found in Champions category",
                NotificationCategory.Tracker,
                "Asset Tracker Discovery");

            Assert.Equal("Asset Tracker", title);
        }

        [Fact]
        public void ResolvesAssetWatcherTitle()
        {
            string title = NotificationTitleResolver.ResolveToastTitle(
                "Monitored assets updated: Game/DATA/...",
                NotificationCategory.Watcher,
                "Monitored Assets Updated");

            Assert.Equal("Asset Watcher", title);
        }

        [Fact]
        public void ResolvesAppUpdateTitle()
        {
            string title = NotificationTitleResolver.ResolveToastTitle(
                "New version v4.2.0 is available!",
                NotificationCategory.Updates,
                "App Update Available");

            Assert.Equal("App Update", title);
        }

        [Fact]
        public void ResolvesHashUpdateTitle()
        {
            string title = NotificationTitleResolver.ResolveToastTitle(
                "New hashes are available!",
                NotificationCategory.Updates,
                "Hash Update");

            Assert.Equal("Hash Update", title);
        }

        [Fact]
        public void ResolvesRiotNewsTitleWithCategory()
        {
            using var doc = JsonDocument.Parse("{\"title\":\"Dev Update August 2026\",\"category\":{\"title\":\"Dev\"}}");
            var newsItem = NewsItemModel.FromJson(doc.RootElement);

            string title = NotificationTitleResolver.ResolveToastTitle(
                "Dev Update August 2026",
                NotificationCategory.News,
                "Riot News",
                newsItem);

            Assert.Equal("Riot News • Dev", title);
        }

        [Fact]
        public void ResolvesFallbackTitleForGenericSystemNotification()
        {
            string title = NotificationTitleResolver.ResolveToastTitle(
                "Some generic notification",
                NotificationCategory.System,
                null);

            Assert.Equal("AssetsManager", title);
        }
    }
}
