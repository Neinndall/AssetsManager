using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;

namespace AssetsManager.Utils
{
    public static class PerformanceBenchmark
    {
        public static async Task TestFindSummonerIconsJson(LogService logService, AppSettings appSettings)
        {
            logService.Log("Starting Benchmark (Optimized): Searching for summoner-icons.json WITHOUT HashResolver...");

            string virtualPath = "plugins/rcp-be-lol-game-data/global/default/v1/summoner-icons.json";
            string normalizedPath = virtualPath.Replace('\\', '/').ToLowerInvariant();
            
            // Calculate target hash once using LeagueToolkit's hasher
            ulong targetHash = LeagueToolkit.Hashing.XxHash64Ext.Hash(normalizedPath);
            logService.Log($"Target Hash for search: {targetHash:X16}");

            string gameDataPath = appSettings.PreferredClient == Views.Models.Settings.PreferredClient.PBE ? appSettings.LolPbeDirectory : appSettings.LolLiveDirectory;
            if (string.IsNullOrEmpty(gameDataPath) || !Directory.Exists(gameDataPath))
            {
                logService.LogWarning("Benchmark Failed: Client path not configured.");
                return;
            }

            try
            {
                var wadFiles = Directory.GetFiles(gameDataPath, "*.wad", SearchOption.AllDirectories)
                                              .Concat(Directory.GetFiles(gameDataPath, "*.wad.client", SearchOption.AllDirectories))
                                              .ToList();

                bool found = false;
                foreach (var wadPath in wadFiles)
                {
                    try 
                    {
                        using (var wadFile = new LeagueToolkit.Core.Wad.WadFile(wadPath))
                        {
                            if (wadFile.Chunks.TryGetValue(targetHash, out var chunk))
                            {
                                logService.Log($"[SUCCESS] Found via Direct Hash in: {Path.GetFileName(wadPath)}");
                                logService.Log($"[SUCCESS] Chunk Hash: {chunk.PathHash:X16}");
                                found = true;
                                break;
                            }
                        }
                    }
                    catch { /* Skip corrupt WADs during benchmark */ }
                }

                if (!found)
                {
                    logService.LogWarning($"[FAILURE] File NOT found using hash {targetHash:X16} in {gameDataPath}");
                }
            }
            catch (Exception ex)
            {
                logService.LogError(ex, "Benchmark Error during hash lookup.");
            }
        }
    }
}
