using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetsManager.Services.Monitor;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Views.Models.Versions;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Monitor
{
    public sealed class VersionServiceTests
    {
        [Fact]
        public async Task DeleteVersionFilesDeletesOnlyMatchingCategoryWhenFilenamesCollide()
        {
            using var bridge = new AssetsManagerTestBridge();
            string leagueDir = Path.Combine(bridge.Directories.VersionsPath, "PBE1", "windows", "league-client");
            string gameDir = Path.Combine(bridge.Directories.VersionsPath, "PBE1", "windows", "lol-game-client");
            Directory.CreateDirectory(leagueDir);
            Directory.CreateDirectory(gameDir);

            string leagueFile = Path.Combine(leagueDir, "26.15.1.txt");
            string gameFile = Path.Combine(gameDir, "26.15.1.txt");
            File.WriteAllText(leagueFile, "league-manifest");
            File.WriteAllText(gameFile, "game-manifest");

            var service = new VersionService(bridge.LogService, null, bridge.Directories, null, null, null);
            var loaded = await service.GetVersionFilesAsync();
            var gameEntry = loaded.Single(f => f.Category == "lol-game-client");

            Assert.Equal(gameFile, gameEntry.FilePath);

            bool deleted = service.DeleteVersionFiles(new[] { gameEntry });

            Assert.True(deleted);
            Assert.False(File.Exists(gameFile));
            Assert.True(File.Exists(leagueFile));

            // Also verify fallback by Category when FilePath is not populated
            File.WriteAllText(gameFile, "game-manifest-2");
            bool deletedByCategory = service.DeleteVersionFiles(new[]
            {
                new VersionFileInfo { FileName = "26.15.1.txt", Category = "lol-game-client" }
            });

            Assert.True(deletedByCategory);
            Assert.False(File.Exists(gameFile));
            Assert.True(File.Exists(leagueFile));
        }
    }
}
