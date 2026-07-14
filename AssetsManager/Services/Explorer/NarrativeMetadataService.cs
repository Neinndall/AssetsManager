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
        
        // Persistent Caches
        private Dictionary<string, SummonerIconJsonEntry> _cachedIcons;
        private Dictionary<string, EmoteJsonEntry> _cachedEmotes;
        private Dictionary<string, WardJsonEntry> _cachedWards;
        private Dictionary<string, LootJsonEntry> _cachedLoot;
        
        private string _loadedIconsSource;
        private string _loadedEmotesSource;
        private string _loadedWardsSource;
        private string _loadedLootSource;

        public NarrativeMetadataService(LogService logService, WadContentProvider wadContentProvider, AppSettings appSettings)
        {
            _logService = logService;
            _wadContentProvider = wadContentProvider;
            _appSettings = appSettings;
        }

        public bool IsMetadataSupported(FileSystemNodeModel node)
        {
            if (node == null || FileSystemNodeModel.CanHaveChildren(node.Type)) return false;
            
            string path = PathUtils.NormalizePath(node.VirtualPath);
            bool isRelevant = path.Contains("profile-icons") || path.Contains("summoneremotes") || path.Contains("wardskinimages") || path.Contains("loot");
            if (!isRelevant) return false;

            // Use the utility method to cover all supported textures and images
            return SupportedFileTypes.IsImage(node.Name) || path.Contains("/loot/");
        }

        public async Task PreloadMetadataAsync(FileSystemNodeModel contextNode)
        {
            if (contextNode == null) return;
            await Task.WhenAll(
                LoadIconsMetadataAsync(contextNode),
                LoadEmotesMetadataAsync(contextNode),
                LoadWardsMetadataAsync(contextNode),
                LoadLootMetadataAsync(contextNode)
            );
        }

        public NarrativeMetadata GetMetadataSync(FileSystemNodeModel node)
        {
            if (node == null || string.IsNullOrEmpty(node.VirtualPath)) return null;

            string path = PathUtils.NormalizePath(node.VirtualPath);

            if (_cachedIcons != null && path.Contains("profile-icons"))
                return GetMappedMetadata(path, "profile-icons/", _cachedIcons, e => e.Title, e => GetCombinedDescription(e.Descriptions));

            if (_cachedEmotes != null && path.Contains("summoneremotes"))
                return GetMappedMetadata(path, "summoneremotes/", _cachedEmotes, e => e.Name, e => e.Description);

            if (_cachedWards != null && path.Contains("wardskinimages"))
                return GetMappedMetadata(path, "wardskinimages/", _cachedWards, e => e.Name, e => GetCombinedDescription(e.RegionalDescriptions));

            if (_cachedLoot != null && path.Contains("loot"))
                return GetMappedMetadata(path, "loot/", _cachedLoot, e => e.Name, e => e.Description);

            return null;
        }

        private NarrativeMetadata GetMappedMetadata<T>(
            string nodePath, string token, Dictionary<string, T> dict, 
            Func<T, string> titleSelector, Func<T, string> descSelector)
        {
            int idx = nodePath.IndexOf(token);
            if (idx == -1) return null;

            string suffix = Path.ChangeExtension(nodePath.Substring(idx), null);
            if (dict.TryGetValue(suffix, out var entry))
            {
                return new NarrativeMetadata { Title = titleSelector(entry) ?? "N/A", Description = descSelector(entry) ?? "N/A" };
            }
            return null;
        }

        private string GetCombinedDescription(IEnumerable<DescriptionEntry> descs) => descs?.FirstOrDefault(d => d.Region == "riot")?.Description ?? descs?.FirstOrDefault()?.Description;
        private string GetCombinedDescription(IEnumerable<WardRegionalDescription> descs) => descs?.FirstOrDefault(d => d.Region == "riot")?.Description ?? descs?.FirstOrDefault()?.Description;

        public async Task<NarrativeMetadata> GetMetadataAsync(FileSystemNodeModel node)
        {
            if (node == null || string.IsNullOrEmpty(node.VirtualPath)) return null;
            string path = PathUtils.NormalizePath(node.VirtualPath);
            
            if (path.Contains("profile-icons")) await LoadIconsMetadataAsync(node);
            else if (path.Contains("summoneremotes")) await LoadEmotesMetadataAsync(node);
            else if (path.Contains("wardskinimages")) await LoadWardsMetadataAsync(node);
            else if (path.Contains("loot")) await LoadLootMetadataAsync(node);

            return GetMetadataSync(node);
        }

        #region --- Robust Loaders ---

        private async Task<Dictionary<string, T>> LoadCatalogAsync<T>(FileSystemNodeModel node, string jsonPath, Func<T, string> pathKeySelector, string pathToken)
        {
            byte[] data = await LoadJsonFromContextAsync(node, jsonPath);
            if (data == null) return null;

            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                List<T> list = null;
                using (var doc = JsonDocument.Parse(data))
                {
                    JsonElement root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("LootItems", out var lootProp)) root = lootProp;
                    if (root.ValueKind == JsonValueKind.Array) list = JsonSerializer.Deserialize<List<T>>(root.GetRawText(), options);
                    else if (root.ValueKind == JsonValueKind.Object) list = JsonSerializer.Deserialize<Dictionary<string, T>>(root.GetRawText(), options)?.Values.ToList();
                }

                var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
                if (list != null)
                {
                    foreach (var item in list)
                    {
                        string raw = pathKeySelector(item);
                        if (string.IsNullOrEmpty(raw)) continue;
                        string norm = PathUtils.NormalizePath(raw);
                        int idx = norm.IndexOf(pathToken);
                        if (idx != -1) result[Path.ChangeExtension(norm.Substring(idx), null)] = item;
                    }
                }
                return result.Count > 0 ? result : null;
            }
            catch (Exception ex) { _logService.LogError(ex, $"Failed to parse catalog: {jsonPath}"); return null; }
        }

        private async Task LoadIconsMetadataAsync(FileSystemNodeModel node)
        {
            if (_cachedIcons != null && _loadedIconsSource == node.SourceWadPath) return;
            var fresh = await LoadCatalogAsync<SummonerIconJsonEntry>(node, RiotCatalogDefinitions.IconsJsonPath, e => e.ImagePath, "profile-icons/");
            if (fresh != null) { _cachedIcons = fresh; _loadedIconsSource = node.SourceWadPath; }
        }

        private async Task LoadEmotesMetadataAsync(FileSystemNodeModel node)
        {
            if (_cachedEmotes != null && _loadedEmotesSource == node.SourceWadPath) return;
            var fresh = await LoadCatalogAsync<EmoteJsonEntry>(node, RiotCatalogDefinitions.EmotesJsonPath, e => e.InventoryIcon, "summoneremotes/");
            if (fresh != null) { _cachedEmotes = fresh; _loadedEmotesSource = node.SourceWadPath; }
        }

        private async Task LoadWardsMetadataAsync(FileSystemNodeModel node)
        {
            if (_cachedWards != null && _loadedWardsSource == node.SourceWadPath) return;
            var fresh = await LoadCatalogAsync<WardJsonEntry>(node, RiotCatalogDefinitions.WardsJsonPath, e => e.WardImagePath, "wardskinimages/");
            if (fresh != null) { _cachedWards = fresh; _loadedWardsSource = node.SourceWadPath; }
        }

        private async Task LoadLootMetadataAsync(FileSystemNodeModel node)
        {
            if (_cachedLoot != null && _loadedLootSource == node.SourceWadPath) return;
            var fresh = await LoadCatalogAsync<LootJsonEntry>(node, RiotCatalogDefinitions.LootJsonPath, e => e.Image, "loot/");
            if (fresh != null) { _cachedLoot = fresh; _loadedLootSource = node.SourceWadPath; }
        }

        #endregion

        private async Task<byte[]> LoadJsonFromContextAsync(FileSystemNodeModel node, string jsonVirtualPath)
        {
            // 1. Try WAD Folder
            string wadDir = !string.IsNullOrEmpty(node.SourceWadPath) && File.Exists(node.SourceWadPath) ? Path.GetDirectoryName(node.SourceWadPath) : null;
            if (!string.IsNullOrEmpty(wadDir))
            {
                var n = await _wadContentProvider.FindNodeByVirtualPathAsync(jsonVirtualPath, wadDir);
                if (n != null) return await _wadContentProvider.GetVirtualFileBytesAsync(n);
            }

            // 2. Try Global Preferred Client
            string globalDir = _appSettings.PreferredClient == PreferredClient.PBE ? _appSettings.LolPbeDirectory : _appSettings.LolLiveDirectory;
            if (!string.IsNullOrEmpty(globalDir))
            {
                var n = await _wadContentProvider.FindNodeByVirtualPathAsync(jsonVirtualPath, globalDir);
                if (n != null) return await _wadContentProvider.GetVirtualFileBytesAsync(n);
            }

            // 3. Final Fallback (Opposite client)
            string altDir = _appSettings.PreferredClient == PreferredClient.PBE ? _appSettings.LolLiveDirectory : _appSettings.LolPbeDirectory;
            if (!string.IsNullOrEmpty(altDir) && altDir != globalDir)
            {
                var n = await _wadContentProvider.FindNodeByVirtualPathAsync(jsonVirtualPath, altDir);
                if (n != null) return await _wadContentProvider.GetVirtualFileBytesAsync(n);
            }

            return null;
        }
    }
}