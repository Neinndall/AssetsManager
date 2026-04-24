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
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Services.Monitor
{
    public class RiotApiService
    {
        private readonly AppSettings _appSettings;
        private readonly HttpClient _httpClient;
        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly SemaphoreSlim _extractionSemaphore = new(1, 1);
        private readonly string[] _passWadNames = { "default-assets2.wad", "default-assets.wad" };

        private readonly Dictionary<string, string> _localEndpoints;
        private readonly Dictionary<string, string> _remoteEndpoints;

        private string GetIconWadPath(string iconUrl)
        {
            string basePath = Regex.Replace(iconUrl.ToLowerInvariant(), @"^.*assets/", "");
            return $"plugins/rcp-be-lol-game-data/global/default/assets/{basePath}";
        }

        public RiotApiService(AppSettings appSettings, HttpClient httpClient, LogService logService, DirectoriesCreator directoriesCreator)
        {
            _appSettings = appSettings;
            _httpClient = httpClient;
            _logService = logService;
            _directoriesCreator = directoriesCreator;

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
                                if (endpointKey == "entitlementsToken" && jsonDoc.RootElement.TryGetProperty("entitlements_token", out var entToken))
                                {
                                    return entToken.GetString();
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
                return JsonSerializer.Deserialize<SalesCatalog>(json);
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
                return json;
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

            foreach (var wadName in _passWadNames)
            {
                string wadPath = Path.Combine(pluginPath, wadName);
                if (!File.Exists(wadPath)) continue;

                try
                {
                    using var wadFile = new WadFile(wadPath);
                    string[] possiblePaths = { 
                        "plugins/rcp-be-lol-game-data/global/default/v1/event-hub.json",
                        "global/default/v1/event-hub.json",
                        "v1/event-hub.json"
                    };
                    
                    foreach (var path in possiblePaths)
                    {
                        ulong hash = LeagueToolkit.Hashing.XxHash64Ext.Hash(path.ToLowerInvariant());
                        if (wadFile.Chunks.TryGetValue(hash, out var chunk))
                        {
                            using var decompressedDataOwner = wadFile.LoadChunkDecompressed(chunk);
                            using (var doc = JsonDocument.Parse(decompressedDataOwner.Span.ToArray()))
                            {
                                string bestId = null;
                                string bestName = "Unknown Event";
                                DateTime latestStart = DateTime.MinValue;
                                bool isBestActive = false;
                                DateTime now = DateTime.UtcNow;

                                foreach (var element in doc.RootElement.EnumerateArray())
                                {
                                    if (element.TryGetProperty("event", out var eventObj))
                                    {
                                        if (eventObj.TryGetProperty("eventHubType", out var type) && type.GetString() == "kSeasonPass")
                                        {
                                            if (eventObj.TryGetProperty("rewardTrack", out var rewardTrack) &&
                                                rewardTrack.TryGetProperty("trackConfig", out var trackConfig) &&
                                                trackConfig.TryGetProperty("id", out var id))
                                            {
                                                string currentId = id.GetString();
                                                string currentName = eventObj.TryGetProperty("localizedName", out var nameProp) ? nameProp.GetString() : "Unknown Event";
                                                DateTime startDate = DateTime.MinValue;
                                                DateTime endDate = DateTime.MaxValue;

                                                if (eventObj.TryGetProperty("startDate", out var startProp))
                                                    DateTime.TryParse(startProp.GetString(), out startDate);

                                                if (eventObj.TryGetProperty("endDate", out var endProp))
                                                    DateTime.TryParse(endProp.GetString(), out endDate);

                                                bool isActive = now >= startDate && now <= endDate;

                                                if (isActive)
                                                {
                                                    if (!isBestActive || startDate > latestStart)
                                                    {
                                                        bestId = currentId;
                                                        bestName = currentName;
                                                        latestStart = startDate;
                                                        isBestActive = true;
                                                    }
                                                }
                                                else if (!isBestActive)
                                                {
                                                    if (bestId == null || startDate > latestStart)
                                                    {
                                                        bestId = currentId;
                                                        bestName = currentName;
                                                        latestStart = startDate;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }

                                if (bestId != null)
                                {
                                    _logService.LogSuccess($"Active Pass ID found called: {bestName}");
                                    return bestId;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Error extracting active pass ID from {wadName}");
                }
            }
            return null;
        }

        public async Task ExtractRewardIconsBatchAsync(IEnumerable<string> iconUrls, Action<string, string> onIconExtracted)
        {
            if (iconUrls == null || !iconUrls.Any()) return;

            string lolDirectory = _appSettings.ApiSettings.UsePbeForApi ? _appSettings.LolPbeDirectory : _appSettings.LolLiveDirectory;
            if (string.IsNullOrEmpty(lolDirectory)) return;

            string pluginPath = Path.Combine(lolDirectory, "Plugins", "rcp-be-lol-game-data");
            string rewardsDir = Path.Combine(_directoriesCreator.ApiCachePath, "rewards");
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
                foreach (var wadName in _passWadNames)
                {
                    string wadPath = Path.Combine(pluginPath, wadName);
                    if (!File.Exists(wadPath)) continue;

                    using var wadFile = new WadFile(wadPath);
                    for (int i = remainingUrls.Count - 1; i >= 0; i--)
                    {
                        var iconUrl = remainingUrls[i];
                        ulong pathHash = LeagueToolkit.Hashing.XxHash64Ext.Hash(GetIconWadPath(iconUrl));

                        if (wadFile.Chunks.TryGetValue(pathHash, out var chunk))
                        {
                            using var decompressedDataOwner = wadFile.LoadChunkDecompressed(chunk);
                            string destinationPath = Path.Combine(rewardsDir, Path.GetFileName(iconUrl));
                            await File.WriteAllBytesAsync(destinationPath, decompressedDataOwner.Span.ToArray());
                            
                            onIconExtracted?.Invoke(iconUrl, destinationPath);
                            remainingUrls.RemoveAt(i);
                        }
                    }
                    if (!remainingUrls.Any()) break;
                }
            }
            finally { _extractionSemaphore.Release(); }
        }

        public async Task<string> ExtractRewardIconAsync(string iconUrl)
        {
            if (string.IsNullOrEmpty(iconUrl)) return null;
            string fileName = Path.GetFileName(iconUrl);
            string destinationPath = Path.Combine(_directoriesCreator.ApiCachePath, "rewards", fileName);
            if (File.Exists(destinationPath)) return destinationPath;

            await _extractionSemaphore.WaitAsync();
            try
            {
                if (File.Exists(destinationPath)) return destinationPath;
                string lolDirectory = _appSettings.ApiSettings.UsePbeForApi ? _appSettings.LolPbeDirectory : _appSettings.LolLiveDirectory;
                string pluginPath = Path.Combine(lolDirectory, "Plugins", "rcp-be-lol-game-data");
                
                ulong pathHash = LeagueToolkit.Hashing.XxHash64Ext.Hash(GetIconWadPath(iconUrl));

                foreach (var wadName in _passWadNames)
                {
                    string wadPath = Path.Combine(pluginPath, wadName);
                    if (!File.Exists(wadPath)) continue;

                    using var wadFile = new WadFile(wadPath);
                    if (wadFile.Chunks.TryGetValue(pathHash, out var chunk))
                    {
                        using var decompressedDataOwner = wadFile.LoadChunkDecompressed(chunk);
                        _directoriesCreator.CreateDirectory(Path.GetDirectoryName(destinationPath));
                        await File.WriteAllBytesAsync(destinationPath, decompressedDataOwner.Span.ToArray());
                        return destinationPath;
                    }
                }
            }
            catch { }
            finally { _extractionSemaphore.Release(); }
            return null;
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
            if (!_remoteEndpoints.TryGetValue(endpointKey, out var endpointPath)) return null;
            var tempPath = endpointPath;
            if (tempPath.Contains("{events_id}") && !string.IsNullOrEmpty(eventId)) tempPath = tempPath.Replace("{events_id}", eventId);
            if (tempPath.Contains("{locales}")) tempPath = tempPath.Replace("{locales}", "en_US");

            string tokenKey = (endpointKey == "sales") ? "entitlementsToken" : "leagueSessionToken";
            string jwt = await GetTokenFromEndpoint(tokenKey);
            if (string.IsNullOrEmpty(jwt)) return null;

            _appSettings.ApiSettings.Token.Jwt = jwt;
            ParseJwtPayload(jwt);
            
            var currentRegion = _appSettings.ApiSettings.Token.Region?.ToLower() ?? "unknown";
            if (currentRegion == "unknown")
            {
                _logService.LogError("Could not determine region. Remote request cancelled.");
                return null;
            }

            var regionKey = Regex.Replace(currentRegion, @"\d+$", "");
            var baseUrl = Endpoints.BaseUrlLive.Replace("{region}", regionKey);
            var requestUri = $"{baseUrl}{tempPath}";

            var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Add("Authorization", $"Bearer {jwt}");
            request.Headers.Add("User-Agent", "LeagueOfLegendsClient/15.1.645.4557 (rcp-be-lol-ranked)");
            request.Headers.Add("Accept", "application/json");

            try
            {
                var response = await _httpClient.SendAsync(request);
                if ((response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden) && retryCount > 0)
                {
                    string refreshedJwt = await GetTokenFromEndpoint(tokenKey);
                    if (!string.IsNullOrEmpty(refreshedJwt))
                    {
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", refreshedJwt);
                        return await _httpClient.SendAsync(request);
                    }
                }
                return response;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Error in remote request to {requestUri}.");
                return null;
            }
        }

        private async Task<string> GetMythicShopAsync()
        {
            var response = await MakeRemoteRequestAsync("mythic_shop");
            if (response != null && response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                _directoriesCreator.CreateDirectory(_directoriesCreator.ApiCachePath);
                await File.WriteAllTextAsync(Path.Combine(_directoriesCreator.ApiCachePath, "mythic_shop.json"), json);
                return json;
            }
            return null;
        }
    }
}
