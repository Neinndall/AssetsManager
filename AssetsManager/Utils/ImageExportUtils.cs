using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;

namespace AssetsManager.Utils
{
    public static class ImageExportUtils
    {
        public static async Task SaveAsPngAsync(FrameworkElement element, string filePath, LogService logService)
        {
            if (element.ActualWidth <= 0 || element.DesiredSize.Height <= 0)
            {
                logService.LogWarning("The size of the off-screen control for PNG capture is invalid.");
                return;
            }

            try
            {
                var renderWidth = (int)element.ActualWidth;
                var renderHeight = (int)element.DesiredSize.Height;

                // Render the element to a bitmap
                RenderTargetBitmap rtb = new RenderTargetBitmap(renderWidth, renderHeight, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(element);

                // Encode the bitmap to PNG
                PngBitmapEncoder pngEncoder = new PngBitmapEncoder();
                pngEncoder.Frames.Add(BitmapFrame.Create(rtb));

                // Save the PNG to a memory stream first
                using (MemoryStream ms = new MemoryStream())
                {
                    pngEncoder.Save(ms);
                    ms.Seek(0, SeekOrigin.Begin);

                    // Write the memory stream to the file asynchronously
                    await Task.Run(async () =>
                    {
                        using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                        {
                            await ms.CopyToAsync(fileStream);
                        }
                    });
                }

                logService.LogInteractiveSuccess($"Saved as PNG to {Path.GetFileName(filePath)}", filePath, Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                logService.LogError(ex, $"Failed to save data as PNG to {filePath}.");
                throw; // Re-throw the exception to be caught by the calling method
            }
        }
    }
}
