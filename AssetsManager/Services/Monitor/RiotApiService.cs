using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Compression;
using System.Text.RegularExpressions;
using LeagueToolkit.Core.Wad;
using AssetsManager.Utils;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Services.Monitor
{
    public class RiotApiService
    {
        private readonly AppSettings _appSettings;
        private readonly HttpClient _httpClient;
        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly WadContentProvider _wadContentProvider;
        private readonly SemaphoreSlim _extractionSemaphore = new(1, 1);

        private Dictionary<string, string> _skinNamePathMap;
        private Dictionary<string, string> _emoteNamePathMap;
        private Dictionary<string, string> _wardNamePathMap;
        private Dictionary<string, string> _iconNamePathMap;
        private readonly Dictionary<string, string> _localEndpoints;
        private readonly Dictionary<string, string> _remoteEndpoints;

        private Task _metadataLoadTask;

        private string GetIconWadPath(string iconUrl)
        {
            return PathUtils.NormalizeRiotIconPath(iconUrl);
        }

        private Task LoadMetadataMapsAsync()
        {
            // Pattern: Thread-safe one-time initialization task
            return _metadataLoadTask ??= Task.Run(async () =>
            {
                _skinNamePathMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _emoteNamePathMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _wardNamePathMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _iconNamePathMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                string lolDirectory = _appSettings.ApiSettings.UsePbeForApi ? _appSettings.LolPbeDirectory : _appSettings.LolLiveDirectory;
                if (string.IsNullOrEmpty(lolDirectory)) return;

                string pluginPath = Path.Combine(lolDirectory, "Plugins", "rcp-be-lol-game-data");

                // Mapping definitions to our internal dictionaries
                var catalogs = new[] {
                    new { Info = RiotCatalogDefinitions.SkinCatalog, Map = _skinNamePathMap },
                    new { Info = RiotCatalogDefinitions.EmoteCatalog, Map = _emoteNamePathMap },
                    new { Info = RiotCatalogDefinitions.WardCatalog, Map = _wardNamePathMap },
                    new { Info = RiotCatalogDefinitions.IconCatalog, Map = _iconNamePathMap }
                };

                foreach (var catalog in catalogs)
                {
                    try
                    {
                        var node = await _wadContentProvider.FindNodeByVirtualPathAsync(catalog.Info.Path, pluginPath);
                        if (node == null) continue;

                        byte[] jsonData = await _wadContentProvider.GetVirtualFileBytesAsync(node);
                        if (jsonData == null) continue;

                        using var doc = JsonDocument.Parse(jsonData);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in doc.RootElement.EnumerateArray())
                                AddEntryToMap(item, catalog.Map, catalog.Info.NameKey, catalog.Info.PathKey);
                        }
                        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var property in doc.RootElement.EnumerateObject())
                                AddEntryToMap(property.Value, catalog.Map, catalog.Info.NameKey, catalog.Info.PathKey);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logService.LogError(ex, $"Error loading metadata for {catalog.Info.Path}");
                    }
                }
            });
        }

        private void AddEntryToMap(JsonElement element, Dictionary<string, string> map, string nameKey, string pathKey)
        {
            if (element.TryGetProperty(nameKey, out var nameProp))
            {
                string name = nameProp.GetString();
                if (string.IsNullOrEmpty(name)) return;

                // Intentamos la clave principal
                if (element.TryGetProperty(pathKey, out var pathProp))
                {
                    string path = pathProp.GetString();
                    if (!string.IsNullOrEmpty(path))
                    {
                        map[name] = path;
                        return;
                    }
                }

                // Fallback para iconos que pueden usar imagePath o iconPath indistintamente
                if (pathKey == "imagePath" || pathKey == "iconPath")
                {
                    string fallbackKey = pathKey == "imagePath" ? "iconPath" : "imagePath";
                    if (element.TryGetProperty(fallbackKey, out var fallbackProp))
                    {
                        string path = fallbackProp.GetString();
                        if (!string.IsNullOrEmpty(path))
                        {
                            map[name] = path;
                        }
                    }
                }
            }
        }

        public async Task<string> GetMythicAssetPathAsync(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            await LoadMetadataMapsAsync();

            string cleanedName = PathUtils.CleanRiotName(name);
            var maps = new[] { _skinNamePathMap, _emoteNamePathMap, _wardNamePathMap, _iconNamePathMap };

            foreach (var map in maps)
            {
                if (map.TryGetValue(name, out var path)) return path;
                if (cleanedName != name && map.TryGetValue(cleanedName, out var cleanedPath)) return cleanedPath;
            }

            return null;
        }

        private async Task<string> ExtractFromWadsAsync(string virtualPath, string targetDirectory, string fileName)
        {
            string destinationPath = Path.Combine(targetDirectory, fileName);
            if (File.Exists(destinationPath)) return destinationPath;

            await _extractionSemaphore.WaitAsync();
            try
            {
                if (File.Exists(destinationPath)) return destinationPath;

                string lolDirectory = _appSettings.ApiSettings.UsePbeForApi ? _appSettings.LolPbeDirectory : _appSettings.LolLiveDirectory;
                string pluginPath = Path.Combine(lolDirectory, "Plugins", "rcp-be-lol-game-data");
                
                var node = await _wadContentProvider.FindNodeByVirtualPathAsync(virtualPath, pluginPath);
                if (node != null)
                {
                    byte[] data = await _wadContentProvider.GetVirtualFileBytesAsync(node);
                    if (data != null)
                    {
                        _directoriesCreator.CreateDirectory(targetDirectory);
                        await File.WriteAllBytesAsync(destinationPath, data);
                        return destinationPath;
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Error extracting {virtualPath} from WADs");
            }
            finally { _extractionSemaphore.Release(); }
            return null;
        }

        public async Task<string> ExtractMythicIconAsync(string iconPath, string subFolder = null)
        {
            if (string.IsNullOrEmpty(iconPath)) return null;

            // Use specialized path from directories creator if available, otherwise resolve relative to ApiCachePath
            string targetDir = subFolder switch
            {
                "mythic" => _directoriesCreator.ApiCacheMythicPath,
                "sales" => _directoriesCreator.ApiCacheSalesPath,
                "rewards" => _directoriesCreator.ApiCacheRewardsPath,
                _ => Path.Combine(_directoriesCreator.ApiCachePath, subFolder ?? "mythic")
            };

            return await ExtractFromWadsAsync(GetIconWadPath(iconPath), targetDir, Path.GetFileName(iconPath));
        }

        public RiotApiService(
            AppSettings appSettings, 
            HttpClient httpClient, 
            LogService logService, 
            DirectoriesCreator directoriesCreator,
            WadContentProvider wadContentProvider)
        {
            _appSettings = appSettings;
            _httpClient = httpClient;
            _logService = logService;
            _directoriesCreator = directoriesCreator;
            _wadContentProvider = wadContentProvider;

            _localEndpoints = Endpoints.GetLocalEndpoints();
            _remoteEndpoints = Endpoints.GetRemoteEndpoints();
        }

        public async Task<bool> ReadLockfileAsync(bool logErrorOnFailure = true)
        {
            string lolDirectory = _appSettings.ApiSettings.UsePbeForApi
                ? _appSettings.LolPbeDirectory
                : _appSettings.LolLiveDirectory;

            string clientType = _appSettings.ApiSettings.UsePbeForApi ? "PBE" : "Live";

            if (string.IsNullOrEmpty(lolDirectory) || !Directory.Exists(lolDirectory))
            {
                if (logErrorOnFailure)
                {
                    _logService.LogWarning($"LoL {clientType} Directory is not configured or does not exist.");
                }
                return false;
            }
            var lockfilePath = Path.Combine(lolDirectory, "lockfile");

            if (!File.Exists(lockfilePath))
            {
                if (logErrorOnFailure)
                {
                    _logService.LogWarning($"Lockfile not found. Make sure the {clientType} client is running.");
                }
                return false;
            }

            try
            {
                string lockfileContent;
                using (var fileStream = new FileStream(lockfilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fileStream))
                {
                    lockfileContent = await reader.ReadToEndAsync();
                }

                var parts = lockfileContent.Split(':');
                if (parts.Length >= 4)
                {
                    _appSettings.ApiSettings.Connection.Port = int.Parse(parts[2]);
                    _appSettings.ApiSettings.Connection.Password = parts[3];
                    _appSettings.ApiSettings.Connection.LocalApiUrl = $"https://127.0.0.1:{_appSettings.ApiSettings.Connection.Port}";

                    AppSettings.SaveSettings(_appSettings);
                    return true;
                }
                if (logErrorOnFailure)
                {
                    _logService.LogError("The lockfile format is incorrect. Could not extract necessary data.");
                }
            }
            catch (Exception ex)
            {
                if (logErrorOnFailure)
                {
                    _logService.LogError(ex, "Error reading or processing lockfile.");
                }
                return false;
            }

            return false;
        }

        public string GetLocalAuthHeader()
        {
            if (string.IsNullOrEmpty(_appSettings.ApiSettings.Connection.Password))
            {
                return string.Empty;
            }

            var authString = $"riot:{_appSettings.ApiSettings.Connection.Password}";
            var authBytes = System.Text.Encoding.UTF8.GetBytes(authString);
            var base64String = System.Convert.ToBase64String(authBytes);

            return $"Basic {base64String}";
        }

        public async Task<bool> AuthenticateForUiDisplayAsync()
        {
            string token = await GetTokenFromEndpoint("entitlementsToken");
            if (!string.IsNullOrEmpty(token))
            {
                // This token's parsed info will be the one displayed in the UI.
                _appSettings.ApiSettings.Token.Jwt = token;
                ParseJwtPayload(token); // This method already saves the settings
                _logService.LogSuccess("UI authentication token has been set from entitlements endpoint.");
                return true;
            }
            
            _logService.LogError("Failed to acquire a token for UI display.");
            return false;
        }

        private async Task<string> GetTokenFromEndpoint(string endpointKey)
        {
            if (!_localEndpoints.TryGetValue(endpointKey, out var tokenEndpointPath))
            {
                return null;
            }

            try
            {
                var response = await MakeLocalRequestAsync(tokenEndpointPath);
                if (response != null && response.IsSuccessStatusCode)
                {
                    var rawResponse = await response.Content.ReadAsStringAsync();
                    
                    if (rawResponse.StartsWith("\"") && rawResponse.EndsWith("\""))
                    {
                        return rawResponse.Trim('"');
                    }

                    try 
                    {
                        using (var jsonDoc = JsonDocument.Parse(rawResponse))
                        {
                            if (jsonDoc.RootElement.ValueKind == JsonValueKind.String)
                            {
                                return jsonDoc.RootElement.GetString();
                            }
                            
                            if (jsonDoc.RootElement.ValueKind == JsonValueKind.Object)
                            {
                                if (endpointKey == "entitlementsToken")
                                {
                                    if (jsonDoc.RootElement.TryGetProperty("accessToken", out var aToken)) return aToken.GetString();
                                    if (jsonDoc.RootElement.TryGetProperty("entitlements_token", out var entToken)) return entToken.GetString();
                                }
                                if (endpointKey == "leagueSessionToken")
                                {
                                    if (jsonDoc.RootElement.TryGetProperty("token", out var sToken)) return sToken.GetString();
                                    if (jsonDoc.RootElement.TryGetProperty("accessToken", out var aToken)) return aToken.GetString();
                                }
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        return rawResponse.Trim('"');
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogWarning($"HTTP error while acquiring token from {endpointKey}: {ex.Message}");
            }

            return null;
        }

        private void ParseJwtPayload(string token)
        {
            try
            {
                var parsedInfo = JwtUtils.ParsePayload(token);

                _appSettings.ApiSettings.Token.Expiration = parsedInfo.Expiration;
                _appSettings.ApiSettings.Token.IssuedAt = parsedInfo.IssuedAt;
                _appSettings.ApiSettings.Token.Puuid = parsedInfo.Puuid ?? "Unknown";
                _appSettings.ApiSettings.Token.Platform = parsedInfo.Platform ?? "Unknown";
                _appSettings.ApiSettings.Token.Region = parsedInfo.Region ?? "Unknown";
                _appSettings.ApiSettings.Token.SummonerId = parsedInfo.SummonerId;

                AppSettings.SaveSettings(_appSettings);
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Error parsing JWT payload. Default values will be used.");
                _appSettings.ApiSettings.Token.Expiration = DateTime.UtcNow.AddHours(1);
                _appSettings.ApiSettings.Token.Region = "Unknown";
                _appSettings.ApiSettings.Token.Puuid = "Unknown";
                _appSettings.ApiSettings.Token.SummonerId = 0;
                _appSettings.ApiSettings.Token.Platform = "Unknown";
                _appSettings.ApiSettings.Token.IssuedAt = DateTime.UtcNow;
            }
        }

        public async Task<SalesCatalog> GetSalesCatalogAsync()
        {
            var response = await MakeRemoteRequestAsync("sales");
            if (response != null && response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                _directoriesCreator.CreateDirectory(_directoriesCreator.ApiCachePath);
                await File.WriteAllTextAsync(Path.Combine(_directoriesCreator.ApiCachePath, "sales.json"), json);
                
                _logService.LogSuccess("Sales catalog retrieved and cached successfully.");
                return JsonSerializer.Deserialize<SalesCatalog>(json);
            }
            
            if (response != null)
            {
                _logService.LogError($"Failed to retrieve Sales catalog. Server returned status: {response.StatusCode}");
            }
            else
            {
                _logService.LogError("Failed to retrieve Sales catalog. The server response was empty or null.");
            }
            return null;
        }

        public async Task<MythicShopResponse> GetMythicShopResponseAsync()
        {
            var mythicShopJson = await GetMythicShopAsync();
            if (!string.IsNullOrEmpty(mythicShopJson))
            {
                return JsonSerializer.Deserialize<MythicShopResponse>(mythicShopJson);
            }
            return null;
        }

        public async Task<string> GetPassRewardsProgressionAsync(string eventId)
        {
            var response = await MakeRemoteRequestAsync("progression", eventId: eventId);
            if (response != null && response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                _directoriesCreator.CreateDirectory(_directoriesCreator.ApiCachePath);
                await File.WriteAllTextAsync(Path.Combine(_directoriesCreator.ApiCachePath, "pass_progression.json"), json);

                _logService.LogSuccess("Pass rewards progression retrieved and cached successfully.");
                return json;
            }

            if (response != null)
            {
                _logService.LogError($"Failed to retrieve Pass progression. Server returned status: {response.StatusCode}");
            }
            else
            {
                _logService.LogError("Failed to retrieve Pass progression. The server response was empty or null.");
            }
            return null;
        }

        public async Task<string> GetPassRewardsRewardsAsync()
        {
            var response = await MakeRemoteRequestAsync("rewards");
            if (response != null && response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                _directoriesCreator.CreateDirectory(_directoriesCreator.ApiCachePath);
                await File.WriteAllTextAsync(Path.Combine(_directoriesCreator.ApiCachePath, "pass_rewards.json"), json);
                return json;
            }
            return null;
        }

        public async Task<string> GetActivePassGroupIdAsync()
        {
            string lolDirectory = _appSettings.ApiSettings.UsePbeForApi ? _appSettings.LolPbeDirectory : _appSettings.LolLiveDirectory;
            if (string.IsNullOrEmpty(lolDirectory)) return null;

            string pluginPath = Path.Combine(lolDirectory, "Plugins", "rcp-be-lol-game-data");
            string[] possiblePaths = { 
                "plugins/rcp-be-lol-game-data/global/default/v1/event-hub.json",
                "global/default/v1/event-hub.json",
                "v1/event-hub.json"
            };

            foreach (var path in possiblePaths)
            {
                var node = await _wadContentProvider.FindNodeByVirtualPathAsync(path, pluginPath);
                if (node == null) continue;

                try
                {
                    byte[] data = await _wadContentProvider.GetVirtualFileBytesAsync(node);
                    if (data == null) continue;

                    using var doc = JsonDocument.Parse(data);
                    var bestEvent = doc.RootElement.EnumerateArray()
                        .Select(e => e.TryGetProperty("event", out var ev) ? ev : (JsonElement?)null)
                        .Where(ev => ev != null && ev.Value.TryGetProperty("eventHubType", out var t) && t.GetString() == "kSeasonPass")
                        .Select(ev => new {
                            Id = ev.Value.GetProperty("rewardTrack").GetProperty("trackConfig").GetProperty("id").GetString(),
                            Name = ev.Value.TryGetProperty("localizedName", out var n) ? n.GetString() : "Unknown Event",
                            Start = ev.Value.TryGetProperty("startDate", out var s) && DateTime.TryParse(s.GetString(), out var sd) ? sd : DateTime.MinValue,
                            End = ev.Value.TryGetProperty("endDate", out var ed) && DateTime.TryParse(ed.GetString(), out var edd) ? edd : DateTime.MaxValue
                        })
                        .Where(e => DateTime.UtcNow <= e.End)
                        .OrderByDescending(e => e.Start)
                        .FirstOrDefault();

                    if (bestEvent != null)
                    {
                        _logService.LogSuccess($"Active Pass ID found: {bestEvent.Name}");
                        return bestEvent.Id;
                    }
                }
                catch (Exception ex) { _logService.LogError(ex, $"Error parsing event-hub from {node.SourceWadPath}"); }
            }
            return null;
        }

        public async Task ExtractRewardIconsBatchAsync(IEnumerable<string> iconUrls, Action<string, string> onIconExtracted)
        {
            if (iconUrls == null || !iconUrls.Any()) return;

            string lolDirectory = _appSettings.ApiSettings.UsePbeForApi ? _appSettings.LolPbeDirectory : _appSettings.LolLiveDirectory;
            if (string.IsNullOrEmpty(lolDirectory)) return;

            string pluginPath = Path.Combine(lolDirectory, "Plugins", "rcp-be-lol-game-data");
            string rewardsDir = _directoriesCreator.ApiCacheRewardsPath;
            _directoriesCreator.CreateDirectory(rewardsDir);

            var uniqueUrls = iconUrls.Distinct().ToList();
            var remainingUrls = new List<string>();

            foreach (var url in uniqueUrls)
            {
                string destinationPath = Path.Combine(rewardsDir, Path.GetFileName(url));
                if (File.Exists(destinationPath)) onIconExtracted?.Invoke(url, destinationPath);
                else remainingUrls.Add(url);
            }

            if (!remainingUrls.Any()) return;

            await _extractionSemaphore.WaitAsync();
            try
            {
                foreach (var url in remainingUrls)
                {
                    var virtualPath = GetIconWadPath(url);
                    var node = await _wadContentProvider.FindNodeByVirtualPathAsync(virtualPath, pluginPath);

                    if (node != null)
                    {
                        byte[] data = await _wadContentProvider.GetVirtualFileBytesAsync(node);
                        if (data != null)
                        {
                            string destinationPath = Path.Combine(rewardsDir, Path.GetFileName(url));
                            await File.WriteAllBytesAsync(destinationPath, data);
                            onIconExtracted?.Invoke(url, destinationPath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Error in batch extraction of reward icons");
            }
            finally { _extractionSemaphore.Release(); }
        }

        private async Task<HttpResponseMessage> MakeLocalRequestAsync(string endpointPath)
        {
            if (string.IsNullOrEmpty(_appSettings.ApiSettings.Connection.LocalApiUrl)) return null;
            var requestUri = $"{_appSettings.ApiSettings.Connection.LocalApiUrl}{endpointPath}";
            var authHeader = GetLocalAuthHeader();
            if (string.IsNullOrEmpty(authHeader)) return null;

            var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Add("Authorization", authHeader);
            if (Uri.TryCreate(requestUri, UriKind.Absolute, out var uri)) request.Headers.Host = uri.Authority;
            return await _httpClient.SendAsync(request);
        }

        private async Task<HttpResponseMessage> MakeRemoteRequestAsync(string endpointKey, int retryCount = 1, string eventId = null)
        {
            if (!_remoteEndpoints.TryGetValue(endpointKey, out var endpointPath))
            {
                _logService.LogError($"Endpoint key '{endpointKey}' not found in remote endpoints.");
                return null;
            }

            var tempPath = endpointPath;
            if (tempPath.Contains("{events_id}") && !string.IsNullOrEmpty(eventId)) tempPath = tempPath.Replace("{events_id}", eventId);
            if (tempPath.Contains("{locales}")) tempPath = tempPath.Replace("{locales}", "en_US");

            string tokenKey = (endpointKey == "sales") ? "entitlementsToken" : "leagueSessionToken";
            string jwt = await GetTokenFromEndpoint(tokenKey);

            if (string.IsNullOrEmpty(jwt))
            {
                _logService.LogError($"Failed to acquire {tokenKey}.");
                return null;
            }

            _appSettings.ApiSettings.Token.Jwt = jwt;
            ParseJwtPayload(jwt);
            
            var currentRegion = _appSettings.ApiSettings.Token.Region?.ToLower() ?? "unknown";
            if (currentRegion == "unknown")
            {
                _logService.LogError("Could not determine region from JWT. Remote request cancelled.");
                return null;
            }

            var regionKey = Regex.Replace(currentRegion, @"\d+$", "");
            var baseUrl = Endpoints.BaseUrlLive.Replace("{region}", regionKey);
            var requestUri = $"{baseUrl}{tempPath}";

            var request = CreateRemoteRequest(requestUri, jwt);

            try
            {
                var response = await _httpClient.SendAsync(request);

                if ((response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden) && retryCount > 0)
                {
                    _logService.LogWarning($"Unauthorized/Forbidden. Attempting refresh for {tokenKey}...");
                    string refreshedJwt = await GetTokenFromEndpoint(tokenKey);

                    if (!string.IsNullOrEmpty(refreshedJwt) && refreshedJwt != jwt)
                    {
                        var retryRequest = CreateRemoteRequest(requestUri, refreshedJwt);
                        return await _httpClient.SendAsync(retryRequest);
                    }
                }
                return response;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Exception during request to {requestUri}.");
                return null;
            }
        }

        private HttpRequestMessage CreateRemoteRequest(string requestUri, string jwt)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
            request.Headers.Add("User-Agent", "LeagueOfLegendsClient/15.1.645.4557 (rcp-be-lol-ranked)");
            request.Headers.Add("Accept", "application/json");
            return request;
        }

        private async Task<string> GetMythicShopAsync()
        {
            var response = await MakeRemoteRequestAsync("mythic_shop");
            if (response != null && response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                _directoriesCreator.CreateDirectory(_directoriesCreator.ApiCachePath);
                await File.WriteAllTextAsync(Path.Combine(_directoriesCreator.ApiCachePath, "mythic_shop.json"), json);

                _logService.LogSuccess("Mythic Shop data retrieved and cached successfully.");
                return json;
            }

            if (response != null)
            {
                _logService.LogError($"Failed to retrieve Mythic Shop data. Server returned status: {response.StatusCode}");
            }
            else
            {
                _logService.LogError("Failed to retrieve Mythic Shop data. The server response was empty or null.");
            }
            return null;
        }
    }
}
