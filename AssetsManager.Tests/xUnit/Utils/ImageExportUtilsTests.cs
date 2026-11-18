using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using AssetsManager.Utils;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
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

        [Fact]
        public void RenderTimelineBadges_ProducesVisibleOpaquePixels()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var (oldBadge, newBadge) = ImageExportUtils.RenderTimelineBadges(
                        Brushes.Orange,
                        Brushes.LimeGreen,
                        new FontFamily("Segoe UI"));
                    using (oldBadge)
                    using (newBadge)
                    {
                        Assert.True(oldBadge.Width > 10 && oldBadge.Height > 10);
                        Assert.True(newBadge.Width > 10 && newBadge.Height > 10);
                        Assert.True(HasOpaquePixels(oldBadge));
                        Assert.True(HasOpaquePixels(newBadge));
                    }
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        [Theory]
        [InlineData(TimelineTransitionEffect.Wipe)]
        [InlineData(TimelineTransitionEffect.Fade)]
        [InlineData(TimelineTransitionEffect.Cut)]
        public async Task SaveTimelineSequenceAsync_GifPreventsOldBleedThroughAndDrawsBadges(TimelineTransitionEffect effect)
        {
            // OLD: 128x64 solid red with alpha; NEW: 64x64 transparent except center 32x32 blue
            byte[] oldPixels = CreateSolidBgra(128, 64, 0, 0, 255, 255);
            byte[] newPixels = CreateCenteredSquareBgra(64, 64, 32, 255, 0, 0, 255);

            using var oldBadge = new Image<Bgra32>(40, 20, new Bgra32(255, 145, 0, 255));
            using var newBadge = new Image<Bgra32>(40, 20, new Bgra32(0, 230, 118, 255));

            string tempPath = Path.Combine(Path.GetTempPath(), $"timeline_test_{Guid.NewGuid():N}.gif");
            try
            {
                var options = new TimelineExportOptions(
                    TimelineExportFormat.Gif,
                    effect,
                    RoundTrip: false,
                    Cycles: 1,
                    HoldDuration: 1.0,
                    Fps: 12,
                    TargetSize: 128,
                    UpscaleSmallImages: false,
                    TimelineBackgroundMode.Light);

                await ImageExportUtils.SaveTimelineSequenceAsync(
                    oldPixels, 128, 64,
                    newPixels, 64, 64,
                    options,
                    tempPath,
                    oldBadge,
                    newBadge);

                Assert.True(File.Exists(tempPath));
                using var gif = await Image.LoadAsync<Bgra32>(tempPath);
                Assert.True(gif.Frames.Count >= 12);

                // First frame should match Light background outside OLD + OLD badge on top-left
                var firstFrame = gif.Frames[0];
                // Final frame (100% NEW) must have ZERO red pixels from OLD anywhere on the canvas
                var lastFrame = gif.Frames[gif.Frames.Count - 1];
                for (int y = 0; y < lastFrame.Height; y++)
                {
                    for (int x = 0; x < lastFrame.Width; x++)
                    {
                        Bgra32 px = lastFrame[x, y];
                        Assert.False(px.R > 200 && px.G < 50 && px.B < 50, $"OLD red pixel leaked at ({x},{y}) in final frame.");
                    }
                }

                // In Wipe (left-to-right), NEW reveals on the left and OLD stays on the right.
                int oldBadgeX = effect == TimelineTransitionEffect.Wipe ? firstFrame.Width / 2 : 0;
                int newBadgeX = effect == TimelineTransitionEffect.Wipe ? 0 : lastFrame.Width / 2;

                Assert.True(RegionHasColor(firstFrame, oldBadgeX, 0, firstFrame.Width / 2, firstFrame.Height / 3, p => p.R > 200 && p.G > 100 && p.B < 50));
                Assert.True(RegionHasColor(lastFrame, newBadgeX, 0, lastFrame.Width / 2, lastFrame.Height / 3, p => p.G > 180 && p.R < 80));
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        [Fact]
        public async Task SaveTimelineSequenceAsync_Mp4ProducesValidVideoWithUpscale()
        {
            byte[] oldPixels = CreateSolidBgra(64, 64, 0, 0, 220, 255);
            byte[] newPixels = CreateSolidBgra(64, 64, 220, 80, 0, 255);

            using var oldBadge = new Image<Bgra32>(40, 20, new Bgra32(255, 145, 0, 255));
            using var newBadge = new Image<Bgra32>(40, 20, new Bgra32(0, 230, 118, 255));

            string tempPath = Path.Combine(Path.GetTempPath(), $"timeline_test_{Guid.NewGuid():N}.mp4");
            try
            {
                var options = new TimelineExportOptions(
                    TimelineExportFormat.Mp4,
                    TimelineTransitionEffect.Wipe,
                    RoundTrip: false,
                    Cycles: 1,
                    HoldDuration: 1.0,
                    Fps: 24,
                    TargetSize: 256,
                    UpscaleSmallImages: true,
                    TimelineBackgroundMode.Dark);

                await ImageExportUtils.SaveTimelineSequenceAsync(
                    oldPixels, 64, 64,
                    newPixels, 64, 64,
                    options,
                    tempPath,
                    oldBadge,
                    newBadge);

                Assert.True(File.Exists(tempPath));
                var info = new FileInfo(tempPath);
                Assert.True(info.Length > 1024, "Expected non-trivial H.264 MP4 output.");
                byte[] header = await File.ReadAllBytesAsync(tempPath);
                // MP4 'ftyp' box at bytes 4..7
                Assert.Equal((byte)'f', header[4]);
                Assert.Equal((byte)'t', header[5]);
                Assert.Equal((byte)'y', header[6]);
                Assert.Equal((byte)'p', header[7]);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        private static bool HasOpaquePixels(Image<Bgra32> img)
        {
            for (int y = 0; y < img.Height; y++)
                for (int x = 0; x < img.Width; x++)
                    if (img[x, y].A > 200) return true;
            return false;
        }

        private static bool RegionHasColor(ImageFrame<Bgra32> frame, int startX, int startY, int w, int h, Func<Bgra32, bool> predicate)
        {
            int endX = Math.Min(frame.Width, startX + w);
            int endY = Math.Min(frame.Height, startY + h);
            for (int y = startY; y < endY; y++)
                for (int x = startX; x < endX; x++)
                    if (predicate(frame[x, y])) return true;
            return false;
        }

        private static byte[] CreateSolidBgra(int width, int height, byte b, byte g, byte r, byte a)
        {
            byte[] pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = a;
            }
            return pixels;
        }

        private static byte[] CreateCenteredSquareBgra(int width, int height, int squareSize, byte b, byte g, byte r, byte a)
        {
            byte[] pixels = new byte[width * height * 4];
            int minX = (width - squareSize) / 2;
            int minY = (height - squareSize) / 2;
            for (int y = minY; y < minY + squareSize; y++)
            {
                for (int x = minX; x < minX + squareSize; x++)
                {
                    int i = (y * width + x) * 4;
                    pixels[i] = b;
                    pixels[i + 1] = g;
                    pixels[i + 2] = r;
                    pixels[i + 3] = a;
                }
            }
            return pixels;
        }
    }
}
