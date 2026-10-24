using System;
using System.IO;
using AssetsManager.Services.Parsers;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Parsers;

public sealed class ImageAutoAtlasTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void ReadsPageCountAndPreservesSpritePageIndices(int pageCount)
    {
        byte[] data = CreateAtlas(pageCount);

        Assert.True(ImageAutoAtlas.TryRead(data, out var atlas));
        Assert.Equal(pageCount, atlas.TextureHashes.Count);
        Assert.Equal(0UL, atlas.TextureHashes[0]);
        Assert.Equal((uint)(pageCount - 1), Assert.Single(atlas.Sprites).TextureIndex);
    }

    [Fact]
    public void ReadsEmptyAtlas()
    {
        Assert.True(ImageAutoAtlas.TryRead(CreateAtlas(0), out var atlas));
        Assert.Empty(atlas.TextureHashes);
        Assert.Empty(atlas.Sprites);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("page-count")]
    [InlineData("sprite-count")]
    [InlineData("page-index")]
    public void RejectsInvalidTables(string corruption)
    {
        byte[] data = CreateAtlas(5);
        switch (corruption)
        {
            case "truncated": Array.Resize(ref data, data.Length - 1); break;
            case "trailing": Array.Resize(ref data, data.Length + 1); break;
            case "page-count": BitConverter.GetBytes(uint.MaxValue).CopyTo(data, 4); break;
            case "sprite-count": BitConverter.GetBytes(uint.MaxValue).CopyTo(data, 48); break;
            case "page-index": BitConverter.GetBytes(5U).CopyTo(data, data.Length - 4); break;
        }

        Assert.False(ImageAutoAtlas.TryRead(data, out var atlas));
        Assert.Null(atlas);
    }

    private static byte[] CreateAtlas(int pageCount)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("IMAA"u8);
        writer.Write((uint)pageCount);
        for (int i = 0; i < pageCount; i++) writer.Write((ulong)i);
        writer.Write(pageCount == 0 ? 0U : 1U);
        if (pageCount != 0)
        {
            writer.Write(123UL);
            writer.Write(0f);
            writer.Write(0f);
            writer.Write(1f);
            writer.Write(1f);
            writer.Write((uint)(pageCount - 1));
        }
        return stream.ToArray();
    }
}
