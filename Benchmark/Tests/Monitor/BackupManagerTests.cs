using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.BenchmarkTests.Infrastructure;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;
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

        [Theory]
        [InlineData(BackupEnvironment.Pbe, "pbe-main", "pbe-backup")]
        [InlineData(BackupEnvironment.Live, "live-main", "live-backup")]
        [InlineData(BackupEnvironment.All, "pbe-main", "pbe-backup", "live-main", "live-backup")]
        public void EnvironmentFilterReturnsOnlyRequestedClients(
            BackupEnvironment environment,
            params string[] expectedPaths)
        {
            var backups = new[]
            {
                new BackupModel { Path = "pbe-main", IsPbe = true, IsMainClient = true },
                new BackupModel { Path = "pbe-backup", IsPbe = true },
                new BackupModel { Path = "live-main", IsPbe = false, IsMainClient = true },
                new BackupModel { Path = "live-backup", IsPbe = false }
            };

            string[] actualPaths = BackupManager.FilterByEnvironment(backups, environment)
                .Select(backup => backup.Path)
                .ToArray();

            Assert.Equal(expectedPaths, actualPaths);
        }
    }
}
