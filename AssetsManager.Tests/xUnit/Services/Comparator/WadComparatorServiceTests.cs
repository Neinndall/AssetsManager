using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Views.Models.Wad;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Comparator
{
    public sealed class WadComparatorServiceTests
    {
        [Fact]
        public async Task ComparisonReportsModifiedNewRemovedAndRenamedChunks()
        {
            using var bridge = new AssetsManagerTestBridge();
            string oldDirectory = bridge.CreateDirectory("old");
            string newDirectory = bridge.CreateDirectory("new");
            string oldWad = bridge.BakeWad(oldDirectory, "test.wad",
                ("assets/shared.json", "old"),
                ("assets/removed.json", "removed"),
                ("assets/old-name.json", "renamed"));
            string newWad = bridge.BakeWad(newDirectory, "test.wad",
                ("assets/shared.json", "new"),
                ("assets/added.json", "added"),
                ("assets/new-name.json", "renamed"));
            var comparator = bridge.CreateComparator();
            List<ChunkDiff> result = null;
            comparator.ComparisonCompleted += (diffs, _, _, _) =>
            {
                result = diffs;
                return Task.CompletedTask;
            };

            await comparator.CompareSingleWadAsync(oldWad, newWad, "test", CancellationToken.None);

            Assert.NotNull(result);
            Assert.Contains(result, diff => diff.Type == ChunkDiffType.Modified);
            Assert.Contains(result, diff => diff.Type == ChunkDiffType.New);
            Assert.Contains(result, diff => diff.Type == ChunkDiffType.Removed);
            Assert.Contains(result, diff => diff.Type == ChunkDiffType.Renamed);
            Assert.Equal(4, result.Count);
        }

        [Fact]
        public async Task CancelledComparisonPropagatesCancellationAndReportsNoResult()
        {
            using var bridge = new AssetsManagerTestBridge();
            var comparator = bridge.CreateComparator();
            List<ChunkDiff> result = new();
            comparator.ComparisonCompleted += (diffs, _, _, _) =>
            {
                result = diffs;
                return Task.CompletedTask;
            };
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                comparator.CompareSingleWadAsync("old.wad", "new.wad", "test", cancellation.Token));

            Assert.Null(result);
        }

        [Fact]
        public async Task IdenticalContentIsPairedOneToOneWhenOnlySomePathsAreRenamed()
        {
            using var bridge = new AssetsManagerTestBridge();
            string oldDirectory = bridge.CreateDirectory("old");
            string newDirectory = bridge.CreateDirectory("new");
            string oldWad = bridge.BakeWad(oldDirectory, "test.wad",
                ("assets/old-a.json", "same"),
                ("assets/old-b.json", "same"));
            string newWad = bridge.BakeWad(newDirectory, "test.wad",
                ("assets/new-a.json", "same"));
            var comparator = bridge.CreateComparator();
            List<ChunkDiff> result = null;
            comparator.ComparisonCompleted += (diffs, _, _, _) =>
            {
                result = diffs;
                return Task.CompletedTask;
            };

            await comparator.CompareSingleWadAsync(oldWad, newWad, "test", CancellationToken.None);

            Assert.Single(result, diff => diff.Type == ChunkDiffType.Renamed);
            Assert.Single(result, diff => diff.Type == ChunkDiffType.Removed);
            Assert.Equal(2, result.Count);
        }

        [Fact]
        public async Task FailedSingleWadComparisonReportsNullResultInsteadOfNoDifferences()
        {
            using var bridge = new AssetsManagerTestBridge();
            var comparator = bridge.CreateComparator();
            List<ChunkDiff> result = new();
            comparator.ComparisonCompleted += (diffs, _, _, _) =>
            {
                result = diffs;
                return Task.CompletedTask;
            };

            string missingOldWad = System.IO.Path.Combine(bridge.RootPath, "missing-old.wad");
            string missingNewWad = System.IO.Path.Combine(bridge.RootPath, "missing-new.wad");

            await comparator.CompareSingleWadAsync(missingOldWad, missingNewWad, "test", CancellationToken.None);

            Assert.Null(result);

            string lockedOldWad = System.IO.Path.Combine(bridge.RootPath, "locked-old.wad");
            string lockedNewWad = System.IO.Path.Combine(bridge.RootPath, "locked-new.wad");
            System.IO.File.WriteAllText(lockedNewWad, "placeholder");

            using (new System.IO.FileStream(lockedOldWad, System.IO.FileMode.Create, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None))
            {
                result = new();
                await comparator.CompareSingleWadAsync(lockedOldWad, lockedNewWad, "test", CancellationToken.None);
            }

            Assert.Null(result);
        }

        [Fact]
        public async Task FailedDirectoryComparisonReportsNullResultInsteadOfNoDifferences()
        {
            using var bridge = new AssetsManagerTestBridge();
            var comparator = bridge.CreateComparator();
            List<ChunkDiff> result = new();
            comparator.ComparisonCompleted += (diffs, _, _, _) =>
            {
                result = diffs;
                return Task.CompletedTask;
            };

            string missingOldDir = System.IO.Path.Combine(bridge.RootPath, "missing-old-dir");
            string missingNewDir = System.IO.Path.Combine(bridge.RootPath, "missing-new-dir");

            await comparator.CompareWadsAsync(missingOldDir, missingNewDir, "test", CancellationToken.None);

            Assert.Null(result);
        }
    }
}
