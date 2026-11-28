using AssetsManager.Services.Formatting;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Formatting
{
    public sealed class WindowsVideoTranscoderComInitTests
    {
        [Theory]
        [InlineData(0, true, true)]                  // S_OK
        [InlineData(1, true, true)]                  // S_FALSE
        [InlineData(-2147417850, true, false)]       // RPC_E_CHANGED_MODE (0x80010106)
        [InlineData(-2147024882, false, false)]      // E_OUTOFMEMORY
        public void TryEvaluateComInitResultHandlesStaModeWithoutUninitializing(int hr, bool expectedOk, bool expectedUninit)
        {
            bool ok = WindowsVideoTranscoder.TryEvaluateComInitResult(hr, out bool shouldUninitialize);

            Assert.Equal(expectedOk, ok);
            Assert.Equal(expectedUninit, shouldUninitialize);
        }
    }
}
