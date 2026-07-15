using AssetsManager.Views.Models.Explorer;
using Xunit;

namespace AssetsManager.BenchmarkTests.Tests.Explorer
{
    public class FilePreviewerModelTests
    {
        [Fact]
        public void ContentPreview_TransitionsBetweenTextAndStatusStates()
        {
            var model = new FilePreviewerModel();

            model.BeginContentLoading(true);
            Assert.Equal(PreviewState.Loading, model.ContentPreviewState);
            Assert.True(model.IsContentPreviewVisible);

            model.ShowContentPreview(PreviewState.Text);
            Assert.Equal(PreviewState.Text, model.ContentPreviewState);
            Assert.False(model.IsContentStatusVisible);

            model.ShowContentUnsupported(".bin");
            Assert.Equal(PreviewState.Unsupported, model.ContentPreviewState);
            Assert.True(model.IsContentStatusVisible);
            Assert.Equal("Format not supported", model.ContentPreviewTitle);
        }

        [Fact]
        public void ImageLoading_PreservesTheDualViewLayout()
        {
            var model = new FilePreviewerModel();
            model.ShowContentPreview(PreviewState.Media);
            model.ShowImagePreview();

            model.BeginImageLoading();

            Assert.Equal(PreviewState.Loading, model.ImagePreviewState);
            Assert.True(model.IsImagePreviewVisible);
            Assert.True(model.IsDualView);
        }

        [Fact]
        public void ImagePreview_PreservesAnUnsupportedContentPanel()
        {
            var model = new FilePreviewerModel();
            model.ShowContentUnsupported(".skn");

            model.ShowImagePreview();

            Assert.Equal(PreviewState.Unsupported, model.ContentPreviewState);
            Assert.Equal(PreviewState.Image, model.ImagePreviewState);
            Assert.True(model.IsDualView);
        }

        [Fact]
        public void ContentPreview_RejectsUnsupportedDisplayKinds()
        {
            var model = new FilePreviewerModel();

            Assert.Throws<System.ArgumentOutOfRangeException>(() => model.ShowContentPreview(PreviewState.Image));
        }
    }
}
