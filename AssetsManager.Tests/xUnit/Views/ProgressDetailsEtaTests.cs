using System;
using AssetsManager.Views.Dialogs;
using Xunit;

namespace AssetsManager.Tests.xUnit.Views
{
    public sealed class ProgressDetailsEtaTests
    {
        [Fact]
        public void DetectsPhaseTransitionFromVerifyingToUpdatingAndCounterReset()
        {
            // Phase 1 (Verifying 100/100) -> Phase 2 (Updating 1/500)
            Assert.True(ProgressDetailsWindow.IsNewProgressPhase(100, 100, "Verifying", 1, 500, "Updating"));

            // Same verb but counter restarted for a new batch
            Assert.True(ProgressDetailsWindow.IsNewProgressPhase(50, 50, "Updating", 1, 200, "Updating"));

            // Normal monotonic progress within the same phase
            Assert.False(ProgressDetailsWindow.IsNewProgressPhase(10, 100, "Updating", 11, 100, "Updating"));
        }

        [Fact]
        public void FormatDurationPreservesHoursBeyond24HoursAndClampsNegatives()
        {
            Assert.Equal("00:00:00", ProgressDetailsWindow.FormatDuration(TimeSpan.FromSeconds(-5)));
            Assert.Equal("00:00:00", ProgressDetailsWindow.FormatDuration(TimeSpan.Zero));
            Assert.Equal("02:05:09", ProgressDetailsWindow.FormatDuration(new TimeSpan(2, 5, 9)));
            Assert.Equal("26:15:30", ProgressDetailsWindow.FormatDuration(new TimeSpan(1, 2, 15, 30)));
        }
    }
}
