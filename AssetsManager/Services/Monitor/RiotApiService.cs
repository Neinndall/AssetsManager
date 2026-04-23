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
        private readonly SemaphoreSlim _extractionSemaphore = new SemaphoreSlim(1, 1);

        private readonly Dictionary<string, string> _localEndpoints;
        private readonly Dictionary<string, string> _remoteEndpoints;

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
            string lolDirectory = _appSettings.ApiSettings.UsePbeForApi ? _appSettings.LolPbeDirectory : _appSettings.LolLiveDirectory;
            if (string.IsNullOrEmpty(lolDirectory) || !Directory.Exists(lolDirectory)) return false;

            var lockfilePath = Path.Combine(lolDirectory, "lockfile");
            if (!File.Exists(lockfilePath)) return false;

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
            }
            catch { }
            return false;
        }

        public string GetLocalAuthHeader()
        {
            if (string.IsNullOrEmpty(_appSettings.ApiSettings.Connection.Password)) return string.Empty;
            var authBytes = System.Text.Encoding.UTF8.GetBytes($"riot:{_appSettings.ApiSettings.Connection.Password}");
            return $"Basic {Convert.ToBase64String(authBytes)}";
        }

        private async Task<string> GetTokenFromEndpoint(string endpointKey)
        {
            if (!_localEndpoints.TryGetValue(endpointKey, out var tokenEndpointPath)) return null;

            try
            {
                var response = await MakeLocalRequestAsync(tokenEndpointPath);
                if (response != null && response.IsSuccessStatusCode)
                {
                    var rawResponse = await response.Content.ReadAsStringAsync();
                    if (rawResponse.StartsWith("\"") && rawResponse.EndsWith("\"")) return rawResponse.Trim('"');

                    using (var jsonDoc = JsonDocument.Parse(rawResponse))
                    {
                        if (jsonDoc.RootElement.ValueKind == JsonValueKind.String) return jsonDoc.RootElement.GetString();
                        if (jsonDoc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            if (endpointKey == "entitlementsToken" && jsonDoc.RootElement.TryGetProperty("entitlements_token", out var entToken)) return entToken.GetString();
                            if (endpointKey == "leagueSessionToken")
                            {
                                if (jsonDoc.RootElement.TryGetProperty("token", out var sToken)) return sToken.GetString();
                                if (jsonDoc.RootElement.TryGetProperty("accessToken", out var aToken)) return aToken.GetString();
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private void ParseJwtPayload(string token)
        {
            try
            {
                var parsedInfo = JwtUtils.ParsePayload(token);
                _appSettings.ApiSettings.Token.Expiration = parsedInfo.Expiration;
                _appSettings.ApiSettings.Token.Puuid = parsedInfo.Puuid ?? "Unknown";
                _appSettings.ApiSettings.Token.Region = parsedInfo.Region ?? "Unknown";
                AppSettings.SaveSettings(_appSettings);
            }
            catch { }
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
            var response = await MakeRemoteRequestAsync("mythic_shop");
            if (response != null && response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                _directoriesCreator.CreateDirectory(_directoriesCreator.ApiCachePath);
                await File.WriteAllTextAsync(Path.Combine(_directoriesCreator.ApiCachePath, "mythic_shop.json"), json);
                return JsonSerializer.Deserialize<MythicShopResponse>(json);
            }
            return null;
        }

        public async Task<string> GetPassRewardsProgressionAsync(string eventId)
        {
            var response = await MakeRemoteRequestAsync("pass_rewards_progression", eventId: eventId);
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
            var response = await MakeRemoteRequestAsync("pass_rewards_rewards");
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

            string[] wadNames = { "default-assets2.wad", "default-assets.wad" };
            string pluginPath = Path.Combine(lolDirectory, "Plugins", "rcp-be-lol-game-data");

            foreach (var wadName in wadNames)
            {
                string wadPath = Path.Combine(pluginPath, wadName);
                if (!File.Exists(wadPath)) continue;

                try
                {
                    using var wadFile = new WadFile(wadPath);
                    string[] possiblePaths = { 
                        "global/default/v1/event-hub.json",
                        "plugins/rcp-be-lol-game-data/global/default/v1/event-hub.json",
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
                                                _logService.LogSuccess($"Active Pass ID found in {wadName}: {id.GetString()}");
                                                return id.GetString();
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        public async Task ExtractRewardIconsBatchAsync(IEnumerable<string> iconUrls, Action<string, string> onIconExtracted)
        {
            if (iconUrls == null || !iconUrls.Any()) return;

            string lolDirectory = _appSettings.ApiSettings.UsePbeForApi ? _appSettings.LolPbeDirectory : _appSettings.LolLiveDirectory;
            if (string.IsNullOrEmpty(lolDirectory)) return;

            string[] wadNames = { "default-assets.wad", "default-assets2.wad" };
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
                foreach (var wadName in wadNames)
                {
                    string wadPath = Path.Combine(pluginPath, wadName);
                    if (!File.Exists(wadPath)) continue;

                    using var wadFile = new WadFile(wadPath);
                    for (int i = remainingUrls.Count - 1; i >= 0; i--)
                    {
                        var iconUrl = remainingUrls[i];
                        
                        // DEFINITIVE CLEANING LOGIC
                        string basePath = iconUrl.ToLowerInvariant();
                        basePath = basePath.Replace("/lol-game-data/", "");
                        basePath = basePath.TrimStart('/');
                        basePath = Regex.Replace(basePath, "^(assets/)+", ""); // Strips all "assets/" from start
                        
                        // Verified root: plugins/rcp-be-lol-game-data/global/default/assets/
                        string fullPath = $"plugins/rcp-be-lol-game-data/global/default/assets/{basePath}";
                        
                        _logService.Log($"[WAD Check] Icon: {Path.GetFileName(iconUrl)} | Path: {fullPath}");
                        ulong pathHash = LeagueToolkit.Hashing.XxHash64Ext.Hash(fullPath);

                        if (wadFile.Chunks.TryGetValue(pathHash, out var chunk))
                        {
                            using var decompressedDataOwner = wadFile.LoadChunkDecompressed(chunk);
                            string destinationPath = Path.Combine(rewardsDir, Path.GetFileName(iconUrl));
                            await File.WriteAllBytesAsync(destinationPath, decompressedDataOwner.Span.ToArray());
                            
                            _logService.LogWarning($"[WAD Extraction] Discovery: {fullPath} in {wadName}");
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
            string destinationPath = Path.Combine(_directoriesCreator.ApiCachePath, "rewards", Path.GetFileName(iconUrl));
            if (File.Exists(destinationPath)) return destinationPath;

            await _extractionSemaphore.WaitAsync();
            try
            {
                if (File.Exists(destinationPath)) return destinationPath;
                string lolDirectory = _appSettings.ApiSettings.UsePbeForApi ? _appSettings.LolPbeDirectory : _appSettings.LolLiveDirectory;
                string pluginPath = Path.Combine(lolDirectory, "Plugins", "rcp-be-lol-game-data");
                string[] wadNames = { "default-assets.wad", "default-assets2.wad" };

                string basePath = iconUrl.ToLowerInvariant();
                basePath = basePath.Replace("/lol-game-data/", "");
                basePath = basePath.TrimStart('/');
                basePath = Regex.Replace(basePath, "^(assets/)+", "");

                foreach (var wadName in wadNames)
                {
                    string wadPath = Path.Combine(pluginPath, wadName);
                    if (!File.Exists(wadPath)) continue;
                    using var wadFile = new WadFile(wadPath);
                    
                    string fullPath = $"plugins/rcp-be-lol-game-data/global/default/assets/{basePath}";
                    ulong pathHash = LeagueToolkit.Hashing.XxHash64Ext.Hash(fullPath);
                    
                    if (wadFile.Chunks.TryGetValue(pathHash, out var chunk))
                    {
                        using var decompressedDataOwner = wadFile.LoadChunkDecompressed(chunk);
                        _directoriesCreator.CreateDirectory(Path.GetDirectoryName(destinationPath));
                        await File.WriteAllBytesAsync(destinationPath, decompressedDataOwner.Span.ToArray());
                        return destinationPath;
                    }
                }
            }
            finally { _extractionSemaphore.Release(); }
            return null;
        }

        private async Task<HttpResponseMessage> MakeLocalRequestAsync(string endpointPath)
        {
            if (string.IsNullOrEmpty(_appSettings.ApiSettings.Connection.LocalApiUrl)) return null;
            var requestUri = $"{_appSettings.ApiSettings.Connection.LocalApiUrl}{endpointPath}";
            _logService.LogWarning($"[LCU Request] {requestUri}");
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

            _logService.LogWarning($"[Remote Request Pending] Endpoint: {endpointKey} | Path: {tempPath}");
            string tokenKey = (endpointKey == "sales") ? "entitlementsToken" : "leagueSessionToken";
            string jwt = await GetTokenFromEndpoint(tokenKey);
            if (string.IsNullOrEmpty(jwt)) return null;

            _appSettings.ApiSettings.Token.Jwt = jwt;
            ParseJwtPayload(jwt);
            var region = Regex.Replace(_appSettings.ApiSettings.Token.Region?.ToLower() ?? "unknown", @"\d+$", "");
            if (region == "unknown") return null;

            var requestUri = $"{Endpoints.BaseUrlLive.Replace("{region}", region)}{tempPath}";
            _logService.LogWarning($"[Remote Request] {requestUri}");
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
