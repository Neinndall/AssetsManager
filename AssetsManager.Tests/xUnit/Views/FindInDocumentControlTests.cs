using AssetsManager.Views.Controls.Shared;
using Xunit;

namespace AssetsManager.Tests.xUnit.Views
{
    public sealed class FindInDocumentControlTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void EmptyDocumentReturnsNotFoundWithoutThrowing(bool forward)
        {
            int index = FindInDocumentControl.FindMatchIndex(string.Empty, "abc", 0, 0, forward);
            Assert.Equal(-1, index);
        }

        [Fact]
        public void ForwardSearchFindsNextMatchAndWrapsAround()
        {
            const string text = "alpha beta ALPHA gamma";

            Assert.Equal(0, FindInDocumentControl.FindMatchIndex(text, "alpha", 0, 0, forward: true));
            Assert.Equal(11, FindInDocumentControl.FindMatchIndex(text, "alpha", 5, 0, forward: true));
            Assert.Equal(0, FindInDocumentControl.FindMatchIndex(text, "alpha", 16, 11, forward: true));
        }

        [Fact]
        public void BackwardSearchFindsPreviousMatchAndWrapsAround()
        {
            const string text = "alpha beta ALPHA gamma";

            Assert.Equal(11, FindInDocumentControl.FindMatchIndex(text, "alpha", 22, 22, forward: false));
            Assert.Equal(0, FindInDocumentControl.FindMatchIndex(text, "alpha", 11, 11, forward: false));
            Assert.Equal(11, FindInDocumentControl.FindMatchIndex(text, "alpha", 0, 0, forward: false));
        }
    }
}
