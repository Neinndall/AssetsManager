using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AssetsManager.Utils;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Settings;

namespace AssetsManager.Services.Explorer
{
    public class SummonerIconMetadataService
    {
        private readonly LogService _logService;
        private readonly WadContentProvider _wadContentProvider;
        private readonly AppSettings _appSettings;
        private List<SummonerIconJsonEntry> _cachedMetadata;
        private string _cachedSourceWad;

        private const string JsonPath = "plugins/rcp-be-lol-game-data/global/default/v1/summoner-icons.json";
        private const string ProfileIconsPath = "v1/profile-icons/";

        public SummonerIconMetadataService(LogService logService, WadContentProvider wadContentProvider, AppSettings appSettings)
        {
            _logService = logService;
            _wadContentProvider = wadContentProvider;
            _appSettings = appSettings;
        }

        public async Task<SummonerIconMetadata> GetMetadataAsync(FileSystemNodeModel node)
        {
            if (node == null || string.IsNullOrEmpty(node.FullPath)) return null;

            string path = node.FullPath.Replace('\\', '/');
            if (!path.Contains(ProfileIconsPath)) return null;

            // Extract ID from filename (e.g., "123.jpg" -> 123)
            string fileName = Path.GetFileNameWithoutExtension(node.Name);
            if (!int.TryParse(fileName, out int iconId)) return null;

            try
            {
                var metadataList = await LoadMetadataAsync(node);
                if (metadataList == null) return null;

                var entry = metadataList.FirstOrDefault(e => e.Id == iconId);
                if (entry == null) return null;

                return new SummonerIconMetadata
                {
                    Title = entry.Title,
                    Description = entry.Descriptions?.FirstOrDefault(d => d.Region == "riot")?.Description ?? entry.Descriptions?.FirstOrDefault()?.Description
                };
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to get summoner icon metadata for ID {iconId}");
                return null;
            }
        }

        private async Task<List<SummonerIconJsonEntry>> LoadMetadataAsync(FileSystemNodeModel node)
        {
            // If we are in the same WAD and already have cache, reuse it
            if (_cachedMetadata != null && _cachedSourceWad == node.SourceWadPath)
            {
                return _cachedMetadata;
            }

            byte[] jsonData = null;

            // 1. Identify the game data directory to search in
            string gameDataPath = null;
            if (File.Exists(node.SourceWadPath))
            {
                gameDataPath = Path.GetDirectoryName(node.SourceWadPath);
            }
            else
            {
                // Backup mode or invalid path, fallback to settings
                gameDataPath = _appSettings.PreferredClient == PreferredClient.PBE ? _appSettings.LolPbeDirectory : _appSettings.LolLiveDirectory;
            }

            // 2. Search for the JSON node
            if (!string.IsNullOrEmpty(gameDataPath) && Directory.Exists(gameDataPath))
            {
                var jsonNode = await _wadContentProvider.FindNodeByVirtualPathAsync(JsonPath, gameDataPath);
                if (jsonNode != null)
                {
                    jsonData = await _wadContentProvider.GetVirtualFileBytesAsync(jsonNode);
                }
            }

            if (jsonData == null)
            {
                _logService.LogWarning("Could not locate summoner-icons.json in current context.");
                return null;
            }

            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                _cachedMetadata = JsonSerializer.Deserialize<List<SummonerIconJsonEntry>>(jsonData, options);
                _cachedSourceWad = node.SourceWadPath;
                return _cachedMetadata;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to parse summoner-icons.json");
                return null;
            }
        }
    }
}
