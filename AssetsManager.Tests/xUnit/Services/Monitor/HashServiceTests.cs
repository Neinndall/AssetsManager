using System;
using System.Text;
using AssetsManager.Services.Monitor;
using AssetsManager.Views.Models.Monitor;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Monitor;

public sealed class HashServiceTests
{
    [Theory]
    [InlineData(HashType.Sha512, 0xBA7A6193A135AFDDul)]
    [InlineData(HashType.Sha256, 0xEACF018FBF1678BAul)]
    [InlineData(HashType.Hkdf, 0x3D4EDA0AB8CF0D24ul)]
    [InlineData(HashType.Blake3, 0x33514638ACB33764ul)]
    public void ComputesRmanChunkIdForSupportedHashTypes(HashType hashType, ulong expected)
    {
        var service = new HashService();

        ulong actual = service.ComputeChunkId("abc"u8, hashType);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputesBlake3ChunkIdRequiredByVersionFourManifests()
    {
        var service = new HashService();

        ulong actual = service.ComputeChunkId(ReadOnlySpan<byte>.Empty, HashType.Blake3);

        Assert.Equal(0xA6A1F9F5B94913AFul, actual);
    }

    [Fact]
    public void RejectsDifferentChunkContent()
    {
        var service = new HashService();
        byte[] expectedData = Encoding.UTF8.GetBytes("expected");
        ulong expectedId = service.ComputeChunkId(expectedData, HashType.Sha256);

        Assert.False(service.VerifyChunk("corrupted"u8, expectedId, HashType.Sha256));
    }
}
