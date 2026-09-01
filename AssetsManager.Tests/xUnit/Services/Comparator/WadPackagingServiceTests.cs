using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Views.Models.Wad;
using LeagueToolkit.Core.Wad;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Comparator
{
    public sealed class WadPackagingServiceTests
    {
        [Fact]
        public async Task CancelledPackagingDoesNotCreateChunkOutput()
        {
            using var bridge = new AssetsManagerTestBridge();
            string output = Path.Combine(bridge.RootPath, "output");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                bridge.CreatePackager().SaveBackupAsync(new List<SerializableChunkDiff>(), "old", "new", output, cancellationToken: cancellation.Token));

            Assert.False(Directory.Exists(output));
        }

        [Fact]
        public async Task PackagingRejectsMissingSourceWad()
        {
            using var bridge = new AssetsManagerTestBridge();
            var diffs = new[]
            {
                new SerializableChunkDiff
                {
                    Type = ChunkDiffType.New,
                    NewPath = "assets/test.json",
                    NewPathHash = 1,
                    SourceWadFile = "missing.wad"
                }
            };

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                bridge.CreatePackager().CreateLeanWadPackageAsync(
                    diffs,
                    bridge.CreateDirectory("old"),
                    bridge.CreateDirectory("new"),
                    bridge.CreateDirectory("old-output"),
                    bridge.CreateDirectory("new-output")));
        }

        [Fact]
        public async Task PackagingAllowsMissingOldWadForNewChunk()
        {
            using var bridge = new AssetsManagerTestBridge();
            string oldDirectory = bridge.CreateDirectory("old");
            string newDirectory = bridge.CreateDirectory("new");
            string wadPath = bridge.BakeWad(newDirectory, "test.wad", ("assets/test.json", "new content"));
            ulong newPathHash;
            using (var wad = new WadFile(wadPath))
            {
                newPathHash = wad.Chunks.Keys.Single();
            }

            var diffs = new[]
            {
                new SerializableChunkDiff
                {
                    Type = ChunkDiffType.New,
                    NewPath = "assets/test.json",
                    NewPathHash = newPathHash,
                    SourceWadFile = "test.wad"
                }
            };
            string newOutput = bridge.CreateDirectory("new-output");

            await bridge.CreatePackager().CreateLeanWadPackageAsync(
                diffs,
                oldDirectory,
                newDirectory,
                bridge.CreateDirectory("old-output"),
                newOutput);

            Assert.True(File.Exists(Path.Combine(newOutput, "test.wad", $"{newPathHash:X16}.chunk")));
        }

        [Fact]
        public async Task PackagingRejectsMissingOldWadForModifiedChunk()
        {
            using var bridge = new AssetsManagerTestBridge();
            string newDirectory = bridge.CreateDirectory("new");
            string wadPath = bridge.BakeWad(newDirectory, "test.wad", ("assets/test.json", "new content"));
            ulong pathHash;
            using (var wad = new WadFile(wadPath))
            {
                pathHash = wad.Chunks.Keys.Single();
            }

            var diffs = new[]
            {
                new SerializableChunkDiff
                {
                    Type = ChunkDiffType.Modified,
                    OldPath = "assets/test.json",
                    NewPath = "assets/test.json",
                    OldPathHash = pathHash,
                    NewPathHash = pathHash,
                    SourceWadFile = "test.wad"
                }
            };

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                bridge.CreatePackager().CreateLeanWadPackageAsync(
                    diffs,
                    bridge.CreateDirectory("old"),
                    newDirectory,
                    bridge.CreateDirectory("old-output"),
                    bridge.CreateDirectory("new-output")));
        }
    }
}
