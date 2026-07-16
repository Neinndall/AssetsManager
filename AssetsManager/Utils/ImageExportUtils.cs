using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Settings;

namespace AssetsManager.Utils
{
    public static class ImageExportUtils
    {
        private const long MaxBitmapBytes = 1024L * 1024 * 1024;

        public static async Task SaveAsPngAsync(
            FrameworkElement element,
            string filePath,
            LogService logService,
            CancellationToken cancellationToken = default,
            IProgress<double> progress = null)
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
                progress?.Report(0.25);

                await SaveBitmapAsPngAsync(rtb, filePath, cancellationToken, progress);

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
            CancellationToken cancellationToken = default,
            IProgress<double> progress = null)
        {
            await SaveBitmapAsImageAsync(bitmap, filePath, ImageExportFormat.Png, cancellationToken, progress);
        }

        public static async Task SaveBitmapAsImageAsync(
            BitmapSource bitmap,
            string filePath,
            ImageExportFormat format,
            CancellationToken cancellationToken = default,
            IProgress<double> progress = null)
        {
            if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));
            ValidateDimensions(bitmap.PixelWidth, bitmap.PixelHeight);

            BitmapSource exportBitmap = bitmap;
            if (!exportBitmap.IsFrozen)
            {
                exportBitmap = bitmap.Clone();
                exportBitmap.Freeze();
            }

            progress?.Report(0.5);
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
            progress?.Report(1.0);
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
    }
}
