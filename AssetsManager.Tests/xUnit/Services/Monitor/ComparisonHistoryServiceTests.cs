using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Monitor;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Views.Models.Wad;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Monitor
{
    public sealed class ComparisonHistoryServiceTests
    {
        [Fact]
        public async Task SyncOrphanedArchivesRemovesGhostsWhenFolderDeletedFromDisk()
        {
            using var bridge = new AssetsManagerTestBridge();
            var settings = new AppSettings();
            
            // Register an entry whose physical folder does not exist on disk
            settings.DiffHistory.Add(new HistoryEntry
            {
                DisplayName = "Ghost Comparison",
                FileName = "Ghost Comparison",
                ReferenceId = "comparison_nonexistent_folder",
                Type = HistoryEntryType.WadArchive,
                Timestamp = DateTime.Now
            });

            var service = new ComparisonHistoryService(null, settings, bridge.Directories, bridge.LogService);

            var (recovered, removed) = await service.SyncOrphanedArchivesAsync();

            Assert.Equal(0, recovered);
            Assert.Equal(1, removed);
            Assert.Empty(settings.DiffHistory);
        }

        [Fact]
        public async Task SyncOrphanedArchivesImportsMissingFoldersFromDisk()
        {
            using var bridge = new AssetsManagerTestBridge();
            var settings = new AppSettings();

            string folderName = "comparison_01012026_120000";
            string folderPath = Path.Combine(bridge.Directories.WadComparisonSavePath, folderName);
            Directory.CreateDirectory(folderPath);

            var sampleData = new WadComparisonData
            {
                Version = "14.1.1",
                OldLolPath = "C:/Riot Games/Old",
                NewLolPath = "C:/Riot Games/League of Legends (PBE)"
            };
            string json = JsonSerializer.Serialize(sampleData);
            await File.WriteAllTextAsync(Path.Combine(folderPath, "wadcomparison.json"), json);

            var service = new ComparisonHistoryService(null, settings, bridge.Directories, bridge.LogService);

            var (recovered, removed) = await service.SyncOrphanedArchivesAsync();

            Assert.Equal(1, recovered);
            Assert.Equal(0, removed);
            Assert.Single(settings.DiffHistory);
            Assert.Equal(folderName, settings.DiffHistory[0].ReferenceId);
            Assert.Equal("League of Legends (PBE)", settings.DiffHistory[0].DisplayName);
        }
    }
}
