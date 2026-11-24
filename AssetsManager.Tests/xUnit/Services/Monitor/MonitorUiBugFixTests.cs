using System;
using AssetsManager.Views.Controls.Monitor;
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
    }
}
