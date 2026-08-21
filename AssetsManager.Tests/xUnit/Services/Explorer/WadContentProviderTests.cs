using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Parsers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Wad;
using LeagueToolkit.Core.Wad;
using Serilog;
using Xunit;
using ZstdSharp;

namespace AssetsManager.Tests.xUnit.Services.Explorer
{
    public sealed class WadContentProviderTests
    {
        [Fact]
        public void ResolveWadPathKeepsSingleWadWhenSourceEndsWithSeparator()
        {
            string wadPath = Path.Combine(Path.GetTempPath(), "Katarina.wad.client");
            string sourcePath = wadPath + Path.DirectorySeparatorChar;

            string resolvedPath = PathUtils.ResolveWadPath(sourcePath, "Katarina.wad.client");

            Assert.Equal(wadPath, resolvedPath);
        }

        [Fact]
        public void ResolveWadPathRejectsDifferentWadForSingleWadSource()
        {
            string wadPath = Path.Combine(Path.GetTempPath(), "Katarina.wad.client");

            string resolvedPath = PathUtils.ResolveWadPath(wadPath, "Ahri.wad.client");

            Assert.Null(resolvedPath);
        }

        [Fact]
        public async Task BatchLookupResolvesRequestedPathsAcrossWads()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            const string firstPath = "assets/test/first.json";
            const string secondPath = "assets/test/second.json";

