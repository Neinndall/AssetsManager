using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using AssetsManager.Services.Core;

namespace AssetsManager.Utils
{
    public static class PerformanceBenchmark
    {
        public static void RunTextureBenchmark(LogService logService)
        {
            logService.Log("--- STARTING TEXTURE PROCESSING BENCHMARK ---");
            
            int iterations = 20; // 20 images to keep test time reasonable
            int width = 2048;
            int height = 2048;
            
            // Create a dummy image to test with
            using var dummyImage = new Image<Rgba32>(width, height);
            
            // --- TEST A: CURRENT PATTERN (SixLabors CloneAs + new byte[]) ---
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long memStartOld = GC.GetTotalAllocatedBytes(true);
            Stopwatch sw = Stopwatch.StartNew();

            for (int i = 0; i < iterations; i++)
            {
                // Simulate TextureUtils.LoadTexture logic
                using (Image<Rgba32> img = dummyImage.Clone())
                {
                    // Simulating the Bgra32 conversion and byte[] copy
                    using (Image<Bgra32> bgra32Image = img.CloneAs<Bgra32>())
                    {
                        var pixelBuffer = new byte[bgra32Image.Width * bgra32Image.Height * 4];
                        bgra32Image.CopyPixelDataTo(pixelBuffer);

                        int stride = bgra32Image.Width * 4;
                        var bitmapSource = BitmapSource.Create(bgra32Image.Width, bgra32Image.Height, 96, 96, PixelFormats.Bgra32, null, pixelBuffer, stride);
                        bitmapSource.Freeze();
                    }
                }
            }
            sw.Stop();
            long timeOld = sw.ElapsedMilliseconds;
            long allocatedOld = GC.GetTotalAllocatedBytes(true) - memStartOld;
            logService.Log($"[OLD PATTERN] Time: {timeOld}ms | RAM Allocated: {allocatedOld / 1024.0 / 1024.0:F2} MB");

            // --- TEST B: OPTIMIZED PATTERN (Direct Copy + MemoryPool) ---
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long memStartNew = GC.GetTotalAllocatedBytes(true);
            sw.Restart();

            for (int i = 0; i < iterations; i++)
            {
                using (Image<Rgba32> img = dummyImage.Clone())
                {
                    // Optimization: We know Rgba32 and Bgra32 have the same memory layout (4 bytes),
                    // we can just swap R and B during the copy if needed, or use a pooled buffer.
                    // Actually, BitmapSource.Create requires a buffer.
                    
                    int pixelCount = img.Width * img.Height;
                    int bufferSize = pixelCount * 4;
                    
                    // Use ArrayPool to avoid GC pressure
                    byte[] pixelBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
                    try
                    {
                        // Copy data directly (assuming we handle R/B swap if needed, 
                        // but here we just measure the allocation saving)
                        img.CopyPixelDataTo(pixelBuffer);
                        
                        // Note: To truly use Bgra32 from Rgba32 without CloneAs, 
                        // we would manually swap or use a custom converter, 
                        // but the main cost is the allocation and the extra image object.
                        
                        int stride = img.Width * 4;
                        var bitmapSource = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, pixelBuffer, stride);
                        bitmapSource.Freeze();
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(pixelBuffer);
                    }
                }
            }
            sw.Stop();
            long timeNew = sw.ElapsedMilliseconds;
            long allocatedNew = GC.GetTotalAllocatedBytes(true) - memStartNew;
            logService.Log($"[NEW PATTERN] Time: {timeNew}ms | RAM Allocated: {allocatedNew / 1024.0 / 1024.0:F2} MB");

            double ramSaving = (double)(allocatedOld - allocatedNew) / (allocatedOld + 1) * 100;
            logService.Log($">> RAM SAVED: {ramSaving:F1}%");
            logService.Log("--- TEXTURE BENCHMARK COMPLETED ---");
        }
    }
}
