using System;
using AssetsManager.Views.Dialogs;
using Xunit;

namespace AssetsManager.Tests.xUnit.Views
{
    public sealed class AudioPlayerWindowTests
    {
        [Theory]
        [InlineData("https://example.com/audio.mp3", true)]
        [InlineData("http://localhost:8080/track.ogg", true)]
        [InlineData(@"C:\Music\track.wav", true)]
        [InlineData("not a valid url", false)]
        [InlineData("relative/path.mp3", false)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        [InlineData(null, false)]
        public void TryCreatePlaybackUriValidatesAbsoluteUrlsAndLocalPaths(string input, bool expected)
        {
            bool ok = AudioPlayerWindow.TryCreatePlaybackUri(input, out Uri uri);

            Assert.Equal(expected, ok);
            if (expected)
            {
                Assert.NotNull(uri);
            }
            else
            {
                Assert.Null(uri);
            }
        }
    }
}
