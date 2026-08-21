using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Monitor;
using AssetsManager.Views.Models.Monitor;
using FlatSharp;
using LeagueToolkit.IO.ReleaseManifestFile;
using Xunit;
using ZstdSharp;
using LtFile = LeagueToolkit.IO.ReleaseManifestFile.ReleaseManifestFile;

namespace AssetsManager.Tests.xUnit.Services.Monitor;

public sealed class RmanServiceTests
{
    [Fact]
    public void ParsesValidManifestAndBuildsValidatedLookupAndPaths()
    {
        byte[] data = CreateRman();

        RmanManifest manifest = new RmanService().Parse(data);

        Assert.Equal(0x1122334455667788ul, manifest.ManifestId);
        Assert.Equal("Data/test.wad.client", Assert.Single(manifest.Files).Name);
        Assert.Equal(HashType.Sha256, manifest.Files[0].HashType);
        Assert.Equal(new byte[] { 1 }, manifest.Files[0].LanguageIds);
        RmanChunk chunk = Assert.Single(Assert.Single(manifest.Bundles).Chunks);
        Assert.Same(chunk, manifest.GetChunk(chunk.ChunkId));
    }

    [Fact]
    public void RejectsTruncatedOrUnsupportedHeaders()
    {
        Assert.Throws<InvalidDataException>(() => new RmanService().Parse("RMAN"u8.ToArray()));

        byte[] unsupported = CreateRman();
        unsupported[4] = 3;
        Assert.Throws<InvalidDataException>(() => new RmanService().Parse(unsupported));

        byte[] invalidRange = CreateRman();
        BinaryPrimitives.WriteUInt32LittleEndian(invalidRange.AsSpan(12, 4), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => new RmanService().Parse(invalidRange));
    }

    [Fact]
    public void RejectsCorruptCompressedBodyAndTruncatedSignature()
    {
        byte[] corrupt = CreateRman();
        corrupt[corrupt.Length / 2] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => new RmanService().Parse(corrupt));

        byte[] missingSignature = CreateRman();
        missingSignature[7] = 1;
        Assert.Throws<InvalidDataException>(() => new RmanService().Parse(missingSignature));
    }

    [Fact]
    public void RejectsMissingChunksAndIncorrectFileSize()
    {
        byte[] missingChunk = CreateRman(body => body.Files[0].ChunkIDs[0] = 0xDEADBEEFul);
        Assert.Throws<InvalidDataException>(() => new RmanService().Parse(missingChunk));

        byte[] wrongSize = CreateRman(body => body.Files[0].Size++);
        Assert.Throws<InvalidDataException>(() => new RmanService().Parse(wrongSize));
    }

    [Fact]
    public void RejectsDirectoryCycles()
    {
        byte[] data = CreateRman(body =>
        {
            body.Directories = new List<ReleaseManifestDirectory>
            {
                new() { ID = 10, ParentID = 11, Name = "Data" },
                new() { ID = 11, ParentID = 10, Name = "Cycle" }
            };
        });

        Assert.Throws<InvalidDataException>(() => new RmanService().Parse(data));
    }

    [Fact]
    public void AcceptsConsistentDuplicateChunksButRejectsContradictorySizes()
    {
        byte[] consistent = CreateRman(body => body.Bundles.Add(new ReleaseManifestBundle
        {
            ID = 0xBBBB,
            Chunks = new List<ReleaseManifestBundleChunk>
            {
                new() { ID = 0x1234, CompressedSize = 4, UncompressedSize = 5 }
            }
        }));
        Assert.NotNull(new RmanService().Parse(consistent).GetChunk(0x1234));

        byte[] contradictory = CreateRman(body => body.Bundles.Add(new ReleaseManifestBundle
        {
            ID = 0xBBBB,
            Chunks = new List<ReleaseManifestBundleChunk>
            {
                new() { ID = 0x1234, CompressedSize = 4, UncompressedSize = 6 }
            }
        }));
        Assert.Throws<InvalidDataException>(() => new RmanService().Parse(contradictory));
    }

    [Fact]
    public void PropagatesCancellationBeforeParsing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => new RmanService().Parse(CreateRman(), cancellation.Token));
    }

    private static byte[] CreateRman(Action<ReleaseManifestBody> configure = null)
    {
        var body = new ReleaseManifestBody
        {
            Bundles = new List<ReleaseManifestBundle>
            {
                new()
                {
                    ID = 0xAAAA,
                    Chunks = new List<ReleaseManifestBundleChunk>
                    {
                        new() { ID = 0x1234, CompressedSize = 4, UncompressedSize = 5 }
                    }
                }
            },
            Languages = new List<ReleaseManifestLanguage>
            {
                new() { ID = 1, Name = "en_US" }
            },
            Directories = new List<ReleaseManifestDirectory>
            {
                new() { ID = 10, ParentID = 0, Name = "Data" }
            },
            Files = new List<LtFile>
            {
                new()
                {
                    ID = 20,
                    ParentID = 10,
                    Size = 5,
                    Name = "test.wad.client",
                    LanguageFlags = 1,
                    ChunkIDs = new List<ulong> { 0x1234 },
                    ChunkingParametersIndex = 0
                }
            },
            EncryptionKeys = new List<ReleaseManifestEncryptionKey>(),
            ChunkingParameters = new List<ReleaseManifestChunkingParameter>
            {
                new() { ID = 1, Version = (sbyte)HashType.Sha256 }
            }
        };
        configure?.Invoke(body);

        byte[] flatBuffer = new byte[ReleaseManifestBody.Serializer.GetMaxSize(body)];
        int bodySize = ReleaseManifestBody.Serializer.Write(flatBuffer, body);
        using var compressor = new Compressor();
        byte[] compressed = compressor.Wrap(flatBuffer.AsSpan(0, bodySize)).ToArray();
        byte[] rman = new byte[28 + compressed.Length];
        "RMAN"u8.CopyTo(rman);
        rman[4] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(rman.AsSpan(8, 4), 28);
        BinaryPrimitives.WriteUInt32LittleEndian(rman.AsSpan(12, 4), (uint)compressed.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(rman.AsSpan(16, 8), 0x1122334455667788ul);
        BinaryPrimitives.WriteUInt32LittleEndian(rman.AsSpan(24, 4), (uint)bodySize);
        compressed.CopyTo(rman, 28);
        return rman;
    }
}
