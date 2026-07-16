using System;
using System.IO;
using System.Threading;
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
    }
}
