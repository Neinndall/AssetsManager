using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Resources.Textures;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map;

public sealed class BlockTextureDataTests
{
    internal static byte[] Tex(byte format, int width, int height, bool mips = true)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x00584554u);
        writer.Write((ushort)width); writer.Write((ushort)height);
        writer.Write((byte)1); writer.Write(format); writer.Write((byte)0); writer.Write((byte)(mips ? 1 : 0));
        int count = mips ? 1 + (int)Math.Log2(Math.Max(width, height)) : 1;
        for (int level = count - 1; level >= 0; level--)
        {
            int blocks = ((Math.Max(width >> level, 1) + 3) / 4) * ((Math.Max(height >> level, 1) + 3) / 4);
            for (int block = 0; block < blocks; block++)
            {
                if (format == 12)
                {
                    writer.Write((byte)220); writer.Write((byte)20);
                    writer.Write(new byte[] { 0x88, 0xC6, 0xFA, 0x88, 0xC6, 0xFA });
                }
                writer.Write((ushort)(level % 2 == 0 ? 0x9B26 : 0x6CE3));
                writer.Write((ushort)0xE9B1);
                writer.Write(0xE4E4E4E4u);
            }
        }
        return stream.ToArray();
    }

    [Theory]
    [InlineData(10)] [InlineData(11)] [InlineData(12)]
    public void SelectedBlocksMatchOriginalLevelsAndDecodedFallback(byte format)
    {
        byte[] bytes = Tex(format, 64, 32);
        using var stream = new MemoryStream(bytes);
        var blocks = BlockTextureData.TryReadTex(stream, 16);
        Assert.Equal(0, stream.Position);
        Assert.Equal(new[] { 16, 8, 4, 2, 1 }, blocks.Levels.Select(level => level.Width));
        var image = new MapTextureImage(blocks);
        Assert.Equal(16, image.Width);
        Assert.False(image.PixelsDecoded);
        Assert.True(MapTextureLoadingService.PreviewSatisfiesFull(image));
        Assert.False(image.PixelsDecoded);
        var decoded = TextureUtils.LoadViewerTextureMipChain(stream, ".tex", 16);
        Assert.Equal(decoded.Count, image.MipLevels.Count);
        for (int level = 0; level < decoded.Count; level++)
        {
            var expected = new byte[decoded[level].PixelWidth * decoded[level].PixelHeight * 4];
            var actual = new byte[expected.Length];
            decoded[level].CopyPixels(expected, decoded[level].PixelWidth * 4, 0);
            image.MipLevels[level].CopyPixels(actual, decoded[level].PixelWidth * 4, 0);
            Assert.Equal(expected, actual);
        }
        long kept = blocks.Levels.Sum(level => (long)level.Data.Length);
        Assert.True(kept < bytes.Length / 4);
    }

    [Theory]
    [InlineData(10, 7, 5, 1)] [InlineData(12, 1, 1, 64)]
    [InlineData(12, 64, 32, 100)] [InlineData(10, 8, 16, 2)]
    public void DimensionsAndBlockRoundingPreserveMipSelection(byte format, int width, int height, int requested)
    {
        using var stream = new MemoryStream(Tex(format, width, height));
        var blocks = BlockTextureData.TryReadTex(stream, requested);
        var decoded = TextureUtils.LoadViewerTextureMipChain(stream, ".tex", requested);
        Assert.Equal(decoded.Select(level => (level.PixelWidth, level.PixelHeight)),
            blocks.Levels.Select(level => (level.Width, level.Height)));
        foreach (var level in blocks.Levels)
            Assert.Equal(((level.Width + 3) / 4) * ((level.Height + 3) / 4) * (format == 12 ? 16 : 8), level.Data.Length);
    }

    [Fact]
    public void UnsupportedContainersAndTruncationKeepTheCallersPosition()
    {
        byte[] valid = Tex(12, 16, 8);
        foreach (int field in new[] { 8, 9, 10 })
        {
            byte[] other = (byte[])valid.Clone(); other[field] = 3;
            using var stream = new MemoryStream(other);
            Assert.Null(BlockTextureData.TryReadTex(stream, 4));
            Assert.Equal(0, stream.Position);
        }
        using var truncated = new MemoryStream(valid[..^1]);
        Assert.Throws<EndOfStreamException>(() => BlockTextureData.TryReadTex(truncated, 4));
        Assert.Equal(0, truncated.Position);
        using var trailing = new MemoryStream(valid.Concat(new byte[] { 0 }).ToArray());
        Assert.Null(BlockTextureData.TryReadTex(trailing, 4));
        Assert.Equal(0, trailing.Position);
    }

    [Fact]
    public async Task ProgressiveWavesShareCompressedImagesWithoutDecodingPixels()
    {
        string root = Path.Combine(Path.GetTempPath(), "AssetsManagerBlocks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string path = "assets/test.tex";
            Directory.CreateDirectory(Path.Combine(root, "assets"));
            await File.WriteAllBytesAsync(Path.Combine(root, path), Tex(12, 256, 128));
            await File.WriteAllBytesAsync(Path.Combine(root, "assets/bad.tex"), Tex(10, 16, 8)[..^1]);
            using var loader = new MapTextureLoadingService(new MapAssetResolver(null, null), null);
            var tasks = Enumerable.Range(0, 8).Select(_ => loader.LoadLightmapsPreviewAsync(new[] { path }, root)).ToArray();
            var loaded = await Task.WhenAll(tasks);
            var preview = loaded[0][path];
            Assert.Equal(64, preview.Width);
            Assert.NotNull(preview.Blocks);
            Assert.All(loaded, result => Assert.Same(preview, result[path]));
            Assert.False(preview.PixelsDecoded);
            var full = (await loader.LoadLightmapsFullAsync(new[] { path }, root))[path];
            Assert.Equal(256, full.Width);
            Assert.False(full.PixelsDecoded);
            Assert.Same(full, (await loader.LoadLightmapsPreviewAsync(new[] { path }, root))[path]);
            var mixed = await loader.LoadLightmapsPreviewAsync(new[] { path, "assets/bad.tex" }, root);
            Assert.Same(full, Assert.Single(mixed).Value);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loader.LoadLightmapsPreviewAsync(new[] { path }, root, canceled.Token));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
