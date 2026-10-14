using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Settings;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using ImagePoint = SixLabors.ImageSharp.Point;

namespace AssetsManager.Utils
{
    public static class ImageExportUtils
    {
        private const long MaxBitmapBytes = 1024L * 1024 * 1024;

        public static async Task SaveAsPngAsync(
            FrameworkElement element,
            string filePath,
            LogService logService,
            CancellationToken cancellationToken = default)
        {
            if (element.ActualWidth <= 0 || element.DesiredSize.Height <= 0)
            {
                logService.LogWarning("The size of the off-screen control for PNG capture is invalid.");
                return;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var renderWidth = (int)element.ActualWidth;
                var renderHeight = (int)element.DesiredSize.Height;
                ValidateDimensions(renderWidth, renderHeight);

                RenderTargetBitmap rtb = new RenderTargetBitmap(renderWidth, renderHeight, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(element);
                rtb.Freeze();
                cancellationToken.ThrowIfCancellationRequested();

                await SaveBitmapAsPngAsync(rtb, filePath, cancellationToken);

                logService.LogInteractiveSuccess($"Saved as PNG to {Path.GetFileName(filePath)}", filePath, Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                logService.LogError(ex, $"Failed to save data as PNG to {filePath}.");
                throw; // Re-throw the exception to be caught by the calling method
            }
        }

        public static async Task SaveBitmapAsPngAsync(
            BitmapSource bitmap,
            string filePath,
            CancellationToken cancellationToken = default)
        {
            await SaveBitmapAsImageAsync(bitmap, filePath, ImageExportFormat.Png, cancellationToken);
        }

        public static async Task SaveBitmapAsImageAsync(
            BitmapSource bitmap,
            string filePath,
            ImageExportFormat format,
            CancellationToken cancellationToken = default)
        {
            if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));
            ValidateDimensions(bitmap.PixelWidth, bitmap.PixelHeight);

            BitmapSource exportBitmap = bitmap;
            if (!exportBitmap.IsFrozen)
            {
                exportBitmap = bitmap.Clone();
                exportBitmap.Freeze();
            }

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                BitmapEncoder encoder = format == ImageExportFormat.Jpeg
                    ? new JpegBitmapEncoder { QualityLevel = 90 }
                    : new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
                encoder.Frames.Add(BitmapFrame.Create(exportBitmap));
                using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
                encoder.Save(stream);
            }, cancellationToken);
        }

        public static long GetEstimatedBitmapBytes(int width, int height) => checked((long)width * height * 4);

        public static void ValidateDimensions(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
            long estimatedBytes = GetEstimatedBitmapBytes(width, height);
            long availableMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            long safeLimit = availableMemory > 0 ? Math.Min(MaxBitmapBytes, availableMemory / 2) : MaxBitmapBytes;
            if (estimatedBytes > safeLimit)
                throw new InvalidOperationException($"The requested image requires approximately {estimatedBytes / (1024 * 1024)} MB and exceeds the safe export limit.");
        }

        // The wipe/fade transition has no time budget of its own: the total stays exactly
        // holds x duration and every transition is paid with half its length taken from
        // the previous state and half from the next one.
        public const double TimelineTransitionDuration = 0.5;

        // Forward: each cycle is OLD -> transition -> NEW, joined by short reverse
        // transitions, so holds = 2 x cycles. Round trip: OLD + cycles x (up + NEW + down)
        // + OLD, so holds = cycles + 2. Either way the total is an exact multiple of the
        // configured hold time.
        public static double TimelineTotalDuration(double holdDuration, bool roundTrip, int cycles)
        {
            cycles = Math.Max(1, cycles);
            int holds = roundTrip ? cycles + 2 : 2 * cycles;
            return holds * holdDuration;
        }

        // Maps sweep progress (0-1) to the transition progress (0-1). States hold for the
        // configured duration while transitions are short and folded into the state
        // boundaries. Shared by live playback and GIF encoding so the exported file
        // always matches the on-screen sequence.
        public static double TimelineProgress(double sweep, bool roundTrip, int cycles, double holdDuration)
        {
            sweep = Math.Max(0, Math.Min(1, sweep));
            cycles = Math.Max(1, cycles);
            double T = TimelineTransitionDuration;
            double total = TimelineTotalDuration(holdDuration, roundTrip, cycles);
            double pos = sweep * total;

            // Holds touching a single transition give up half of it; holds between two
            // transitions give up one full transition
            double edgeHold = holdDuration - T / 2;
            double innerHold = holdDuration - T;

            if (!roundTrip)
            {
                // Forward: OLD hold, sweep to NEW, then reverse sweep back to OLD,
                // repeated for each cycle
                if (pos <= edgeHold) return 0;
                pos -= edgeHold;
                for (int k = 0; k < cycles; k++)
                {
                    if (pos <= T) return pos / T;             // sweep to NEW
                    pos -= T;
                    double hold = k == cycles - 1 ? edgeHold : innerHold;
                    if (pos <= hold) return 1;                // NEW hold
                    pos -= hold;
                    if (k == cycles - 1) return 1;
                    if (pos <= T) return 1 - pos / T;         // reverse sweep to OLD
                    pos -= T;
                    if (pos <= innerHold) return 0;           // OLD hold
                    pos -= innerHold;
                }
                return 1;
            }

            // Round trip: OLD hold, then cycles of (up, NEW hold, down), then OLD hold
            if (pos <= edgeHold) return 0;
            pos -= edgeHold;
            for (int k = 0; k < cycles; k++)
            {
                if (pos <= T) return pos / T;                 // up to NEW
                pos -= T;
                if (pos <= innerHold) return 1;               // NEW hold
                pos -= innerHold;
                if (pos <= T) return 1 - pos / T;             // down to OLD
                pos -= T;
            }
            return 0;                                         // final OLD hold
        }

        public static (byte[] oldPixels, int oldW, int oldH, byte[] newPixels, int newW, int newH, int width, int height) PrepareGifPixels(
            BitmapSource oldSource, BitmapSource newSource, int maxDimension)
        {
            var oldFull = new FormatConvertedBitmap(oldSource, PixelFormats.Bgra32, null, 0);
            var newFull = new FormatConvertedBitmap(newSource, PixelFormats.Bgra32, null, 0);

            var (oldBmp, newBmp) = FitToMaxDimension(oldFull, newFull, maxDimension);

            byte[] oldPixels = new byte[oldBmp.PixelWidth * oldBmp.PixelHeight * 4];
            byte[] newPixels = new byte[newBmp.PixelWidth * newBmp.PixelHeight * 4];
            oldBmp.CopyPixels(oldPixels, oldBmp.PixelWidth * 4, 0);
            newBmp.CopyPixels(newPixels, newBmp.PixelWidth * 4, 0);

            int width = Math.Max(oldBmp.PixelWidth, newBmp.PixelWidth);
            int height = Math.Max(oldBmp.PixelHeight, newBmp.PixelHeight);

            return (oldPixels, oldBmp.PixelWidth, oldBmp.PixelHeight, newPixels, newBmp.PixelWidth, newBmp.PixelHeight, width, height);
        }

        public static Task SaveAsGifSequenceAsync(
            byte[] oldPixels, int oldW, int oldH, byte[] newPixels, int newW, int newH,
            int width, int height, int frameCount, int fps, double holdDuration, bool wipe, bool roundTrip, int cycles,
            string filePath, Image<Bgra32> oldBadge = null, Image<Bgra32> newBadge = null,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateDimensions(width, height);
                EncodeGifSequence(oldPixels, oldW, oldH, newPixels, newW, newH, width, height, frameCount, fps, holdDuration, wipe, roundTrip, cycles, filePath, oldBadge, newBadge);
            }, cancellationToken);
        }

        private static (BitmapSource oldScaled, BitmapSource newScaled) FitToMaxDimension(BitmapSource oldImg, BitmapSource newImg, int maxDimension)
        {
            BitmapSource ScaleIfNeeded(BitmapSource bmp)
            {
                int w = bmp.PixelWidth, h = bmp.PixelHeight;
                if (maxDimension <= 0 || (w <= maxDimension && h <= maxDimension)) return bmp;
                double scale = Math.Min(maxDimension / (double)w, maxDimension / (double)h);
                return new FormatConvertedBitmap(new TransformedBitmap(bmp, new ScaleTransform(scale, scale)), PixelFormats.Bgra32, null, 0);
            }

            return (ScaleIfNeeded(oldImg), ScaleIfNeeded(newImg));
        }

        private static void EncodeGifSequence(byte[] oldPixels, int oldW, int oldH, byte[] newPixels, int newW, int newH,
            int width, int height, int frameCount, int fps, double holdDuration, bool wipe, bool roundTrip, int cycles, string path,
            Image<Bgra32> oldBadge, Image<Bgra32> newBadge)
        {
            int delay = (int)Math.Round(100.0 / fps);

            using var oldImage = Image.LoadPixelData<Bgra32>(oldPixels, oldW, oldH);
            using var newImage = Image.LoadPixelData<Bgra32>(newPixels, newW, newH);

            using var gif = BuildSequenceFrame(oldImage, newImage, width, height, 0, holdDuration, wipe, roundTrip, cycles, oldBadge, newBadge);
            for (int i = 1; i < frameCount; i++)
            {
                double progress = frameCount > 1 ? i / (double)(frameCount - 1) : 1.0;
                using var frameImage = BuildSequenceFrame(oldImage, newImage, width, height, progress, holdDuration, wipe, roundTrip, cycles, oldBadge, newBadge);
                gif.Frames.AddFrame(frameImage.Frames.RootFrame);
            }

            // Uniform frame delay (centiseconds) for a constant sequence speed
            foreach (var frame in gif.Frames)
            {
                SixLabors.ImageSharp.MetadataExtensions.GetGifMetadata(frame.Metadata).FrameDelay = delay;
            }

            // Floyd-Steinberg dithering smooths the gradient banding inherent to the 256-color palette
            var encoder = new GifEncoder
            {
                Quantizer = new OctreeQuantizer(new QuantizerOptions { Dither = KnownDitherings.FloydSteinberg, MaxColors = 256 })
            };
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            SixLabors.ImageSharp.ImageExtensions.SaveAsGif(gif, stream, encoder);
        }

        // Renders one GIF frame on the shared canvas: HUD background, OLD underneath and NEW
        // with the selected wipe/fade transition, plus the OLD/NEW badges on top.
        // ImageSharp handles all alpha compositing.
        private static Image<Bgra32> BuildSequenceFrame(Image<Bgra32> oldImage, Image<Bgra32> newImage,
            int width, int height, double progress, double holdDuration, bool wipe, bool roundTrip, int cycles,
            Image<Bgra32> oldBadge = null, Image<Bgra32> newBadge = null)
        {
            var frame = new Image<Bgra32>(width, height, new Bgra32(24, 26, 30, 255));

            // OLD centered underneath
            frame.Mutate(x => x.DrawImage(oldImage, new ImagePoint((width - oldImage.Width) / 2, (height - oldImage.Height) / 2), 1f));

            double t = TimelineProgress(progress, roundTrip, cycles, holdDuration);
            if (t > 0)
            {
                // NEW centered with the selected transition
                int newOx = (width - newImage.Width) / 2;
                int newOy = (height - newImage.Height) / 2;
                if (wipe)
                {
                    // Wipe: reveal NEW over OLD horizontally
                    int revealW = (int)Math.Round(width * t);
                    if (revealW > 0)
                    {
                        using var strip = newImage.Clone(x => x.Crop(new Rectangle(0, 0, Math.Min(revealW, newImage.Width), newImage.Height)));
                        frame.Mutate(x => x.DrawImage(strip, new ImagePoint(newOx, newOy), 1f));
                    }
                }
                else
                {
                    // Fade: NEW fades in over OLD
                    frame.Mutate(x => x.DrawImage(newImage, new ImagePoint(newOx, newOy), (float)t));
                }
            }

            DrawBadges(frame, oldBadge, newBadge, width, height, t);
            return frame;
        }

        private static void DrawBadges(Image<Bgra32> frame, Image<Bgra32> oldBadge, Image<Bgra32> newBadge, int width, int height, double t)
        {
            if (oldBadge == null || newBadge == null) return;

            // Only the badge of the dominant image is shown: OLD below 50%, NEW above
            Image<Bgra32> badge = t < 0.5 ? oldBadge : newBadge;
            bool topLeft = t < 0.5;

            int margin = Math.Max(8, (int)Math.Round(width * 0.015));
            int badgeH = Math.Max(18, (int)Math.Round(height * 0.055));
            int badgeW = Math.Max(1, (int)Math.Round(badge.Width * (badgeH / (double)badge.Height)));

            using var scaled = badge.Clone(x => x.Resize(badgeW, badgeH));
            int posX = topLeft ? margin : width - margin - badgeW;
            frame.Mutate(x => x.DrawImage(scaled, new ImagePoint(posX, margin), 1f));
        }

        // Renders the on-screen OLD/NEW badge chips (WPF elements) into ImageSharp images so the
        // exported GIF matches the viewport labels exactly. Must run on the UI thread.
        public static (Image<Bgra32> Old, Image<Bgra32> New) RenderGifBadges(FrameworkElement oldBadge, FrameworkElement newBadge)
        {
            return (RenderBadgeElement(oldBadge), RenderBadgeElement(newBadge));
        }

        private static Image<Bgra32> RenderBadgeElement(FrameworkElement badge)
        {
            Visibility previous = badge.Visibility;
            badge.Visibility = Visibility.Hidden;
            badge.UpdateLayout();

            var rtb = new RenderTargetBitmap(
                Math.Max(1, (int)badge.ActualWidth), Math.Max(1, (int)badge.ActualHeight),
                96, 96, PixelFormats.Pbgra32);
            rtb.Render(badge);
            badge.Visibility = previous;
            rtb.Freeze();

            // Unpremultiply alpha (Pbgra32 -> Bgra32) so ImageSharp composites correctly
            var bgra = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
            bgra.Freeze();
            byte[] pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
            bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
            return Image.LoadPixelData<Bgra32>(pixels, bgra.PixelWidth, bgra.PixelHeight);
        }
    }
}
