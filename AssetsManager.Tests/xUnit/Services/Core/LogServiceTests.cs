using AssetsManager.Services.Core;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Core
{
    public sealed class LogServiceTests
    {
        [Fact]
        public void TrySplitClickableMessageMatchesClickableTextDirectly()
        {
            bool matched = LogService.TrySplitClickableMessage(
                "Saved output to Open Folder successfully.",
                @"C:\out",
                "Open Folder",
                out string prefix,
                out string linkText,
                out string suffix);

            Assert.True(matched);
            Assert.Equal("Saved output to ", prefix);
            Assert.Equal("Open Folder", linkText);
            Assert.Equal(" successfully.", suffix);
        }

        [Fact]
        public void TrySplitClickableMessageUsesPathLengthWhenFallingBackToClickablePath()
        {
            bool matched = LogService.TrySplitClickableMessage(
                @"Saved to C:\a!",
                @"C:\a",
                "Click Here To Open Output Folder",
                out string prefix,
                out string linkText,
                out string suffix);

            Assert.True(matched);
            Assert.Equal("Saved to ", prefix);
            Assert.Equal("Click Here To Open Output Folder", linkText);
            Assert.Equal("!", suffix);
        }

        [Fact]
        public void TrySplitClickableMessageReturnsFalseWhenNeitherTextNorPathMatches()
        {
            bool matched = LogService.TrySplitClickableMessage(
                "Export finished.",
                @"C:\out",
                "Open Folder",
                out _,
                out string linkText,
                out _);

            Assert.False(matched);
            Assert.Equal("Open Folder", linkText);
        }
    }
}
