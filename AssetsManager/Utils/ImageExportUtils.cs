using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using AssetsManager.Services.Formatting;
using AssetsManager.Views.Models.Settings;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using ImagePoint = SixLabors.ImageSharp.Point;
using WpfBorder = System.Windows.Controls.Border;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using WpfSize = System.Windows.Size;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace AssetsManager.Utils
{
    public enum TimelineTransitionEffect
    {
        Wipe,
        Fade,
        Cut
    }

    public enum TimelineBackgroundMode
    {
        Dark,
        Checkerboard,
        Light
    }

    public enum TimelineExportFormat
    {
        Mp4,
        Gif
    }

    public sealed record TimelineExportOptions(
        TimelineExportFormat Format,
        TimelineTransitionEffect Effect,
        bool RoundTrip,
        int Cycles,
        double HoldDuration,
        int Fps,
        int TargetSize,
        bool UpscaleSmallImages,
        TimelineBackgroundMode BackgroundMode,
        WpfRect? NormalizedCropRect = null);

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

        public static (byte[] oldPixels, int oldW, int oldH, byte[] newPixels, int newW, int newH) PrepareTimelineSourcePixels(
            BitmapSource oldSource, BitmapSource newSource)
        {
            var oldFull = new FormatConvertedBitmap(oldSource, PixelFormats.Bgra32, null, 0);
            var newFull = new FormatConvertedBitmap(newSource, PixelFormats.Bgra32, null, 0);
            oldFull.Freeze();
            newFull.Freeze();

            byte[] oldPixels = new byte[oldFull.PixelWidth * oldFull.PixelHeight * 4];
            byte[] newPixels = new byte[newFull.PixelWidth * newFull.PixelHeight * 4];
            oldFull.CopyPixels(oldPixels, oldFull.PixelWidth * 4, 0);
            newFull.CopyPixels(newPixels, newFull.PixelWidth * 4, 0);

            return (oldPixels, oldFull.PixelWidth, oldFull.PixelHeight, newPixels, newFull.PixelWidth, newFull.PixelHeight);
        }

        public static Task SaveTimelineSequenceAsync(
            byte[] oldPixels, int oldW, int oldH,
            byte[] newPixels, int newW, int newH,
            TimelineExportOptions options,
            string filePath,
            Image<Bgra32> oldBadge = null,
            Image<Bgra32> newBadge = null,
            IProgress<(int Completed, int Total)> progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            return Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var oldSrc = Image.LoadPixelData<Bgra32>(oldPixels, oldW, oldH);
                using var newSrc = Image.LoadPixelData<Bgra32>(newPixels, newW, newH);
                var prepared = BuildTimelineLayers(oldSrc, newSrc, options, oldBadge, newBadge);

                ValidateDimensions(prepared.Width, prepared.Height);

                double totalSeconds = TimelineTotalDuration(options.HoldDuration, options.RoundTrip, options.Cycles);
                int frameCount = Math.Max(2, (int)Math.Ceiling(totalSeconds * options.Fps));

                if (options.Format == TimelineExportFormat.Mp4)
                {
                    var frameProgress = progress != null
                        ? new Progress<int>(completed => progress.Report((completed, frameCount)))
                        : null;

                    await WindowsVideoTranscoder.EncodeBgraSequenceAsync(
                        filePath,
                        prepared.Width,
                        prepared.Height,
                        options.Fps,
                        frameCount,
                        (frameIndex, destination) =>
                        {
                            double sweep = frameCount > 1 ? frameIndex / (double)(frameCount - 1) : 1.0;
                            WriteSequenceFrame(prepared, sweep, options, destination);
                        },
                        frameProgress,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    EncodeGifSequence(prepared, frameCount, options, filePath, progress, cancellationToken);
                }
            }, cancellationToken);
        }

        private sealed record ScaledBadgeData(byte[] Pixels, int Width, int Height);

        private sealed class PreparedTimelineCanvas
        {
            public required int Width { get; init; }
            public required int Height { get; init; }
            public required byte[] OldLayer { get; init; }
            public required byte[] NewLayer { get; init; }
            public required ScaledBadgeData ScaledOldBadge { get; init; }
            public required ScaledBadgeData ScaledNewBadge { get; init; }
            public required int BadgeMargin { get; init; }
        }

        private static PreparedTimelineCanvas BuildTimelineLayers(
            Image<Bgra32> oldSrc,
            Image<Bgra32> newSrc,
            TimelineExportOptions options,
            Image<Bgra32> oldBadge,
            Image<Bgra32> newBadge)
        {
            int stageW = Math.Max(oldSrc.Width, newSrc.Width);
            int stageH = Math.Max(oldSrc.Height, newSrc.Height);

            WpfRect crop = options.NormalizedCropRect ?? new WpfRect(0, 0, 1, 1);
            double cropX = Math.Clamp(crop.X, 0, 0.95);
            double cropY = Math.Clamp(crop.Y, 0, 0.95);
            double cropW = Math.Clamp(crop.Width, 0.05, 1.0 - cropX);
            double cropH = Math.Clamp(crop.Height, 0.05, 1.0 - cropY);

            double cropStageW = stageW * cropW;
            double cropStageH = stageH * cropH;
            double maxDim = Math.Max(cropStageW, cropStageH);

            double scale = 1.0;
            if (options.TargetSize > 0 && maxDim > 0)
            {
                if (maxDim > options.TargetSize || (options.UpscaleSmallImages && maxDim < options.TargetSize))
                {
                    scale = options.TargetSize / maxDim;
                }
            }

            int canvasW = Math.Max(2, (int)Math.Round(cropStageW * scale));
            int canvasH = Math.Max(2, (int)Math.Round(cropStageH * scale));
            if (options.Format == TimelineExportFormat.Mp4)
            {
                canvasW += canvasW & 1;
                canvasH += canvasH & 1;
            }

            ValidateDimensions(canvasW, canvasH);

            double fullStageW = stageW * scale;
            double fullStageH = stageH * scale;
            int offsetX = (int)Math.Round(cropX * fullStageW);
            int offsetY = (int)Math.Round(cropY * fullStageH);

            byte[] oldLayer = RenderLayerBytes(oldSrc, canvasW, canvasH, fullStageW, fullStageH, offsetX, offsetY, options.BackgroundMode);
            byte[] newLayer = RenderLayerBytes(newSrc, canvasW, canvasH, fullStageW, fullStageH, offsetX, offsetY, options.BackgroundMode);

            int margin = Math.Max(8, (int)Math.Round(Math.Min(canvasW, canvasH) * 0.02));
            ScaledBadgeData scaledOldBadge = ScaleBadgeForCanvas(oldBadge, canvasW, canvasH);
            ScaledBadgeData scaledNewBadge = ScaleBadgeForCanvas(newBadge, canvasW, canvasH);

            return new PreparedTimelineCanvas
            {
                Width = canvasW,
                Height = canvasH,
                OldLayer = oldLayer,
                NewLayer = newLayer,
                ScaledOldBadge = scaledOldBadge,
                ScaledNewBadge = scaledNewBadge,
                BadgeMargin = margin
            };
        }

        private static byte[] RenderLayerBytes(
            Image<Bgra32> src,
            int canvasW,
            int canvasH,
            double fullStageW,
            double fullStageH,
            int cropOffsetX,
            int cropOffsetY,
            TimelineBackgroundMode backgroundMode)
        {
            using var canvas = CreateBackgroundCanvas(canvasW, canvasH, backgroundMode);

            double fitScale = Math.Min(fullStageW / src.Width, fullStageH / src.Height);
            int drawW = Math.Max(1, (int)Math.Round(src.Width * fitScale));
            int drawH = Math.Max(1, (int)Math.Round(src.Height * fitScale));

            // Nearest-neighbor keeps small pixel-art icons/emotes crisp when upscaled for video/GIF.
            var resampler = (drawW > src.Width || drawH > src.Height)
                ? KnownResamplers.NearestNeighbor
                : KnownResamplers.Lanczos3;

            int ox = (int)Math.Round((fullStageW - drawW) / 2.0) - cropOffsetX;
            int oy = (int)Math.Round((fullStageH - drawH) / 2.0) - cropOffsetY;

            using var fitted = src.Clone(x => x.Resize(drawW, drawH, resampler));
            canvas.Mutate(x => x.DrawImage(fitted, new ImagePoint(ox, oy), 1f));

            byte[] bytes = new byte[canvasW * canvasH * 4];
            canvas.CopyPixelDataTo(bytes);
            return bytes;
        }

        private static Image<Bgra32> CreateBackgroundCanvas(int width, int height, TimelineBackgroundMode backgroundMode)
        {
            if (backgroundMode == TimelineBackgroundMode.Light)
                return new Image<Bgra32>(width, height, new Bgra32(220, 220, 220, 255));

            if (backgroundMode == TimelineBackgroundMode.Dark)
                return new Image<Bgra32>(width, height, new Bgra32(15, 17, 26, 255));

            // Checkerboard matching the WPF DrawingBrush (#2D2D30 / #3E3E42)
            var canvas = new Image<Bgra32>(width, height);
            int cellSize = Math.Max(10, Math.Min(width, height) / 32);
            var darkCell = new Bgra32(45, 45, 48, 255);
            var lightCell = new Bgra32(62, 62, 66, 255);

            canvas.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    int cy = y / cellSize;
                    for (int x = 0; x < width; x++)
                    {
                        bool alt = ((x / cellSize) + cy) % 2 == 0;
                        row[x] = alt ? darkCell : lightCell;
                    }
                }
            });
            return canvas;
        }

        private static ScaledBadgeData ScaleBadgeForCanvas(Image<Bgra32> badge, int canvasW, int canvasH)
        {
            if (badge == null) return null;
            int minDim = Math.Min(canvasW, canvasH);
            int targetH = Math.Clamp((int)Math.Round(minDim * 0.048), 20, 44);
            int targetW = Math.Max(1, (int)Math.Round(badge.Width * (targetH / (double)badge.Height)));
            if (targetW > canvasW / 3)
            {
                targetW = Math.Max(1, canvasW / 3);
                targetH = Math.Max(1, (int)Math.Round(badge.Height * (targetW / (double)badge.Width)));
            }
            using var resized = badge.Clone(x => x.Resize(targetW, targetH, KnownResamplers.Lanczos3));
            byte[] pixels = new byte[targetW * targetH * 4];
            resized.CopyPixelDataTo(pixels);
            return new ScaledBadgeData(pixels, targetW, targetH);
        }

        private static void EncodeGifSequence(
            PreparedTimelineCanvas prepared,
            int frameCount,
            TimelineExportOptions options,
            string path,
            IProgress<(int Completed, int Total)> progress,
            CancellationToken cancellationToken)
        {
            int delay = Math.Max(2, (int)Math.Round(100.0 / options.Fps));
            byte[] buffer = new byte[prepared.Width * prepared.Height * 4];

            WriteSequenceFrame(prepared, 0, options, buffer);
            using var gif = Image.LoadPixelData<Bgra32>(buffer, prepared.Width, prepared.Height);
            progress?.Report((1, frameCount));

            for (int i = 1; i < frameCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double sweep = frameCount > 1 ? i / (double)(frameCount - 1) : 1.0;
                WriteSequenceFrame(prepared, sweep, options, buffer);
                using var frameImage = Image.LoadPixelData<Bgra32>(buffer, prepared.Width, prepared.Height);
                gif.Frames.AddFrame(frameImage.Frames.RootFrame);
                progress?.Report((i + 1, frameCount));
            }

            SixLabors.ImageSharp.MetadataExtensions.GetGifMetadata(gif.Metadata).RepeatCount = 0;
            foreach (var frame in gif.Frames)
            {
                SixLabors.ImageSharp.MetadataExtensions.GetGifMetadata(frame.Metadata).FrameDelay = delay;
            }

            var encoder = new GifEncoder
            {
                Quantizer = new OctreeQuantizer(new QuantizerOptions { Dither = KnownDitherings.FloydSteinberg, MaxColors = 256 })
            };
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            SixLabors.ImageSharp.ImageExtensions.SaveAsGif(gif, stream, encoder);
        }

        private static void WriteSequenceFrame(
            PreparedTimelineCanvas prepared,
            double sweep,
            TimelineExportOptions options,
            Span<byte> destination)
        {
            int width = prepared.Width;
            int height = prepared.Height;
            int stride = width * 4;
            byte[] oldLayer = prepared.OldLayer;
            byte[] newLayer = prepared.NewLayer;

            double t = TimelineProgress(sweep, options.RoundTrip, options.Cycles, options.HoldDuration);

            switch (options.Effect)
            {
                case TimelineTransitionEffect.Wipe:
                {
                    // Sweep left to right revealing NEW: [0..cutX] = NEW, [cutX..width] = OLD.
                    int cutX = Math.Clamp((int)Math.Round(width * t), 0, width);
                    int leftBytes = cutX * 4;
                    int rightBytes = stride - leftBytes;

                    for (int y = 0; y < height; y++)
                    {
                        int rowOffset = y * stride;
                        if (leftBytes > 0)
                            newLayer.AsSpan(rowOffset, leftBytes).CopyTo(destination.Slice(rowOffset, leftBytes));
                        if (rightBytes > 0)
                            oldLayer.AsSpan(rowOffset + leftBytes, rightBytes).CopyTo(destination.Slice(rowOffset + leftBytes, rightBytes));
                    }

                    if (t > 0.001 && t < 0.999)
                    {
                        int lineThickness = Math.Max(2, width / 400);
                        int startX = Math.Clamp(cutX - lineThickness / 2, 0, width - lineThickness);
                        for (int y = 0; y < height; y++)
                        {
                            int rowOffset = y * stride;
                            for (int lx = 0; lx < lineThickness; lx++)
                            {
                                int px = rowOffset + (startX + lx) * 4;
                                destination[px] = 255;     // B
                                destination[px + 1] = 133; // G
                                destination[px + 2] = 92;  // R (#5C85FF AccentBrush)
                                destination[px + 3] = 255; // A
                            }
                        }
                    }
                    break;
                }
                case TimelineTransitionEffect.Cut:
                {
                    byte[] source = t < 0.5 ? oldLayer : newLayer;
                    source.AsSpan().CopyTo(destination);
                    break;
                }
                default: // Fade
                {
                    if (t <= 0.0001)
                    {
                        oldLayer.AsSpan().CopyTo(destination);
                    }
                    else if (t >= 0.9999)
                    {
                        newLayer.AsSpan().CopyTo(destination);
                    }
                    else
                    {
                        int weightNew = (int)Math.Round(t * 256.0);
                        int weightOld = 256 - weightNew;
                        for (int i = 0; i < destination.Length; i += 4)
                        {
                            destination[i] = (byte)((oldLayer[i] * weightOld + newLayer[i] * weightNew + 128) >> 8);
                            destination[i + 1] = (byte)((oldLayer[i + 1] * weightOld + newLayer[i + 1] * weightNew + 128) >> 8);
                            destination[i + 2] = (byte)((oldLayer[i + 2] * weightOld + newLayer[i + 2] * weightNew + 128) >> 8);
                            destination[i + 3] = 255;
                        }
                    }
                    break;
                }
            }

            OverlayTimelineBadges(destination, width, height, prepared, options.Effect, t);
        }

        private static void OverlayTimelineBadges(
            Span<byte> frame,
            int width,
            int height,
            PreparedTimelineCanvas prepared,
            TimelineTransitionEffect effect,
            double t)
        {
            int margin = prepared.BadgeMargin;

            if (effect == TimelineTransitionEffect.Wipe)
            {
                bool sweeping = t > 0.001 && t < 0.999;
                if (t < 0.999 && prepared.ScaledOldBadge != null)
                {
                    AlphaCompositeBadge(frame, width, height, prepared.ScaledOldBadge, margin, margin);
                }
                if ((sweeping || t >= 0.999) && prepared.ScaledNewBadge != null)
                {
                    int rightX = Math.Max(margin, width - margin - prepared.ScaledNewBadge.Width);
                    AlphaCompositeBadge(frame, width, height, prepared.ScaledNewBadge, rightX, margin);
                }
                return;
            }

            if (t < 0.5)
            {
                if (prepared.ScaledOldBadge != null)
                    AlphaCompositeBadge(frame, width, height, prepared.ScaledOldBadge, margin, margin);
            }
            else if (prepared.ScaledNewBadge != null)
            {
                int rightX = Math.Max(margin, width - margin - prepared.ScaledNewBadge.Width);
                AlphaCompositeBadge(frame, width, height, prepared.ScaledNewBadge, rightX, margin);
            }
        }

        private static void AlphaCompositeBadge(
            Span<byte> frame,
            int frameW,
            int frameH,
            ScaledBadgeData badge,
            int dstX,
            int dstY)
        {
            int copyW = Math.Min(badge.Width, frameW - dstX);
            int copyH = Math.Min(badge.Height, frameH - dstY);
            if (copyW <= 0 || copyH <= 0) return;

            byte[] srcBytes = badge.Pixels;
            int badgeStride = badge.Width * 4;
            for (int y = 0; y < copyH; y++)
            {
                int srcRowOffset = y * badgeStride;
                int dstRowOffset = ((dstY + y) * frameW + dstX) * 4;
                for (int x = 0; x < copyW; x++)
                {
                    int sp = srcRowOffset + x * 4;
                    byte sa = srcBytes[sp + 3];
                    if (sa == 0) continue;

                    int dp = dstRowOffset + x * 4;
                    if (sa == 255)
                    {
                        frame[dp] = srcBytes[sp];
                        frame[dp + 1] = srcBytes[sp + 1];
                        frame[dp + 2] = srcBytes[sp + 2];
                        frame[dp + 3] = 255;
                    }
                    else
                    {
                        int inv = 255 - sa;
                        frame[dp] = (byte)((srcBytes[sp] * sa + frame[dp] * inv + 127) / 255);
                        frame[dp + 1] = (byte)((srcBytes[sp + 1] * sa + frame[dp + 1] * inv + 127) / 255);
                        frame[dp + 2] = (byte)((srcBytes[sp + 2] * sa + frame[dp + 2] * inv + 127) / 255);
                        frame[dp + 3] = 255;
                    }
                }
            }
        }

        // Renders detached HUD badge chips on the UI thread at 2x DPI so exported GIF/MP4
        // frames stay crisp regardless of whether the live viewport badges are currently visible.
        public static (Image<Bgra32> Old, Image<Bgra32> New) RenderTimelineBadges(
            System.Windows.Media.Brush accentOrange,
            System.Windows.Media.Brush accentGreen,
            WpfFontFamily fontFamily)
        {
            return (
                RenderDetachedBadge("OLD", accentOrange ?? WpfBrushes.Orange, fontFamily),
                RenderDetachedBadge("NEW", accentGreen ?? WpfBrushes.LimeGreen, fontFamily));
        }

        private static Image<Bgra32> RenderDetachedBadge(
            string label,
            System.Windows.Media.Brush accentBrush,
            WpfFontFamily fontFamily)
        {
            var border = new WpfBorder
            {
                Background = new SolidColorBrush(WpfColor.FromArgb(0xDC, 0x1B, 0x1B, 0x1F)),
                BorderBrush = accentBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 4, 12, 4),
                Child = new WpfTextBlock
                {
                    Text = label,
                    FontFamily = fontFamily,
                    FontSize = 11,
                    FontWeight = FontWeights.Black,
                    Foreground = accentBrush
                }
            };

            border.Measure(new WpfSize(double.PositiveInfinity, double.PositiveInfinity));
            WpfSize size = border.DesiredSize;
            border.Arrange(new WpfRect(new WpfPoint(0, 0), size));
            border.UpdateLayout();

            const double dpi = 192.0;
            const double scale = dpi / 96.0;
            int pixelW = Math.Max(1, (int)Math.Ceiling(size.Width * scale));
            int pixelH = Math.Max(1, (int)Math.Ceiling(size.Height * scale));

            var rtb = new RenderTargetBitmap(pixelW, pixelH, dpi, dpi, PixelFormats.Pbgra32);
            rtb.Render(border);
            rtb.Freeze();

            var bgra = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
            bgra.Freeze();
            byte[] pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
            bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
            return Image.LoadPixelData<Bgra32>(pixels, bgra.PixelWidth, bgra.PixelHeight);
        }
    }
}
