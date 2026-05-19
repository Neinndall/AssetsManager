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
    public class NarrativeMetadataService
    {
        private readonly LogService _logService;
        private readonly WadContentProvider _wadContentProvider;
        private readonly AppSettings _appSettings;
        
        private List<SummonerIconJsonEntry> _cachedIcons;
        private List<EmoteJsonEntry> _cachedEmotes;
        private List<WardJsonEntry> _cachedWards;
        private string _cachedIconsSourceWad;
        private string _cachedEmotesSourceWad;
        private string _cachedWardsSourceWad;

        public NarrativeMetadataService(LogService logService, WadContentProvider wadContentProvider, AppSettings appSettings)
        {
            _logService = logService;
            _wadContentProvider = wadContentProvider;
            _appSettings = appSettings;
        }

        public async Task<NarrativeMetadata> GetMetadataAsync(FileSystemNodeModel node)
        {
            if (node == null || string.IsNullOrEmpty(node.FullPath)) return null;

            string path = PathUtils.NormalizePath(node.FullPath);
            
            // 1. Check for Summoner Icons
            if (path.Contains(RiotCatalogDefinitions.ProfileIconsVirtualPath))
            {
                return await GetIconMetadataAsync(node);
            }
            
            // 2. Check for Emotes
            if (path.Contains(RiotCatalogDefinitions.EmotesVirtualPath))
            {
                return await GetEmoteMetadataAsync(node);
            }

            // 3. Check for Wards
            if (path.Contains(RiotCatalogDefinitions.WardsVirtualPath))
            {
                return await GetWardMetadataAsync(node);
            }

            return null;
        }

        private async Task<NarrativeMetadata> GetIconMetadataAsync(FileSystemNodeModel node)
        {
            string fileName = Path.GetFileNameWithoutExtension(node.Name);
            if (!int.TryParse(fileName, out int iconId)) return null;

            try
            {
                var iconsList = await LoadIconsMetadataAsync(node);
                if (iconsList == null) return null;

                var entry = iconsList.FirstOrDefault(e => e.Id == iconId);
                if (entry == null) return null;

                return new NarrativeMetadata
                {
                    Title = string.IsNullOrWhiteSpace(entry.Title) ? "N/A" : entry.Title,
                    Description = entry.Descriptions?.FirstOrDefault(d => d.Region == "riot")?.Description 
                                  ?? entry.Descriptions?.FirstOrDefault()?.Description 
                                  ?? "N/A"
                };
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to get summoner icon metadata for ID {iconId}");
                return null;
            }
        }

        private async Task<NarrativeMetadata> GetEmoteMetadataAsync(FileSystemNodeModel node)
        {
            try
            {
                var emotesList = await LoadEmotesMetadataAsync(node);
                if (emotesList == null) return null;

                string normalizedNodePath = PathUtils.NormalizePath(node.FullPath);
                
                // Strategy 1: Match by ID in filename (e.g. "123_EM.png")
                string fileName = Path.GetFileNameWithoutExtension(node.Name);
                string idPart = fileName.Split('_')[0];
                if (int.TryParse(idPart, out int emoteId))
                {
                    var entry = emotesList.FirstOrDefault(e => e.Id == emoteId);
                    if (entry != null) return MapEmoteToMetadata(entry);
                }

                // Strategy 2: Match by Path (for named emotes in subfolders)
                // Use "summoneremotes/" as an anchor to avoid issues with duplicated "assets/" folders
                string token = "summoneremotes/";
                int tokenIndex = normalizedNodePath.IndexOf(token);
                if (tokenIndex != -1)
                {
                    string nodeSuffix = Path.ChangeExtension(normalizedNodePath.Substring(tokenIndex), null);
                    
                    var entry = emotesList.FirstOrDefault(e => {
                        if (string.IsNullOrEmpty(e.InventoryIcon)) return false;
                        string jsonPath = PathUtils.NormalizePath(e.InventoryIcon);
                        int jsonTokenIndex = jsonPath.IndexOf(token);
                        if (jsonTokenIndex == -1) return false;

                        return nodeSuffix == Path.ChangeExtension(jsonPath.Substring(jsonTokenIndex), null);
                    });


                    if (entry != null) return MapEmoteToMetadata(entry);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to get emote metadata for node {node.Name}");
                return null;
            }
        }

        private async Task<NarrativeMetadata> GetWardMetadataAsync(FileSystemNodeModel node)
        {
            try
            {
                var wardsList = await LoadWardsMetadataAsync(node);
                if (wardsList == null) return null;

                // Match by ID in filename (e.g. "wardhero_101.png")
                string fileName = Path.GetFileNameWithoutExtension(node.Name);
                var match = System.Text.RegularExpressions.Regex.Match(fileName, @"\d+");
                if (match.Success && int.TryParse(match.Value, out int wardId))
                {
                    var entry = wardsList.FirstOrDefault(e => e.Id == wardId);
                    if (entry != null) return MapWardToMetadata(entry);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to get ward metadata for node {node.Name}");
                return null;
            }
        }

        private NarrativeMetadata MapEmoteToMetadata(EmoteJsonEntry entry)
        {
            return new NarrativeMetadata
            {
                Title = string.IsNullOrWhiteSpace(entry.Name) ? "N/A" : entry.Name,
                Description = string.IsNullOrWhiteSpace(entry.Description) ? "N/A" : entry.Description
            };
        }

        private NarrativeMetadata MapWardToMetadata(WardJsonEntry entry)
        {
            return new NarrativeMetadata
            {
                Title = string.IsNullOrWhiteSpace(entry.Name) ? "N/A" : entry.Name,
                Description = entry.RegionalDescriptions?.FirstOrDefault(d => d.Region == "riot")?.Description
                              ?? (!string.IsNullOrWhiteSpace(entry.Description) ? entry.Description : "N/A")
            };
        }

        private async Task<List<SummonerIconJsonEntry>> LoadIconsMetadataAsync(FileSystemNodeModel node)
        {
            if (_cachedIcons != null && _cachedIconsSourceWad == node.SourceWadPath) return _cachedIcons;

            byte[] jsonData = await LoadJsonFromContextAsync(node, RiotCatalogDefinitions.IconsJsonPath);
            if (jsonData == null) return null;

            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                _cachedIcons = JsonSerializer.Deserialize<List<SummonerIconJsonEntry>>(jsonData, options);
                _cachedIconsSourceWad = node.SourceWadPath;
                return _cachedIcons;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to parse summoner-icons.json");
                return null;
            }
        }

        private async Task<List<EmoteJsonEntry>> LoadEmotesMetadataAsync(FileSystemNodeModel node)
        {
            if (_cachedEmotes != null && _cachedEmotesSourceWad == node.SourceWadPath) return _cachedEmotes;

            byte[] jsonData = await LoadJsonFromContextAsync(node, RiotCatalogDefinitions.EmotesJsonPath);
            if (jsonData == null) return null;

            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                _cachedEmotes = JsonSerializer.Deserialize<List<EmoteJsonEntry>>(jsonData, options);
                _cachedEmotesSourceWad = node.SourceWadPath;
                return _cachedEmotes;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to parse summoner-emotes.json");
                return null;
            }
        }

        private async Task<List<WardJsonEntry>> LoadWardsMetadataAsync(FileSystemNodeModel node)
        {
            if (_cachedWards != null && _cachedWardsSourceWad == node.SourceWadPath) return _cachedWards;

            byte[] jsonData = await LoadJsonFromContextAsync(node, RiotCatalogDefinitions.WardsJsonPath);
            if (jsonData == null) return null;

            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                _cachedWards = JsonSerializer.Deserialize<List<WardJsonEntry>>(jsonData, options);
                _cachedWardsSourceWad = node.SourceWadPath;
                return _cachedWards;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to parse ward-skins.json");
                return null;
            }
        }

        private async Task<byte[]> LoadJsonFromContextAsync(FileSystemNodeModel node, string jsonVirtualPath)
        {
            string gameDataPath = null;
            if (File.Exists(node.SourceWadPath))
            {
                gameDataPath = Path.GetDirectoryName(node.SourceWadPath);
            }
            else
            {
                gameDataPath = _appSettings.PreferredClient == PreferredClient.PBE ? _appSettings.LolPbeDirectory : _appSettings.LolLiveDirectory;
            }

            if (!string.IsNullOrEmpty(gameDataPath) && Directory.Exists(gameDataPath))
            {
                var jsonNode = await _wadContentProvider.FindNodeByVirtualPathAsync(jsonVirtualPath, gameDataPath);
                if (jsonNode != null)
                {
                    return await _wadContentProvider.GetVirtualFileBytesAsync(jsonNode);
                }
            }

            return null;
        }
    }
}
