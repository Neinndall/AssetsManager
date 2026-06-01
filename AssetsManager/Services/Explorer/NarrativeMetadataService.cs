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
        
        private Dictionary<int, SummonerIconJsonEntry> _cachedIcons;
        private Dictionary<string, EmoteJsonEntry> _cachedEmotes; // Key: Path suffix
        private Dictionary<int, WardJsonEntry> _cachedWards;
        private Dictionary<string, LootJsonEntry> _cachedLoot; // Key: Path suffix
        private string _cachedIconsSourceWad;
        private string _cachedEmotesSourceWad;
        private string _cachedWardsSourceWad;
        private string _cachedLootSourceWad;

        public NarrativeMetadataService(LogService logService, WadContentProvider wadContentProvider, AppSettings appSettings)
        {
            _logService = logService;
            _wadContentProvider = wadContentProvider;
            _appSettings = appSettings;
        }

        public bool IsMetadataSupported(FileSystemNodeModel node)
        {
            if (node == null || FileSystemNodeModel.CanHaveChildren(node.Type)) return false;
            
            // Fast check before normalization
            string name = node.Name.ToLowerInvariant();
            if (!name.EndsWith(".png") && !name.EndsWith(".jpg") && !name.EndsWith(".dds")) return false;

            string path = PathUtils.NormalizePath(node.VirtualPath);
            return path.Contains("profile-icons") ||
                   path.Contains("summoneremotes") ||
                   path.Contains("wardskinimages") ||
                   path.Contains("loot");
        }

        /// <summary>
        /// Preloads all relevant metadata catalogs for a given WAD or client context.
        /// Essential for high-performance search loops.
        /// </summary>
        public async Task PreloadMetadataAsync(FileSystemNodeModel contextNode)
        {
            if (contextNode == null) return;
            
            // We fire these in parallel for maximum speed
            await Task.WhenAll(
                LoadIconsMetadataAsync(contextNode),
                LoadEmotesMetadataAsync(contextNode),
                LoadWardsMetadataAsync(contextNode),
                LoadLootMetadataAsync(contextNode)
            );
        }

        /// <summary>
        /// Synchronous lookup. Requires PreloadMetadataAsync to have been called first.
        /// Used for high-speed search loops.
        /// </summary>
        public NarrativeMetadata GetMetadataSync(FileSystemNodeModel node)
        {
            if (node == null || string.IsNullOrEmpty(node.VirtualPath)) return null;

            string path = PathUtils.NormalizePath(node.VirtualPath);

            // 1. Icons
            if (_cachedIcons != null && path.Contains(RiotCatalogDefinitions.ProfileIconsVirtualPath))
            {
                string fileName = Path.GetFileNameWithoutExtension(node.Name);
                if (int.TryParse(fileName, out int iconId) && _cachedIcons.TryGetValue(iconId, out var entry))
                {
                    return new NarrativeMetadata
                    {
                        Title = string.IsNullOrWhiteSpace(entry.Title) ? "N/A" : entry.Title,
                        Description = entry.Descriptions?.FirstOrDefault(d => d.Region == "riot")?.Description 
                                      ?? entry.Descriptions?.FirstOrDefault()?.Description ?? "N/A"
                    };
                }
            }

            // 2. Emotes
            if (_cachedEmotes != null && path.Contains(RiotCatalogDefinitions.EmotesVirtualPath))
            {
                return GetMappedMetadataFromDict(path, "summoneremotes/", _cachedEmotes);
            }

            // 3. Wards
            if (_cachedWards != null && path.Contains(RiotCatalogDefinitions.WardsVirtualPath))
            {
                string fileName = Path.GetFileNameWithoutExtension(node.Name);
                var match = System.Text.RegularExpressions.Regex.Match(fileName, @"\d+");
                if (match.Success && int.TryParse(match.Value, out int wardId) && _cachedWards.TryGetValue(wardId, out var entry))
                {
                    return MapToMetadata(entry.Name, entry.RegionalDescriptions?.FirstOrDefault(d => d.Region == "riot")?.Description ?? entry.Description);
                }
            }

            // 4. Loot
            if (_cachedLoot != null && path.Contains(RiotCatalogDefinitions.LootVirtualPath))
            {
                return GetMappedMetadataFromDict(path, "assets/loot/", _cachedLoot);
            }

            return null;
        }

        private NarrativeMetadata GetMappedMetadataFromDict<T>(string path, string token, Dictionary<string, T> dict)
        {
            int tokenIndex = path.IndexOf(token);
            if (tokenIndex == -1) return null;

            string suffix = Path.ChangeExtension(path.Substring(tokenIndex), null);
            if (dict.TryGetValue(suffix, out var entry))
            {
                if (entry is EmoteJsonEntry e) return MapToMetadata(e.Name, e.Description);
                if (entry is LootJsonEntry l) return MapToMetadata(l.Name, l.Description);
            }
            return null;
        }

        private NarrativeMetadata MapToMetadata(string title, string description)
        {
            return new NarrativeMetadata
            {
                Title = string.IsNullOrWhiteSpace(title) ? "N/A" : title,
                Description = string.IsNullOrWhiteSpace(description) ? "N/A" : description
            };
        }

        public async Task<NarrativeMetadata> GetMetadataAsync(FileSystemNodeModel node)
        {
            if (node == null || string.IsNullOrEmpty(node.VirtualPath)) return null;
            return GetMetadataSync(node); // Reuse sync logic since caches are pre-warmed or handled
        }

        // --- Catalog Loading Logic ---
        // (Loading methods stay mostly the same as they are context-specific and async)

        private async Task<NarrativeMetadata> GetIconMetadataAsync(FileSystemNodeModel node)
        {
            string fileName = Path.GetFileNameWithoutExtension(node.Name);
            if (!int.TryParse(fileName, out int iconId)) return null;

            try
            {
                var iconsDict = await LoadIconsMetadataAsync(node);
                if (iconsDict == null) return null;

                if (!iconsDict.TryGetValue(iconId, out var entry)) return null;

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
                var emotesDict = await LoadEmotesMetadataAsync(node);
                if (emotesDict == null) return null;

                string normalizedNodePath = PathUtils.NormalizePath(node.VirtualPath);
                string token = "summoneremotes/";
                int tokenIndex = normalizedNodePath.IndexOf(token);
                if (tokenIndex == -1) return null;

                string nodeSuffix = Path.ChangeExtension(normalizedNodePath.Substring(tokenIndex), null);

                if (emotesDict.TryGetValue(nodeSuffix, out var entry))
                {
                    return MapEmoteToMetadata(entry);
                }

                // Strategy 2: Match by ID in filename as fallback
                string fileName = Path.GetFileNameWithoutExtension(node.Name);
                string idPart = fileName.Split('_')[0];
                if (int.TryParse(idPart, out int emoteId))
                {
                    var fallbackEntry = emotesDict.Values.FirstOrDefault(e => e.Id == emoteId);
                    if (fallbackEntry != null) return MapEmoteToMetadata(fallbackEntry);
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
                var wardsDict = await LoadWardsMetadataAsync(node);
                if (wardsDict == null) return null;

                // Match by ID in filename (e.g. "wardhero_101.png")
                string fileName = Path.GetFileNameWithoutExtension(node.Name);
                var match = System.Text.RegularExpressions.Regex.Match(fileName, @"\d+");
                if (match.Success && int.TryParse(match.Value, out int wardId))
                {
                    if (wardsDict.TryGetValue(wardId, out var entry))
                    {
                        return MapWardToMetadata(entry);
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to get ward metadata for node {node.Name}");
                return null;
            }
        }

        private async Task<NarrativeMetadata> GetLootMetadataAsync(FileSystemNodeModel node)
        {
            try
            {
                var lootDict = await LoadLootMetadataAsync(node);
                if (lootDict == null) return null;

                string normalizedNodePath = PathUtils.NormalizePath(node.VirtualPath);
                string token = "assets/loot/";
                int tokenIndex = normalizedNodePath.IndexOf(token);
                if (tokenIndex == -1) return null;

                string nodeSuffix = Path.ChangeExtension(normalizedNodePath.Substring(tokenIndex), null);

                if (lootDict.TryGetValue(nodeSuffix, out var entry))
                {
                    return MapLootToMetadata(entry);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to get loot metadata for node {node.Name}");
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

        private NarrativeMetadata MapLootToMetadata(LootJsonEntry entry)
        {
            return new NarrativeMetadata
            {
                Title = string.IsNullOrWhiteSpace(entry.Name) ? "N/A" : entry.Name,
                Description = string.IsNullOrWhiteSpace(entry.Description) ? "N/A" : entry.Description
            };
        }

        private async Task<Dictionary<int, SummonerIconJsonEntry>> LoadIconsMetadataAsync(FileSystemNodeModel node)
        {
            if (_cachedIcons != null && _cachedIconsSourceWad == node.SourceWadPath) return _cachedIcons;

            byte[] jsonData = await LoadJsonFromContextAsync(node, RiotCatalogDefinitions.IconsJsonPath);
            if (jsonData == null) return null;

            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var list = JsonSerializer.Deserialize<List<SummonerIconJsonEntry>>(jsonData, options);
                _cachedIcons = list?.ToDictionary(e => e.Id, e => e);
                _cachedIconsSourceWad = node.SourceWadPath;
                return _cachedIcons;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to parse summoner-icons.json");
                return null;
            }
        }

        private async Task<Dictionary<string, EmoteJsonEntry>> LoadEmotesMetadataAsync(FileSystemNodeModel node)
        {
            if (_cachedEmotes != null && _cachedEmotesSourceWad == node.SourceWadPath) return _cachedEmotes;

            byte[] jsonData = await LoadJsonFromContextAsync(node, RiotCatalogDefinitions.EmotesJsonPath);
            if (jsonData == null) return null;

            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var list = JsonSerializer.Deserialize<List<EmoteJsonEntry>>(jsonData, options);
                
                _cachedEmotes = new Dictionary<string, EmoteJsonEntry>(StringComparer.OrdinalIgnoreCase);
                string token = "summoneremotes/";

                if (list != null)
                {
                    foreach (var entry in list)
                    {
                        if (string.IsNullOrEmpty(entry.InventoryIcon)) continue;
                        string jsonPath = PathUtils.NormalizePath(entry.InventoryIcon);
                        int jsonTokenIndex = jsonPath.IndexOf(token);
                        if (jsonTokenIndex == -1) continue;

                        string suffix = Path.ChangeExtension(jsonPath.Substring(jsonTokenIndex), null);
                        _cachedEmotes[suffix] = entry;
                    }
                }

                _cachedEmotesSourceWad = node.SourceWadPath;
                return _cachedEmotes;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to parse summoner-emotes.json");
                return null;
            }
        }

        private async Task<Dictionary<int, WardJsonEntry>> LoadWardsMetadataAsync(FileSystemNodeModel node)
        {
            if (_cachedWards != null && _cachedWardsSourceWad == node.SourceWadPath) return _cachedWards;

            byte[] jsonData = await LoadJsonFromContextAsync(node, RiotCatalogDefinitions.WardsJsonPath);
            if (jsonData == null) return null;

            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var list = JsonSerializer.Deserialize<List<WardJsonEntry>>(jsonData, options);
                _cachedWards = list?.ToDictionary(e => e.Id, e => e);
                _cachedWardsSourceWad = node.SourceWadPath;
                return _cachedWards;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to parse ward-skins.json");
                return null;
            }
        }

        private async Task<Dictionary<string, LootJsonEntry>> LoadLootMetadataAsync(FileSystemNodeModel node)
        {
            if (_cachedLoot != null && _cachedLootSourceWad == node.SourceWadPath) return _cachedLoot;

            byte[] jsonData = await LoadJsonFromContextAsync(node, RiotCatalogDefinitions.LootJsonPath);
            if (jsonData == null) return null;

            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                List<LootJsonEntry> list = null;

                using (var doc = JsonDocument.Parse(jsonData))
                {
                    JsonElement root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("LootItems", out var lootItemsProp))
                    {
                        root = lootItemsProp;
                    }

                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        list = JsonSerializer.Deserialize<List<LootJsonEntry>>(root.GetRawText(), options);
                    }
                    else if (root.ValueKind == JsonValueKind.Object)
                    {
                        var dict = JsonSerializer.Deserialize<Dictionary<string, LootJsonEntry>>(root.GetRawText(), options);
                        list = dict?.Values.ToList();
                    }
                }

                _cachedLoot = new Dictionary<string, LootJsonEntry>(StringComparer.OrdinalIgnoreCase);
                string token = "assets/loot/";

                if (list != null)
                {
                    foreach (var entry in list)
                    {
                        if (string.IsNullOrEmpty(entry.Image)) continue;
                        string jsonPath = PathUtils.NormalizePath(entry.Image);
                        int jsonTokenIndex = jsonPath.IndexOf(token);
                        if (jsonTokenIndex == -1) continue;

                        string suffix = Path.ChangeExtension(jsonPath.Substring(jsonTokenIndex), null);
                        _cachedLoot[suffix] = entry;
                    }
                }

                _cachedLootSourceWad = node.SourceWadPath;
                return _cachedLoot;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to parse loot.json");
                return null;
            }
        }

        private async Task<byte[]> LoadJsonFromContextAsync(FileSystemNodeModel node, string jsonVirtualPath)
        {
            string gameDataPath = null;
            
            // Try to resolve path from the node's WAD
            if (!string.IsNullOrEmpty(node.SourceWadPath) && File.Exists(node.SourceWadPath))
            {
                gameDataPath = Path.GetDirectoryName(node.SourceWadPath);
            }
            
            // Fallback to global settings if WAD context is missing or JSON not found there
            if (string.IsNullOrEmpty(gameDataPath))
            {
                gameDataPath = _appSettings.PreferredClient == PreferredClient.PBE ? _appSettings.LolPbeDirectory : _appSettings.LolLiveDirectory;
            }

            if (!string.IsNullOrEmpty(gameDataPath))
            {
                // _logService.Log($"[Metadata] Searching for catalog '{Path.GetFileName(jsonVirtualPath)}' in: {gameDataPath}");
                var jsonNode = await _wadContentProvider.FindNodeByVirtualPathAsync(jsonVirtualPath, gameDataPath);
                if (jsonNode != null)
                {
                    return await _wadContentProvider.GetVirtualFileBytesAsync(jsonNode);
                }
                else
                {
                    // Secondary fallback: Try searching in the opposite client directory just in case
                    string alternatePath = _appSettings.PreferredClient == PreferredClient.PBE ? _appSettings.LolLiveDirectory : _appSettings.LolPbeDirectory;
                    if (!string.IsNullOrEmpty(alternatePath) && alternatePath != gameDataPath)
                    {
                        jsonNode = await _wadContentProvider.FindNodeByVirtualPathAsync(jsonVirtualPath, alternatePath);
                        if (jsonNode != null) return await _wadContentProvider.GetVirtualFileBytesAsync(jsonNode);
                    }
                }
            }

            _logService.LogWarning($"[Metadata] Could not locate catalog file: {jsonVirtualPath}");
            return null;
        }
    }
}