            try
            {
                WadBuilder.Bake(
                    new[] { new WadBakeEntry(firstPath, () => new MemoryStream(Encoding.UTF8.GetBytes("first")), WadChunkCompression.None) },
                    Path.Combine(directory, "first.wad"),
                    new WadBakeSettings());
                WadBuilder.Bake(
                    new[] { new WadBakeEntry(secondPath, () => new MemoryStream(Encoding.UTF8.GetBytes("second")), WadChunkCompression.None) },
                    Path.Combine(directory, "second.wad"),
                    new WadBakeSettings());

                var provider = CreateProvider();
                var nodes = await provider.FindNodesByVirtualPathsAsync(
                    new[] { firstPath, secondPath, "assets/test/missing.json" },
                    directory);

                Assert.Equal(2, nodes.Count);
                Assert.Equal(Path.Combine(directory, "first.wad"), nodes[firstPath].SourceWadPath);
                Assert.Equal(Path.Combine(directory, "second.wad"), nodes[secondPath].SourceWadPath);
                Assert.DoesNotContain("assets/test/missing.json", nodes.Keys);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public async Task LookupResolvesEventHubFromDefaultAssetsWad()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string wadPath = Path.Combine(directory, "default-assets2.wad");

            try
            {
                WadBuilder.Bake(
                    new[] { new WadBakeEntry(RiotCatalogDefinitions.EventHubJsonPath, () => new MemoryStream(Encoding.UTF8.GetBytes("[]")), WadChunkCompression.None) },
                    wadPath,
                    new WadBakeSettings());

                var provider = CreateProvider();
                var node = await provider.FindNodeByVirtualPathAsync(RiotCatalogDefinitions.EventHubJsonPath, directory);

                Assert.NotNull(node);
                Assert.Equal(wadPath, node.SourceWadPath);
                Assert.Equal(RiotCatalogDefinitions.EventHubJsonPath, node.VirtualPath);
                Assert.Equal(Encoding.UTF8.GetBytes("[]"), await provider.GetVirtualFileBytesAsync(node));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public async Task SingleLookupAcceptsBackslashSeparatedPath()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string wadPath = Path.Combine(directory, "paths.wad");

            try
            {
                const string virtualPath = "assets/test/first.json";
                WadBuilder.Bake(
                    new[] { new WadBakeEntry(virtualPath, () => new MemoryStream(Encoding.UTF8.GetBytes("content")), WadChunkCompression.None) },
                    wadPath,
                    new WadBakeSettings());

                var provider = CreateProvider();
                var node = await provider.FindNodeByVirtualPathAsync("assets\\test\\first.json", directory);

                Assert.NotNull(node);
                Assert.Equal(virtualPath, node.VirtualPath);
                Assert.Equal(wadPath, node.SourceWadPath);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public async Task BackupChunkReadingReturnsOriginalBytes()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            const string sourceWad = "default-assets2.wad";
            const ulong hash = 0x1234UL;
            byte[] expected = Encoding.UTF8.GetBytes("backup chunk payload");

            try
            {
                string chunkDirectory = Path.Combine(directory, "wad_chunks", "new", sourceWad);
                Directory.CreateDirectory(chunkDirectory);
                await File.WriteAllBytesAsync(Path.Combine(chunkDirectory, $"{hash:X16}.chunk"), expected);

                var provider = CreateProvider();
                byte[] actual = await provider.GetBackupChunkBytesAsync(directory, sourceWad, hash, WadChunkCompression.None, false);

                Assert.Equal(expected, actual);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public async Task ChangedWadContentIsReadWithoutRetainedBytes()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string wadPath = Path.Combine(directory, "changing.wad");
            const string virtualPath = "assets/test/changing.json";

            try
            {
                WadBuilder.Bake(
                    new[] { new WadBakeEntry(virtualPath, () => new MemoryStream(Encoding.UTF8.GetBytes("first!")), WadChunkCompression.None) },
                    wadPath,
                    new WadBakeSettings());

                var provider = CreateProvider();
                var node = await provider.FindNodeByVirtualPathAsync(virtualPath, directory);
                Assert.Equal("first!", Encoding.UTF8.GetString(await provider.GetVirtualFileBytesAsync(node)));

                WadBuilder.Bake(
                    new[] { new WadBakeEntry(virtualPath, () => new MemoryStream(Encoding.UTF8.GetBytes("second")), WadChunkCompression.None) },
                    wadPath,
                    new WadBakeSettings());
                File.SetLastWriteTimeUtc(wadPath, DateTime.UtcNow.AddSeconds(2));

                Assert.Equal("second", Encoding.UTF8.GetString(await provider.GetVirtualFileBytesAsync(node)));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public async Task BackupChunkReadingLoadsChunkedPayloadWithoutMetadata()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            const string sourceWad = "champions/test.wad.client";
            const ulong hash = 0xABCDEFUL;
            byte[] expected = Encoding.UTF8.GetBytes(new string('a', 4096));
            using var compressor = new Compressor();
            byte[] stored = compressor.Wrap(expected).ToArray();

            try
            {
                Directory.CreateDirectory(directory);
                string chunkDirectory = Path.Combine(directory, "wad_chunks", "new", sourceWad);
                Directory.CreateDirectory(chunkDirectory);
                string chunkPath = Path.Combine(chunkDirectory, $"{hash:X16}.chunk");
                await File.WriteAllBytesAsync(chunkPath, stored);

                var provider = CreateProvider();
                byte[] actual = await provider.GetBackupChunkBytesAsync(
                    directory,
                    sourceWad,
                    hash,
                    WadChunkCompression.ZstdChunked,
                    isOld: false,
                    uncompressedSize: (ulong)expected.Length);

                Assert.Equal(expected, actual);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public async Task BackupChunkReadingLoadsLegacyZstdPayloadWithoutSizeMetadata()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            const string sourceWad = "champions/test.wad.client";
            const string virtualPath = "assets/test/events.bnk";
            byte[] expected = Encoding.UTF8.GetBytes(new string('e', 16384));
            string wadPath = Path.Combine(directory, "source.wad");

            try
            {
                Directory.CreateDirectory(directory);
                WadBuilder.Bake(
                    new[] { new WadBakeEntry(virtualPath, () => new MemoryStream(expected), WadChunkCompression.Zstd) },
                    wadPath,
                    new WadBakeSettings());

                ulong hash = LeagueToolkit.Hashing.XxHash64Ext.Hash(virtualPath);
                using var wad = new WadFile(wadPath);
                WadChunk chunk = wad.Chunks[hash];
                byte[] stored = new byte[chunk.CompressedSize];
                using (var stream = File.OpenRead(wadPath))
                {
                    stream.Position = chunk.DataOffset;
                    await stream.ReadExactlyAsync(stored);
                }

                string chunkDirectory = Path.Combine(directory, "wad_chunks", "new", sourceWad);
                Directory.CreateDirectory(chunkDirectory);
                await File.WriteAllBytesAsync(Path.Combine(chunkDirectory, $"{hash:X16}.chunk"), stored);

                var provider = CreateProvider();
                byte[] actual = await provider.GetBackupChunkBytesAsync(
                    directory,
                    sourceWad,
                    hash,
                    WadChunkCompression.Zstd,
                    isOld: false);

                Assert.Equal(expected, actual);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public async Task LiveZstdChunkUsesWadMetadataForDecompression()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string wadPath = Path.Combine(directory, "zstd.wad");
            const string virtualPath = "assets/test/zstd.json";
            byte[] expected = Encoding.UTF8.GetBytes(new string('x', 16384));

            try
            {
                WadBuilder.Bake(
                    new[] { new WadBakeEntry(virtualPath, () => new MemoryStream(expected), WadChunkCompression.Zstd) },
                    wadPath,
                    new WadBakeSettings());

                var provider = CreateProvider();
                var node = await provider.FindNodeByVirtualPathAsync(virtualPath, directory);

                Assert.Equal(expected, await provider.GetVirtualFileBytesAsync(node));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public async Task GalleryThumbnailRendersArchivedSvg()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            const string sourceWad = "default-assets.wad.client";
            const ulong hash = 0x12345678UL;
            byte[] svg = Encoding.UTF8.GetBytes(
                "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"32\" height=\"32\"><rect width=\"32\" height=\"32\" fill=\"#44aaee\"/></svg>");

            try
            {
                string chunkDirectory = Path.Combine(directory, "wad_chunks", "new", sourceWad);
                Directory.CreateDirectory(chunkDirectory);
                string chunkPath = Path.Combine(chunkDirectory, $"{hash:X16}.chunk");
                await File.WriteAllBytesAsync(chunkPath, svg);

                var diff = new SerializableChunkDiff
                {
                    Type = ChunkDiffType.New,
                    NewPath = "assets/test/icon.svg",
                    NewPathHash = hash,
                    NewCompressionType = WadChunkCompression.None,
                    NewUncompressedSize = (ulong)svg.Length,
                    SourceWadFile = sourceWad,
                    BackupChunkPath = chunkPath
                };

                var provider = CreateProvider();
                var thumbnail = await provider.GetDiffThumbnailAsync(diff, null, null, 256);

                Assert.NotNull(thumbnail);
                Assert.True(thumbnail.IsFrozen);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public async Task BackupChunkReadingPropagatesCancellation()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var provider = CreateProvider();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                provider.GetBackupChunkBytesAsync("unused", "unused.wad", 1, WadChunkCompression.None, false, cancellation.Token));
        }

        private static WadContentProvider CreateProvider()
        {
            return new WadContentProvider(new LogService(Log.Logger), null, null, new SvgParser());
        }
    }
}
