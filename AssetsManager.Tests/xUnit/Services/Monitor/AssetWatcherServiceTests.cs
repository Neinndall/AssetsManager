using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AssetsManager.Services.Monitor;
using AssetsManager.Tests.xUnit.Infrastructure;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Monitor
{
    public sealed class AssetWatcherServiceTests
    {
        [Fact]
        public async Task InitialBaselineCheckPersistsLastKnownHashToSettings()
        {
            using var bridge = new AssetsManagerTestBridge();
            string wadDir = bridge.CreateDirectory("pbe");
            string wadPath = bridge.BakeWad(wadDir, "test.wad.client", ("assets/sample.json", "{\"v\":1}"));

            string previousCwd = Environment.CurrentDirectory;
            Environment.CurrentDirectory = bridge.RootPath;
            try
            {
                var settings = AppSettings.GetDefaultSettings();
                var asset = new MonitoredAsset
                {
                    AssetPath = "Game/test.wad.client/assets/sample.json",
                    Alias = "sample.json",
                    WadName = wadPath,
                    InternalPath = "assets/sample.json",
                    LastKnownHash = 0
                };
                settings.MonitoredAssets = new List<MonitoredAsset> { asset };

                var watcher = new AssetWatcherService(
                    settings,
                    bridge.LogService,
                    bridge.Directories,
                    wadContentProvider: null,
                    versionService: null,
                    backupManager: null);

                var (anyUpdated, updatedNames) = await watcher.CheckAssetsAsync(settings.MonitoredAssets, silent: true);

                Assert.False(anyUpdated);
                Assert.Empty(updatedNames);
                Assert.NotEqual(0UL, asset.LastKnownHash);
                Assert.Equal(AssetStatus.UpToDate, asset.Status);

                // Verify the initial baseline hash was actually written to config.json on disk
                string configPath = Path.Combine(bridge.RootPath, "config.json");
                Assert.True(File.Exists(configPath));
                var reloaded = AppSettings.LoadSettings();
                Assert.Single(reloaded.MonitoredAssets);
                Assert.Equal(asset.LastKnownHash, reloaded.MonitoredAssets[0].LastKnownHash);

                // Patching the WAD after a simulated restart must now be detected as an update
                bridge.BakeWad(wadDir, "test.wad.client", ("assets/sample.json", "{\"v\":2}"));
                var restartedWatcher = new AssetWatcherService(
                    reloaded,
                    bridge.LogService,
                    bridge.Directories,
                    wadContentProvider: null,
                    versionService: null,
                    backupManager: null);

                var (patchDetected, patchedNames) = await restartedWatcher.CheckAssetsAsync(reloaded.MonitoredAssets, silent: true);
                Assert.True(patchDetected);
                Assert.Single(patchedNames, "sample.json");
                Assert.NotEqual(asset.LastKnownHash, reloaded.MonitoredAssets[0].LastKnownHash);
            }
            finally
            {
                Environment.CurrentDirectory = previousCwd;
            }
        }
    }
}
