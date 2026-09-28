using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Utils;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>Writes the top mip of a TEX/DDS as PNG, plus its alpha channel as a grey image.</summary>
    internal static class TextureToPngDiagnostic
    {
        internal static void Run(string[] args)
        {
            if (args.Length < 2 || !File.Exists(args[0]))
            {
                Console.WriteLine("Usage: tex-to-png <texture> <output.png>");
                return;
            }

            using FileStream stream = File.OpenRead(args[0]);
            BitmapSource image = TextureUtils.LoadViewerTexture(stream, Path.GetExtension(args[0]));
            BitmapSource bgra = image.Format == PixelFormats.Bgra32
                ? image
                : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

            int stride = bgra.PixelWidth * 4;
            byte[] pixels = new byte[stride * bgra.PixelHeight];
            bgra.CopyPixels(pixels, stride, 0);
            byte[] alpha = new byte[pixels.Length];
            int transparent = 0;
            for (int at = 0; at < pixels.Length; at += 4)
            {
                byte a = pixels[at + 3];
                alpha[at] = alpha[at + 1] = alpha[at + 2] = a;
                alpha[at + 3] = 255;
                if (a < 128) transparent++;
            }

            Save(bgra, args[1]);
            Save(BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, 96, 96, PixelFormats.Bgra32, null, alpha, stride),
                Path.ChangeExtension(args[1], ".alpha.png"));
            Console.WriteLine(
                $"[Texture] {bgra.PixelWidth}x{bgra.PixelHeight} source={image.Format} " +
                $"alphaBelowHalf={transparent * 100.0 / (pixels.Length / 4):0.#}%");
        }

        private static void Save(BitmapSource bitmap, string path)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream output = File.Create(path);
            encoder.Save(output);
        }
    }
}
