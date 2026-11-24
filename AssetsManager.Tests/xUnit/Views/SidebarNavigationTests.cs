using AssetsManager.Views.Controls;
using Xunit;

namespace AssetsManager.Tests.xUnit.Views
{
    public sealed class SidebarNavigationTests
    {
        [Theory]
        [InlineData("Settings", true)]
        [InlineData("Help", true)]
        [InlineData("Home", false)]
        [InlineData("Explorer", false)]
        [InlineData("Comparator", false)]
        [InlineData("Viewer", false)]
        [InlineData("Monitor", false)]
        [InlineData("HashLab", false)]
        [InlineData("News", false)]
        public void ModalNavigationTagsAreDistinguishedFromMainViews(string tag, bool expectedModal)
        {
            Assert.Equal(expectedModal, SidebarView.IsModalNavigationTag(tag));
        }
    }
}
