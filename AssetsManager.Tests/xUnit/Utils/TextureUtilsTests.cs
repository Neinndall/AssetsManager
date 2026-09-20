using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace AssetsManager.Tests.xUnit.Utils
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
        public void ModelPart_DoesNotInferTransparencyFromColorTextureAlpha()
        {
            BitmapSource bitmap = BitmapSource.Create(
                1,
                1,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                new byte[] { 0, 0, 0, 128 },
                4);
            bitmap.Freeze();

            var part = new ModelPart
            {
                AllTextures = new Dictionary<string, BitmapSource>
                {
                    ["skin_tx_cm"] = bitmap
                },
                SelectedTextureName = "skin_tx_cm"
            };

            Assert.False(part.IsAlphaBlended);
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

        [Theory]
        [InlineData(1, 8)]
        [InlineData(2, 16)]
        [InlineData(3, 16)]
        public void LoadViewerTexture_DecodesEtcTexFormats(int format, int blockSize)
        {
            using MemoryStream stream = CreateTex(4, 4, (byte)format, new byte[blockSize]);

            BitmapSource bitmap = TextureUtils.LoadViewerTexture(stream, ".tex");

            Assert.NotNull(bitmap);
            Assert.Equal(4, bitmap.PixelWidth);
            Assert.Equal(4, bitmap.PixelHeight);
        }

        [Fact]
        public void LoadViewerTexture_DecodesBc5SnormInSignedSpace()
        {
            var block = new byte[16];
            block[0] = 127;
            block[1] = 0x81;
            block[8] = 0x81;
            block[9] = 0x81;
            using MemoryStream stream = CreateTex(4, 4, 14, block);

            BitmapSource bitmap = TextureUtils.LoadViewerTexture(stream, ".tex");
            var row = new byte[bitmap.PixelWidth * 4];
            bitmap.CopyPixels(new System.Windows.Int32Rect(0, 0, bitmap.PixelWidth, 1), row, row.Length, 0);

            Assert.Equal(new byte[] { 0, 0, 255, 255 }, row.Take(4).ToArray());
        }

        [Theory]
        [InlineData(21)]
        [InlineData(22)]
        public void LoadViewerTexture_DecodesFloatingPointTexWithPreviewClamping(int format)
        {
            byte[] pixel = format == 21
                ? HalfPixel(0.5f, -1f, 2f, 1f)
                : FloatPixel(0.5f, -1f, 2f, 1f);
            using MemoryStream stream = CreateTex(1, 1, (byte)format, pixel);

            BitmapSource bitmap = TextureUtils.LoadViewerTexture(stream, ".tex");
            var bgra = new byte[4];
            bitmap.CopyPixels(bgra, 4, 0);

            Assert.Equal(new byte[] { 255, 0, 128, 255 }, bgra);
        }

        [Fact]
        public void LoadViewerTexture_SelectsAuthoredFloatMipInsteadOfResizingLevelZero()
        {
            byte[] smallest = FloatPixel(1f, 0f, 0f, 1f);
            byte[] largest = Enumerable.Repeat(FloatPixel(0f, 1f, 0f, 1f), 4)
                .SelectMany(pixel => pixel)
                .ToArray();
            using MemoryStream stream = CreateTex(
                2,
                2,
                22,
                smallest.Concat(largest).ToArray(),
                hasMipmaps: true);

            BitmapSource bitmap = TextureUtils.LoadViewerTexture(stream, ".tex", 1, 1);
            var bgra = new byte[4];
            bitmap.CopyPixels(bgra, 4, 0);

            Assert.Equal(1, bitmap.PixelWidth);
            Assert.Equal(1, bitmap.PixelHeight);
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, bgra);
        }

        [Fact]
        public void LoadViewerTexture_ReturnsNullForTruncatedCompatibleTex()
        {
            using MemoryStream stream = CreateTex(4, 4, 22, new byte[4]);

            Assert.Null(TextureUtils.LoadViewerTexture(stream, ".tex"));
        }

        private static MemoryStream CreateTex(
            ushort width,
            ushort height,
            byte format,
            byte[] payload,
            bool hasMipmaps = false)
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(0x00584554u);
                writer.Write(width);
                writer.Write(height);
                writer.Write((byte)1);
                writer.Write(format);
                writer.Write((byte)0);
                writer.Write(hasMipmaps ? (byte)1 : (byte)0);
                writer.Write(payload);
            }
            stream.Position = 0;
            return stream;
        }

        private static byte[] HalfPixel(params float[] channels) =>
            channels
                .SelectMany(channel => BitConverter.GetBytes(BitConverter.HalfToUInt16Bits((Half)channel)))
                .ToArray();

        private static byte[] FloatPixel(params float[] channels) =>
            channels.SelectMany(BitConverter.GetBytes).ToArray();
    }
}
