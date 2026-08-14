using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.BenchmarkTests.Infrastructure;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Views.Models.Settings;
using Xunit;

namespace AssetsManager.BenchmarkTests.Monitor
{
    public sealed class BackupManagerTests
    {
        [Fact]
        public void ConfiguredMainInstallationCannotBeDeleted()
        {
            using var bridge = new AssetsManagerTestBridge();
            string mainPath = bridge.CreateDirectory("League of Legends");
            string backupPath = bridge.CreateDirectory("League of Legends_old_20260726_120000");
            var settings = new AppSettings { LolLiveDirectory = mainPath };
            var manager = new BackupManager(bridge.Directories, bridge.LogService, settings, null);

            Assert.False(manager.CanDeleteBackup(mainPath));
            Assert.False(manager.DeleteBackup(mainPath));
            Assert.True(Directory.Exists(mainPath));
            Assert.True(manager.CanDeleteBackup(backupPath));
        }

        [Fact]
        public async Task BackupCopiesFilesAndReportsAccurateEstimate()
        {
            using var bridge = new AssetsManagerTestBridge();
            string sourcePath = bridge.CreateDirectory("League of Legends (PBE)");
            Directory.CreateDirectory(Path.Combine(sourcePath, "Game"));
            await File.WriteAllTextAsync(Path.Combine(sourcePath, "root.dat"), "12345");
            await File.WriteAllTextAsync(Path.Combine(sourcePath, "Game", "game.dat"), "1234567");
            string destinationPath = Path.Combine(bridge.RootPath, "League of Legends (PBE)_old_20260726_120000");
            var settings = new AppSettings { LolPbeDirectory = sourcePath };
            var manager = new BackupManager(bridge.Directories, bridge.LogService, settings, null);

            BackupManager.BackupStorageEstimate estimate = await manager.GetStorageEstimateAsync(
                sourcePath, destinationPath, CancellationToken.None);
            await manager.CreateLolPbeDirectoryBackupAsync(
                sourcePath,
                destinationPath,
                CancellationToken.None);

            Assert.Equal(2, estimate.FileCount);
            Assert.Equal(12, estimate.TotalBytes);
            Assert.Equal("12345", await File.ReadAllTextAsync(Path.Combine(destinationPath, "root.dat")));
            Assert.Equal("1234567", await File.ReadAllTextAsync(Path.Combine(destinationPath, "Game", "game.dat")));
            Assert.False(File.Exists(Path.Combine(destinationPath, ".assetsmanager-backup.json")));
        }

        [Fact]
        public async Task BackupAwaitsCompletionObserversBeforeReturning()
        {
            using var bridge = new AssetsManagerTestBridge();
            string sourcePath = bridge.CreateDirectory("League of Legends");
            await File.WriteAllTextAsync(Path.Combine(sourcePath, "game.dat"), "content");
            string destinationPath = Path.Combine(bridge.RootPath, "League of Legends_old_20260814_120000");
            var manager = new BackupManager(bridge.Directories, bridge.LogService, new AppSettings(), null);
            var observerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseObserver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            manager.BackupCompleted += async success =>
            {
                Assert.True(success);
                observerStarted.SetResult();
                await releaseObserver.Task;
            };

            Task backupTask = manager.CreateLolPbeDirectoryBackupAsync(
                sourcePath,
                destinationPath,
                CancellationToken.None);

            await observerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(backupTask.IsCompleted);

            releaseObserver.SetResult();
            await backupTask;
        }

        [Fact]
        public void InstallationOutsideConfiguredRootsIsIdentifiedAsMain()
        {
            using var bridge = new AssetsManagerTestBridge();
            string liveMain = bridge.CreateDirectory("League of Legends");
            string liveBackup = bridge.CreateDirectory("League of Legends_old_20260726_120000");
            var settings = new AppSettings { LolPbeDirectory = bridge.CreateDirectory("League of Legends (PBE)") };
            var manager = new BackupManager(bridge.Directories, bridge.LogService, settings, null);

            var (liveIsPbe, liveIsMain) = manager.GetPathIdentification(liveMain);
            var (backupIsPbe, backupIsMain) = manager.GetPathIdentification(liveBackup);

            Assert.False(liveIsPbe);
            Assert.True(liveIsMain);
            Assert.False(backupIsMain);
        }

        [Theory]
        [InlineData(PreferredClient.PBE, "pbe-main", "pbe-backup")]
        [InlineData(PreferredClient.LIVE, "live-main", "live-backup")]
        [InlineData(null, "pbe-main", "pbe-backup", "live-main", "live-backup")]
        public void EnvironmentFilterReturnsOnlyRequestedClients(
            PreferredClient? client,
            params string[] expectedPaths)
        {
            var backups = new[]
            {
                new BackupModel { Path = "pbe-main", IsPbe = true, IsMainClient = true },
                new BackupModel { Path = "pbe-backup", IsPbe = true },
                new BackupModel { Path = "live-main", IsPbe = false, IsMainClient = true },
                new BackupModel { Path = "live-backup", IsPbe = false }
            };

            string[] actualPaths = BackupManager.FilterByClient(backups, client)
                .Select(backup => backup.Path)
                .ToArray();

            Assert.Equal(expectedPaths, actualPaths);
        }

        [Fact]
        public async Task BackupsChangedFiresOnCreateCloneAndDelete()
        {
            using var bridge = new AssetsManagerTestBridge();
            string sourcePath = bridge.CreateDirectory("League of Legends (PBE)");
            string clonePath = Path.Combine(bridge.RootPath, "League of Legends (PBE)_old_20260814_111111");
            string backupPath = Path.Combine(bridge.RootPath, "League of Legends (PBE)_old_20260814_222222");
            var settings = new AppSettings { LolPbeDirectory = sourcePath };
            var manager = new BackupManager(bridge.Directories, bridge.LogService, settings, null);

            int eventCount = 0;
            manager.BackupsChanged += () => eventCount++;

            await manager.CreateLolPbeDirectoryBackupAsync(sourcePath, backupPath, CancellationToken.None);
            Assert.Equal(1, eventCount);

            await manager.CloneBackupAsync(backupPath, clonePath, CancellationToken.None);
            Assert.Equal(2, eventCount);

            Assert.True(manager.DeleteBackup(clonePath));
            Assert.Equal(3, eventCount);
        }
    }
}
