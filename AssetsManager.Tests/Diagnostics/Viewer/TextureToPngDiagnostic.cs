using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Utils;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// Writes the top mip of a TEX/DDS as PNG, plus its alpha channel as a grey image. A path that is not a file on disk is
    /// read from the installed WADs as a game asset path.
    /// </summary>
    internal static class TextureToPngDiagnostic
    {
        internal static void Run(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: tex-to-png <texture|game asset path> <output.png>");
                return;
            }

            using Stream stream = File.Exists(args[0]) ? File.OpenRead(args[0]) : OpenInstalled(args[0]);
            if (stream == null)
            {
                Console.WriteLine($"[Texture] {args[0]} is neither a file nor an installed asset.");
                return;
            }
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

        private static Stream OpenInstalled(string assetPath)
        {
            ulong hash = LeagueToolkit.Hashing.XxHash64Ext.Hash(assetPath.Replace('\\', '/').ToLowerInvariant());
            string final = Path.Combine(AssetsManager.Tests.Support.InstalledSkins.FindInstall(), @"Game\DATA\FINAL");
            foreach (string wadPath in Directory.GetFiles(final, "*.wad.client", SearchOption.AllDirectories))
            {
                using var wad = new LeagueToolkit.Core.Wad.WadFile(wadPath);
                if (!wad.Chunks.ContainsKey(hash)) continue;
                using var data = wad.LoadChunkDecompressed(hash);
                return new MemoryStream(data.Span.ToArray(), writable: false);
            }
            return null;
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
