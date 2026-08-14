using System;
using System.Text;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using Xunit;

namespace AssetsManager.BenchmarkTests.Hashes
{
    public sealed class ChunkExtensionInferenceTests
    {
        [Theory]
        [InlineData(new byte[] { 0x50, 0x52, 0x4F, 0x50, 0x01, 0x00 }, false, "bin")]
        [InlineData(new byte[] { 0x50, 0x54, 0x43, 0x48, 0x01, 0x00 }, false, "bin")]
        [InlineData(new byte[] { 0x44, 0x44, 0x53, 0x20, 0x7C, 0x00 }, false, "dds")]
        [InlineData(new byte[] { 0x54, 0x45, 0x58, 0x00, 0x01, 0x00 }, false, "tex")]
        [InlineData(new byte[] { 0x33, 0x22, 0x11, 0x00, 0x01, 0x00 }, false, "skn")]
        [InlineData(new byte[] { 0x72, 0x33, 0x64, 0x32, 0x73, 0x6B, 0x6C, 0x74 }, false, "skl")]
        [InlineData(new byte[] { 0x72, 0x33, 0x64, 0x32, 0x61, 0x6E, 0x6D, 0x64 }, false, "anm")]
        [InlineData(new byte[] { 0x72, 0x33, 0x64, 0x32, 0x63, 0x61, 0x6E, 0x6D }, false, "anm")]
        [InlineData(new byte[] { 0x50, 0x72, 0x65, 0x4C, 0x6F, 0x61, 0x64 }, false, "preload")]
        [InlineData(new byte[] { 0x42, 0x4B, 0x48, 0x44, 0x00, 0x00 }, false, "bnk")]
        [InlineData(new byte[] { 0x4F, 0x45, 0x47, 0x4D, 0x01, 0x00 }, false, "mapgeo")]
        public void InferChunkExtension_DetectsFormatsViaFileTypeDetector(byte[] header, bool detectJson, string expectedExtension)
        {
            var data = new ArraySegment<byte>(header);
            string inferred = HashGuessingService.InferChunkExtension(data, detectJson);
            Assert.Equal(expectedExtension, inferred);
        }

        [Fact]
        public void InferChunkExtension_RespectsDetectJsonFlag()
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes("  {\"test\": true}");
            var data = new ArraySegment<byte>(jsonBytes);

            Assert.Equal(string.Empty, HashGuessingService.InferChunkExtension(data, detectJson: false));
            Assert.Equal("json", HashGuessingService.InferChunkExtension(data, detectJson: true));
        }
    }
}
