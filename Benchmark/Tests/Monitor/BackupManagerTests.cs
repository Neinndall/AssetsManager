using System.IO;
using System.Text.Json;
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
        public async Task BackupWritesCompleteManifestAndAccurateEstimate()
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
                CancellationToken.None,
                displayName: "Preseason Snapshot");

            Assert.Equal(2, estimate.FileCount);
            Assert.Equal(12, estimate.TotalBytes);
            string manifestPath = Path.Combine(destinationPath, BackupManager.ManifestFileName);
            Assert.True(File.Exists(manifestPath));
            BackupManifest manifest = JsonSerializer.Deserialize<BackupManifest>(
                await File.ReadAllTextAsync(manifestPath));
            Assert.Equal("Preseason Snapshot", manifest.DisplayName);
            Assert.Equal("PBE", manifest.Environment);
            Assert.Equal("Complete", manifest.Status);
            Assert.Equal(2, manifest.FileCount);
            Assert.Equal(12, manifest.TotalBytes);
        }
    }
}
