using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Utils;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    internal static class ChampionTextureDiagnostic
    {
        public static void Run(string[] paths)
        {
            if (paths.Length == 0)
            {
                Console.WriteLine("Usage: champion-texture-audit <texture.tex|texture.dds> [paths ...]");
                return;
            }

            foreach (string path in paths)
            {
                Audit(Path.GetFullPath(path));
            }
        }

        private static void Audit(string path)
        {
            Console.WriteLine($"\n[ChampionTexture] {path}");
            if (!File.Exists(path))
            {
                Console.WriteLine("  FILE NOT FOUND");
                return;
            }

            BitmapSource source = TextureUtils.LoadTextureFromFile(path);
            if (source == null)
            {
                Console.WriteLine("  UNREADABLE");
                return;
            }

            var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[height * stride];
            bitmap.CopyPixels(pixels, stride, 0);

            long[] sums = new long[4];
            byte[] minimum = { 255, 255, 255, 255 };
            byte[] maximum = { 0, 0, 0, 0 };
            for (int i = 0; i < pixels.Length; i += 4)
            {
                for (int channel = 0; channel < 4; channel++)
                {
                    byte value = pixels[i + channel];
                    sums[channel] += value;
                    minimum[channel] = Math.Min(minimum[channel], value);
                    maximum[channel] = Math.Max(maximum[channel], value);
                }
            }

            double pixelCount = width * (double)height;
            Console.WriteLine($"  Size={width}x{height} Format={source.Format} Bits={source.Format.BitsPerPixel}");
            Console.WriteLine(
                $"  B={Describe(sums[0], minimum[0], maximum[0], pixelCount)} " +
                $"G={Describe(sums[1], minimum[1], maximum[1], pixelCount)} " +
                $"R={Describe(sums[2], minimum[2], maximum[2], pixelCount)} " +
                $"A={Describe(sums[3], minimum[3], maximum[3], pixelCount)}");

            string previewPath = Path.Combine(
                Path.GetTempPath(),
                $"assetsmanager-texture-{Guid.NewGuid():N}.png");
            using (var output = File.Create(previewPath))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(output);
            }

            Console.WriteLine($"  Preview={previewPath}");
        }

        private static string Describe(long sum, byte minimum, byte maximum, double count) =>
            $"min={minimum} max={maximum} avg={sum / count:0.###}";
    }
}
