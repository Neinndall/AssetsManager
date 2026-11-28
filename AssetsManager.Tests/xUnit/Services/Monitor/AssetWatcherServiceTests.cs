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

        [Fact]
        public async Task CheckAssetsAsyncHandlesLiveCollectionMutationDuringCheck()
        {
            using var bridge = new AssetsManagerTestBridge();
            string wadDir = bridge.CreateDirectory("pbe");
            string wadPath = bridge.BakeWad(wadDir, "test.wad.client",
                ("assets/first.json", "{\"v\":2}"),
                ("assets/second.json", "{\"v\":2}"));

            string previousCwd = Environment.CurrentDirectory;
            Environment.CurrentDirectory = bridge.RootPath;
            try
            {
                var settings = AppSettings.GetDefaultSettings();
                var liveAssets = new AssetsManager.Utils.Framework.ObservableRangeCollection<MonitoredAsset>
                {
                    new MonitoredAsset
                    {
                        AssetPath = "Game/test.wad.client/assets/first.json",
                        Alias = "first.json",
                        WadName = wadPath,
                        InternalPath = "assets/first.json",
                        LastKnownHash = 1
                    },
                    new MonitoredAsset
                    {
                        AssetPath = "Game/test.wad.client/assets/second.json",
                        Alias = "second.json",
                        WadName = wadPath,
                        InternalPath = "assets/second.json",
                        LastKnownHash = 1
                    }
                };

                var watcher = new AssetWatcherService(
                    settings,
                    bridge.LogService,
                    bridge.Directories,
                    wadContentProvider: null,
                    versionService: null,
                    backupManager: null);

                watcher.AssetUpdated += _ => liveAssets.Clear();

                var (anyUpdated, updatedNames) = await watcher.CheckAssetsAsync(liveAssets, silent: true);

                Assert.True(anyUpdated);
                Assert.Equal(2, updatedNames.Count);
            }
            finally
            {
                Environment.CurrentDirectory = previousCwd;
            }
        }

        [Fact]
        public async Task AssetsWithSameFilenameUseDistinctCacheFilesWithoutOverwriting()
        {
            using var bridge = new AssetsManagerTestBridge();
            string wadDir = bridge.CreateDirectory("pbe");
            string ahriWad = bridge.BakeWad(wadDir, "Ahri.wad.client", ("data/characters/ahri/skins/skin0.bin", "AHRI_DATA"));
            string luxWad = bridge.BakeWad(wadDir, "Lux.wad.client", ("data/characters/lux/skins/skin0.bin", "LUX_DATA"));

            string previousCwd = Environment.CurrentDirectory;
            Environment.CurrentDirectory = bridge.RootPath;
            try
            {
                var settings = AppSettings.GetDefaultSettings();
                var ahriAsset = new MonitoredAsset
                {
                    AssetPath = "Game/Ahri.wad.client/data/characters/ahri/skins/skin0.bin",
                    Alias = "skin0.bin",
                    WadName = ahriWad,
                    InternalPath = "data/characters/ahri/skins/skin0.bin",
                    LastKnownHash = 0
                };
                var luxAsset = new MonitoredAsset
                {
                    AssetPath = "Game/Lux.wad.client/data/characters/lux/skins/skin0.bin",
                    Alias = "skin0.bin",
                    WadName = luxWad,
                    InternalPath = "data/characters/lux/skins/skin0.bin",
                    LastKnownHash = 0
                };

                var watcher = new AssetWatcherService(
                    settings,
                    bridge.LogService,
                    bridge.Directories,
                    wadContentProvider: null,
                    versionService: null,
                    backupManager: null);

                await watcher.CheckAssetsAsync(new List<MonitoredAsset> { ahriAsset, luxAsset }, silent: true);

                Assert.NotEqual(ahriAsset.NewFilePath, luxAsset.NewFilePath);
                Assert.NotEqual(ahriAsset.OldFilePath, luxAsset.OldFilePath);
                Assert.EndsWith("skin0.bin", ahriAsset.NewFilePath);
                Assert.EndsWith("skin0.bin", luxAsset.NewFilePath);
                Assert.Equal("AHRI_DATA", File.ReadAllText(ahriAsset.NewFilePath));
                Assert.Equal("LUX_DATA", File.ReadAllText(luxAsset.NewFilePath));
            }
            finally
            {
                Environment.CurrentDirectory = previousCwd;
            }
        }
    }
}
