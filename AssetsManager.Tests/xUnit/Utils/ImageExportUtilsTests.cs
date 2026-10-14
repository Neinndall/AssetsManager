using System;
using AssetsManager.Utils;
using Xunit;

namespace AssetsManager.Tests.xUnit.Utils
{
    public class ImageExportUtilsTests
    {
        [Fact]
        public void GetEstimatedBitmapBytes_UsesFourBytesPerPixel()
        {
            Assert.Equal(96_000_000, ImageExportUtils.GetEstimatedBitmapBytes(1200, 20_000));
        }

        [Fact]
        public void ValidateDimensions_RejectsUnsafeBitmapSize()
        {
            Assert.Throws<InvalidOperationException>(() => ImageExportUtils.ValidateDimensions(100_000, 100_000));
        }

        [Fact]
        public void ValidateDimensions_RejectsEmptyImage()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ImageExportUtils.ValidateDimensions(0, 100));
        }
    }
}
