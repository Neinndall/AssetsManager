using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Utils;
using Serilog;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Explorer
{
    public sealed class MediaTempFileStoreTests
    {
        [Fact]
        public async Task CancelledCreationDoesNotLeaveATemporaryFile()
        {
            string rootPath = Path.Combine(Path.GetTempPath(), $"AssetsManager_MediaTemp_{Guid.NewGuid():N}");
            var store = CreateStore(rootPath);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    store.CreateAsync(new byte[1024], ".ogg", cancellation.Token));

                Assert.Empty(Directory.GetFiles(Path.Combine(rootPath, "webview2data", "TempPreview")));
                Assert.Equal(0, store.PendingCount);
            }
            finally
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, true);
                }
            }
        }

        [Fact]
        public async Task LockedFileIsRetriedAfterTheHandleIsReleased()
        {
            string rootPath = Path.Combine(Path.GetTempPath(), $"AssetsManager_MediaTemp_{Guid.NewGuid():N}");
            var store = CreateStore(rootPath);
            string filePath = await store.CreateAsync(new byte[] { 1, 2, 3 }, ".ogg", CancellationToken.None);

            try
            {
                using (File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    store.DeleteOrDefer(filePath);
                    Assert.True(File.Exists(filePath));
                    Assert.Equal(1, store.PendingCount);
                }

                store.RetryPending(true);

                Assert.False(File.Exists(filePath));
                Assert.Equal(0, store.PendingCount);
            }
            finally
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, true);
                }
            }
        }

        [Fact]
        public async Task RetiredActiveFileIsRemovedOnTheNextRetry()
        {
            string rootPath = Path.Combine(Path.GetTempPath(), $"AssetsManager_MediaTemp_{Guid.NewGuid():N}");
            var store = CreateStore(rootPath);
            string filePath = await store.CreateAsync(new byte[] { 4, 5, 6 }, ".webm", CancellationToken.None);

            try
            {
                store.Activate(filePath);
                store.RetireActive();

                Assert.True(File.Exists(filePath));
                Assert.Equal(1, store.PendingCount);

                store.RetryPending();

                Assert.False(File.Exists(filePath));
                Assert.Equal(0, store.PendingCount);
            }
            finally
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, true);
                }
            }
        }

        [Fact]
        public async Task ReleaseRemovesTheActiveMediaPreview()
        {
            string rootPath = Path.Combine(Path.GetTempPath(), $"AssetsManager_MediaTemp_{Guid.NewGuid():N}");
            var store = CreateStore(rootPath);
            string filePath = await store.CreateAsync(new byte[] { 7, 8, 9 }, ".ogg", CancellationToken.None);

            try
            {
                store.Activate(filePath);

                store.Release();

                Assert.False(File.Exists(filePath));
                Assert.Equal(0, store.PendingCount);
            }
            finally
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, true);
                }
            }
        }

        private static MediaTempFileStore CreateStore(string rootPath)
        {
            return new MediaTempFileStore(
                new DirectoriesCreator(rootPath),
                new LogService(Log.Logger));
        }
    }
}
