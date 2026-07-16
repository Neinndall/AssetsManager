using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.BenchmarkTests.Infrastructure;
using AssetsManager.Views.Models.Wad;
using Xunit;

namespace AssetsManager.BenchmarkTests.Tests.Comparator
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
    }
}
