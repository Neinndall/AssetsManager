using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Explorer;
using AssetsManager.Views.Models.Dialogs;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Explorer
{
    public sealed class ImageMergerServiceTests
    {
        [Theory]
        [InlineData("icon.tex", true)]
        [InlineData("ICON.TEX", true)]
        [InlineData("texture.dds", true)]
        [InlineData("TEXTURE.DDS", true)]
        [InlineData("image.png", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsRiotTexturePathMatchesTexAndDdsCaseInsensitively(string path, bool expected)
        {
            Assert.Equal(expected, ImageMergerService.IsRiotTexturePath(path));
        }

        [Fact]
        public async Task ConcurrentRenderRequestsCompleteWithoutObjectDisposedException()
        {
            var service = new ImageMergerService(null, null, null);
            var bitmap = BitmapSource.Create(
                4,
                4,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                new byte[4 * 4 * 4],
                4 * 4);
            bitmap.Freeze();

            service.Items.Add(new ImageMergerItem
            {
                Name = "sample.png",
                Path = "sample.png",
                Image = bitmap
            });

            var model = new ImageMergerModel(service.Items) { Columns = 1, Margin = 0 };
            var tasks = Enumerable.Range(0, 30)
                .Select(_ => service.RenderMergedImageAsync(model))
                .ToArray();

            await Task.WhenAll(tasks);

            Assert.NotNull(model.PreviewImage);
            Assert.False(model.IsProcessing);
        }
    }
}
