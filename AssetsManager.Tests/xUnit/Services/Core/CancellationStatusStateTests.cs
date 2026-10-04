using System;
using AssetsManager.Services.Core;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Core
{
    public sealed class CancellationStatusStateTests
    {
        [Theory]
        [InlineData("Loading WADs...")]
        [InlineData("Ready")]
        [InlineData("")]
        public void CancellationCannotBeOverwrittenFor1500Milliseconds(string incoming)
        {
            var clock = new ManualClock();
            var status = new CancellationStatusState(clock);
            status.Begin();
            Assert.True(status.Allows("Cancelling Task..."));
            Assert.False(status.Allows(incoming));
            clock.Advance(1499);
            Assert.False(status.Allows(incoming));
            Assert.Equal(1, status.RemainingMilliseconds);
            clock.Advance(1);
            Assert.True(status.Allows(incoming));
        }

        [Fact]
        public void RepeatedCancellationRestartsTheVisibilityPeriod()
        {
            var clock = new ManualClock();
            var status = new CancellationStatusState(clock);
            status.Begin();
            clock.Advance(1000);
            status.Begin();
            clock.Advance(500);
            Assert.False(status.Allows("Ready"));
            clock.Advance(1000);
            Assert.True(status.Allows("Ready"));
        }

        [Fact]
        public void IdleStatusDoesNotBlockNormalProgress()
        {
            var status = new CancellationStatusState(new ManualClock());
            Assert.False(status.IsActive);
            Assert.True(status.Allows("Extracting assets..."));
        }

        private sealed class ManualClock : TimeProvider
        {
            private long _milliseconds;
            public override long TimestampFrequency => 1000;
            public override long GetTimestamp() => _milliseconds;
            public void Advance(long milliseconds) => _milliseconds += milliseconds;
        }
    }
}
