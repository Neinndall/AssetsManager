using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
using AssetsManager.Views.Models.Settings;

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

        private Dictionary<string, string> _assetPathMap;
        private readonly Dictionary<string, string> _localEndpoints;
        private readonly Dictionary<string, string> _remoteEndpoints;

        private Task _metadataLoadTask;

        public string GetEffectiveGameDirectory()
        {
            return _appSettings.ApiSettings.ClientTarget == ApiClientTarget.PBE
                ? _appSettings.LolPbeDirectory
                : _appSettings.LolLiveDirectory;
        }

        public string GetEffectiveClientType()
        {
            return _appSettings.ApiSettings.ClientTarget == ApiClientTarget.PBE ? "PBE" : "LIVE";
        }

        public void InvalidateMetadata()
        {
            _metadataLoadTask = null;
        }

        private string GetIconWadPath(string iconUrl)
        {
            return PathUtils.NormalizeRiotIconPath(iconUrl);
        }

        private Task LoadMetadataMapsAsync()
        {
            // Pattern: Thread-safe one-time initialization task
            return _metadataLoadTask ??= Task.Run(async () =>
            {
                _assetPathMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                string lolDirectory = GetEffectiveGameDirectory();
                if (string.IsNullOrEmpty(lolDirectory)) return;

                string pluginPath = Path.Combine(lolDirectory, "Plugins", "rcp-be-lol-game-data");

                // Each catalog supplies the JSON path, name field, and image field.
                var catalogs = new[] {
                    RiotCatalogDefinitions.SkinCatalog,
                    RiotCatalogDefinitions.EmoteCatalog,
                    RiotCatalogDefinitions.WardCatalog,
                    RiotCatalogDefinitions.IconCatalog,
                    RiotCatalogDefinitions.LootCatalog,
                    RiotCatalogDefinitions.NexusFinisherCatalog
                };
                var catalogNodes = await _wadContentProvider.FindNodesByVirtualPathsAsync(
                    catalogs.Select(catalog => catalog.Path),
                    pluginPath);

                foreach (var catalog in catalogs)
                {
                    try
                    {
                        if (!catalogNodes.TryGetValue(catalog.Path, out var node)) continue;

                        byte[] jsonData = await _wadContentProvider.GetVirtualFileBytesAsync(node);
                        if (jsonData == null) continue;

                        using var doc = JsonDocument.Parse(jsonData);
                        JsonElement root = doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Object
                            && root.TryGetProperty("LootItems", out var lootItems))
                        {
                            root = lootItems;
                        }

                        if (root.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in root.EnumerateArray())
                                AddEntryToMap(item, _assetPathMap, catalog.NameKey, catalog.PathKey);
                        }
                        else if (root.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var property in root.EnumerateObject())
                                AddEntryToMap(property.Value, _assetPathMap, catalog.NameKey, catalog.PathKey);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logService.LogError(ex, $"Error loading metadata for {catalog.Path}");
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

                if (element.TryGetProperty(pathKey, out var pathProp))
                {
                    string path = pathProp.GetString();
                    if (!string.IsNullOrEmpty(path)) map.TryAdd(name, path);
                }

                // If this skin entry contains nested chromas, index each chroma asset
                if (element.TryGetProperty("chromas", out var chromasProp) && chromasProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var chroma in chromasProp.EnumerateArray())
                    {
                        string chromaName = chroma.TryGetProperty("name", out var cNameProp) ? cNameProp.GetString() : null;
                        string chromaPath = chroma.TryGetProperty("chromaPath", out var cPathProp) ? cPathProp.GetString() : null;

                        if (!string.IsNullOrEmpty(chromaName) && !string.IsNullOrEmpty(chromaPath))
                        {
                            map.TryAdd(chromaName, chromaPath);

                            // Also index composite name permutations if the chroma name is short
                            if (!chromaName.Contains(name, StringComparison.OrdinalIgnoreCase))
                            {
                                map.TryAdd($"{name} ({chromaName})", chromaPath);
                                map.TryAdd($"{name} - {chromaName}", chromaPath);
                            }
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
            if (_assetPathMap.TryGetValue(name, out var path)) return path;
            if (cleanedName != name && _assetPathMap.TryGetValue(cleanedName, out var cleanedPath)) return cleanedPath;

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

                string lolDirectory = GetEffectiveGameDirectory();
                if (string.IsNullOrEmpty(lolDirectory)) return null;

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
            string lolDirectory = GetEffectiveGameDirectory();
            string clientType = GetEffectiveClientType();

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
                    int newPort = int.Parse(parts[2]);
                    string newPassword = parts[3];
                    string newLocalUrl = $"https://127.0.0.1:{newPort}";

                    if (_appSettings.ApiSettings.Connection.Port != newPort ||
                        _appSettings.ApiSettings.Connection.Password != newPassword ||
                        _appSettings.ApiSettings.Connection.LocalApiUrl != newLocalUrl)
                    {
                        _appSettings.ApiSettings.Connection.Port = newPort;
                        _appSettings.ApiSettings.Connection.Password = newPassword;
                        _appSettings.ApiSettings.Connection.LocalApiUrl = newLocalUrl;

                        AppSettings.SaveSettings(_appSettings);
                    }
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

        public async Task<(string Json, HttpStatusCode? StatusCode)> GetPassRewardsProgressionAsync(string eventId, string overrideName = null)
        {
            var response = await MakeRemoteRequestAsync("progression", eventId: eventId);
            if (response != null && response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                
                // Determine a unique name for this pass to avoid overwriting
                string fileName = "pass_progression.json";
                string eventName = overrideName;

                if (string.IsNullOrEmpty(eventName))
                {
                    try 
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("name", out var nameProp))
                        {
                            eventName = nameProp.GetString();
                        }
                    } catch { /* Fallback */ }
                }

                if (!string.IsNullOrEmpty(eventName))
                {
                    string cleanName = PathUtils.CleanPassName(eventName);
                    fileName = $"{PathUtils.SanitizeName(cleanName).Replace(" ", "_")}_progression.json";
                }

                _directoriesCreator.CreateDirectory(_directoriesCreator.ApiCachePath);
                await File.WriteAllTextAsync(Path.Combine(_directoriesCreator.ApiCachePath, fileName), json);

                _logService.LogSuccess($"Pass rewards progression for {fileName} retrieved and cached successfully.");
                return (json, response.StatusCode);
            }

            if (response != null)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logService.LogWarning("The pass was not found on Riot servers or may be inactive or temporarily removed.");
                }
                else
                {
                    _logService.LogError($"Failed to retrieve pass progression. Server returned status: {response.StatusCode}");
                }
                return (null, response.StatusCode);
            }
            else
            {
                _logService.LogError("Failed to retrieve pass progression. The server response was empty or null.");
                return (null, null);
            }
        }

        private string SanitizeFileName(string fileName)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(c, '_');
            }
            return fileName.Replace(' ', '_');
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

            if (response != null)
            {
                _logService.LogError($"Failed to retrieve pass rewards catalog. Server returned status: {response.StatusCode}");
            }
            else
            {
                _logService.LogError("Failed to retrieve pass rewards catalog. The server response was empty or null.");
            }
            return null;
        }

        public async Task<(string Id, string Name)> GetActivePassGroupIdAsync()
        {
            string lolDirectory = GetEffectiveGameDirectory();
            if (string.IsNullOrEmpty(lolDirectory)) return (null, null);

            string pluginPath = Path.Combine(lolDirectory, "Plugins", "rcp-be-lol-game-data");
            var node = await _wadContentProvider.FindNodeByVirtualPathAsync(RiotCatalogDefinitions.EventHubJsonPath, pluginPath);
            if (node == null) return (null, null);

            try
            {
                byte[] data = await _wadContentProvider.GetVirtualFileBytesAsync(node);
                if (data == null) return (null, null);

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
                    _logService.LogSuccess($"Active Pass found: {bestEvent.Name}");
                    return (bestEvent.Id, bestEvent.Name);
                }
            }
            catch (Exception ex) { _logService.LogError(ex, $"Error parsing event-hub from {node.SourceWadPath}"); }
            return (null, null);
        }

        public async Task<string> GetPassNameFromHubAsync(string trackConfigId)
        {
            if (string.IsNullOrEmpty(trackConfigId)) return null;

            string lolDirectory = GetEffectiveGameDirectory();
            if (string.IsNullOrEmpty(lolDirectory)) return null;

            string pluginPath = Path.Combine(lolDirectory, "Plugins", "rcp-be-lol-game-data");
            var node = await _wadContentProvider.FindNodeByVirtualPathAsync(RiotCatalogDefinitions.EventHubJsonPath, pluginPath);
            if (node == null) return null;

            try
            {
                byte[] data = await _wadContentProvider.GetVirtualFileBytesAsync(node);
                if (data == null) return null;

                using var doc = JsonDocument.Parse(data);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("event", out var ev))
                    {
                        if (ev.TryGetProperty("rewardTrack", out var rt) &&
                            rt.TryGetProperty("trackConfig", out var tc) &&
                            tc.TryGetProperty("id", out var idProp) &&
                            string.Equals(idProp.GetString(), trackConfigId, StringComparison.OrdinalIgnoreCase))
                        {
                            string name = ev.TryGetProperty("localizedName", out var n) ? n.GetString() : null;
                            if (!string.IsNullOrEmpty(name))
                            {
                                _logService.Log($"Found pass name '{name}' with ID: {trackConfigId}");
                                return name;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { _logService.LogError(ex, $"Error parsing event-hub from {node.SourceWadPath}"); }
            
            return null;
        }

        public async Task ExtractRewardIconsBatchAsync(IEnumerable<string> iconUrls, Action<string, string> onIconExtracted)
        {
            if (iconUrls == null || !iconUrls.Any()) return;

            string lolDirectory = GetEffectiveGameDirectory();
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
                var iconNodes = await _wadContentProvider.FindNodesByVirtualPathsAsync(
                    remainingUrls.Select(GetIconWadPath),
                    pluginPath);

                foreach (var url in remainingUrls)
                {
                    var virtualPath = GetIconWadPath(url);
                    if (!iconNodes.TryGetValue(virtualPath, out var node)) continue;

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
