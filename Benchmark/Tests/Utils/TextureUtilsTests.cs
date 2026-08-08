using System;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Utils;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace AssetsManager.BenchmarkTests.Tests.Utils
{
    public class TextureUtilsTests
    {
        [Fact]
        public void LoadTexture_PreservesColorAndAlphaDuringBgraConversion()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    string filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
                    try
                    {
                        using var source = new Image<Rgba32>(1, 1);
                        source[0, 0] = new Rgba32(10, 20, 30, 40);
                        source.SaveAsPng(filePath);

                        var bitmap = TextureUtils.LoadTextureFromFile(filePath);
                        var pixels = new byte[4];
                        bitmap.CopyPixels(pixels, 4, 0);

                        Assert.Equal(new byte[] { 30, 20, 10, 40 }, pixels);
                    }
                    finally
                    {
                        File.Delete(filePath);
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) throw failure;
        }

        [Fact]
        public void CreateViewerTextureBrush_UsesUvRelativeViewportAndAuthoredWrapMode()
        {
            BitmapSource bitmap = BitmapSource.Create(
                1,
                1,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                new byte[] { 0, 0, 0, 255 },
                4);
            bitmap.Freeze();

            ImageBrush tiled = TextureUtils.CreateViewerTextureBrush(bitmap, true);
            ImageBrush clamped = TextureUtils.CreateViewerTextureBrush(bitmap, false);

            Assert.Equal(BrushMappingMode.RelativeToBoundingBox, tiled.ViewportUnits);
            Assert.Equal(BrushMappingMode.RelativeToBoundingBox, tiled.ViewboxUnits);
            Assert.Equal(TileMode.Tile, tiled.TileMode);
            Assert.Equal(TileMode.None, clamped.TileMode);
        }

        [Fact]
        public void LoadViewerTexture_SelectsExistingTexMipWithoutResizing()
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(0x00584554u);
                writer.Write((ushort)2);
                writer.Write((ushort)2);
                writer.Write((byte)0);
                writer.Write((byte)20);
                writer.Write((byte)0);
                writer.Write((byte)1);
                writer.Write(new byte[] { 30, 20, 10, 40 });
                writer.Write(new byte[16]);
            }

            stream.Position = 0;
            BitmapSource bitmap = TextureUtils.LoadViewerTexture(stream, ".tex", 1, 1);
            var pixels = new byte[4];
            bitmap.CopyPixels(pixels, 4, 0);

            Assert.Equal(1, bitmap.PixelWidth);
            Assert.Equal(1, bitmap.PixelHeight);
            Assert.Equal(new byte[] { 30, 20, 10, 40 }, pixels);
        }

        [Fact]
        public void LoadViewerTexture_PreservesTexWithoutMipmapsWhenItExceedsCap()
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(0x00584554u);
                writer.Write((ushort)2);
                writer.Write((ushort)2);
                writer.Write((byte)0);
                writer.Write((byte)20);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write(new byte[16]);
            }

            stream.Position = 0;
            BitmapSource bitmap = TextureUtils.LoadViewerTexture(stream, ".tex", 1, 1);

            Assert.Equal(2, bitmap.PixelWidth);
            Assert.Equal(2, bitmap.PixelHeight);
        }
    }
}
