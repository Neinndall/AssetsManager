using System;
using AssetsManager.Services.Monitor;
using AssetsManager.Views.Controls.Monitor;
using AssetsManager.Views.Models.Home;
using AssetsManager.Views.Models.Monitor;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Monitor
{
    public sealed class MonitorUiBugFixTests
    {
        [Fact]
        public void UncheckedMonitoredAssetDisplaysPlaceholderInsteadOfYear0001()
        {
            var asset = new MonitoredAsset();

            Assert.Equal(0UL, asset.LastKnownHash);
            Assert.Equal("Not checked yet", asset.FormattedLastUpdated);
            Assert.DoesNotContain("0001", asset.FormattedLastUpdated, StringComparison.Ordinal);

            var checkedAt = new DateTime(2026, 11, 24, 15, 30, 0);
            asset.LastUpdated = checkedAt;

            Assert.Equal(checkedAt.ToString("dd MMM yyyy · HH:mm"), asset.FormattedLastUpdated);
        }

        [Fact]
        public void SaleInfoFormatsDiscountInEnglish()
        {
            var sale = new SaleInfo { Rp = 675, PercentOff = 50 };

            Assert.Equal("Sale: 675 RP (50% off)", sale.FormattedSaleDetails);
        }

        [Fact]
        public void PassRewardBlueEssenceTitleFormatsInEnglish()
        {
            Assert.Equal("1000 Blue Essence", ApiControl.TransformTitle("rewards_title_Reward_BE_1000", 1));
            Assert.Equal("Regular Reward", ApiControl.TransformTitle("Regular Reward", 1));
        }

        [Fact]
        public void HistoryEntryRenameUpdatesDisplayNameAndNotifiesBindings()
        {
            var entry = new HistoryEntry
            {
                DisplayName = "League of Legends (PBE)",
                FileName = "PBE_16_19"
            };
            var notified = new System.Collections.Generic.List<string>();
            entry.PropertyChanged += (_, args) => notified.Add(args.PropertyName);

            Assert.False(entry.Rename("   "));
            Assert.False(entry.Rename("League of Legends (PBE)"));

            Assert.True(entry.Rename("  Patch 16.20 Snapshot  "));
            Assert.Equal("Patch 16.20 Snapshot", entry.DisplayName);
            Assert.Equal("Patch 16.20 Snapshot", entry.FileName);
            Assert.Contains(nameof(HistoryEntry.DisplayName), notified);
            Assert.Contains(nameof(HistoryEntry.FileName), notified);
        }

        [Fact]
        public void PbeMaintenanceStatusDistinguishesFutureAndPastAndCapitalizesDashboardSubtitle()
        {
            var now = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
            var futureStart = now.AddHours(2);
            var pastStart = now.AddHours(-1);

            string futureStatus = PbeStatusService.FormatMaintenanceConciseStatus(futureStart, now);
            string pastStatus = PbeStatusService.FormatMaintenanceConciseStatus(pastStart, now);

            Assert.StartsWith("Maintenance starts at ", futureStatus, StringComparison.Ordinal);
            Assert.StartsWith("Maintenance started at ", pastStatus, StringComparison.Ordinal);
            Assert.StartsWith("Starts at ", MonitorDashboardModel.FormatPbeMaintenanceSubtitle(futureStatus), StringComparison.Ordinal);
            Assert.StartsWith("Started at ", MonitorDashboardModel.FormatPbeMaintenanceSubtitle(pastStatus), StringComparison.Ordinal);
            Assert.Equal("Detected", MonitorDashboardModel.FormatPbeMaintenanceSubtitle("Maintenance detected"));
        }

        [Fact]
        public void ConverterCanConvertReflectsItemCountAndProcessingState()
        {
            var model = new ConverterModel();
            Assert.False(model.CanConvert);

            model.Items.Add(new ConverterItem { FileName = "skin.tex", FileType = ConverterFileType.Image });
            Assert.True(model.CanConvert);

            model.IsProcessing = true;
            Assert.False(model.CanConvert);

            model.IsProcessing = false;
            Assert.True(model.CanConvert);

            model.Items.Clear();
            Assert.False(model.CanConvert);
        }
    }
}
