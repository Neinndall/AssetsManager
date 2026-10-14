using AssetsManager.Services.Viewer.Vfx.Rendering;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxGpuResourceUploaderTests
    {
        [Fact]
        public void UploadBudgetAlwaysAllowsOneOversizedResource()
        {
            var budget = new VfxGpuResourceUploader.UploadBudget(4, 1024);

            Assert.True(budget.TryTake(4096));
            Assert.False(budget.TryTake(1));
        }

        [Fact]
        public void UploadBudgetStopsAtByteLimitAfterFirstResource()
        {
            var budget = new VfxGpuResourceUploader.UploadBudget(4, 1024);

            Assert.True(budget.TryTake(768));
            Assert.False(budget.TryTake(512));
        }

        [Fact]
        public void UploadBudgetStopsAtUploadCountLimit()
        {
            var budget = new VfxGpuResourceUploader.UploadBudget(2, 4096);

            Assert.True(budget.TryTake(256));
            Assert.True(budget.TryTake(256));
            Assert.False(budget.TryTake(256));
        }
    }
}
