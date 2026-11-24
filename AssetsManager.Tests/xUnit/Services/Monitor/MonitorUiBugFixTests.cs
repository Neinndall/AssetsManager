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
    }
}
