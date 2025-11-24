using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.IO.Compression;
using System.Text.RegularExpressions;
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
                    _appSettings.ApiSettings.Connection.Lockfile = lockfileContent;
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
                _logService.LogError("Lockfile password not available to generate authentication header.");
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
                _logService.LogError($"Internal Error: The '{endpointKey}' endpoint is not defined in the service.");
                return null;
            }

            try
            {
                var response = await MakeLocalRequestAsync(tokenEndpointPath);
                response.EnsureSuccessStatusCode();
                var rawResponse = await response.Content.ReadAsStringAsync();
                string token = null;

                try
                {
                    using (var jsonDoc = JsonDocument.Parse(rawResponse))
                    {
                        if (jsonDoc.RootElement.ValueKind == JsonValueKind.Object &&
                            jsonDoc.RootElement.TryGetProperty("accessToken", out var accessTokenElement))
                        {
                            token = accessTokenElement.GetString();
                        }
                        else if (jsonDoc.RootElement.ValueKind == JsonValueKind.String)
                        {
                            token = jsonDoc.RootElement.GetString();
                        }
                        else
                        {
                            token = rawResponse.Trim('"');
                        }
                    }
                }
                catch (JsonException)
                {
                    token = rawResponse.Trim('"');
                }

                if (!string.IsNullOrEmpty(token))
                {
                    return token;
                }
                _logService.LogWarning($"Token was null or empty after processing response from {endpointKey}.");
            }
            catch (HttpRequestException httpEx)
            {
                _logService.LogWarning($"HTTP error while acquiring token from {endpointKey}: {httpEx.Message}");
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Unexpected error while acquiring token from {endpointKey}.");
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
                // Fallback to default values if parsing fails
                _appSettings.ApiSettings.Token.Expiration = DateTime.UtcNow.AddHours(1);
                _appSettings.ApiSettings.Token.Region = "Unknown";
                _appSettings.ApiSettings.Token.Puuid = "Unknown";
                _appSettings.ApiSettings.Token.SummonerId = 0;
                _appSettings.ApiSettings.Token.Platform = "Unknown";
                _appSettings.ApiSettings.Token.IssuedAt = DateTime.UtcNow;
            }
        }

        public async Task<string> GetSalesAsync()
        {
            var response = await MakeRemoteRequestAsync("sales");
            if (response != null && response.IsSuccessStatusCode)
            {
                var salesJson = await response.Content.ReadAsStringAsync();

                // Save to API cache
                await _directoriesCreator.CreateDirApiCacheAsync();
                var fileName = "sales.json";
                var filePath = Path.Combine(_directoriesCreator.ApiCachePath, fileName);
                await File.WriteAllTextAsync(filePath, salesJson);
                return salesJson;
            }
            return null;
        }

        public async Task<SalesCatalog> GetSalesCatalogAsync()
        {
            var salesJson = await GetSalesAsync();
            if (!string.IsNullOrEmpty(salesJson))
            {
                return JsonSerializer.Deserialize<SalesCatalog>(salesJson);
            }
            return null;
        }

        public async Task<string> GetMythicShopAsync()
        {
            var response = await MakeRemoteRequestAsync("mythic_shop");
            if (response != null && response.IsSuccessStatusCode)
            {
                var mythicShopJson = await response.Content.ReadAsStringAsync();

                // Save to API cache
                await _directoriesCreator.CreateDirApiCacheAsync();
                var fileName = "mythic_shop.json";
                var filePath = Path.Combine(_directoriesCreator.ApiCachePath, fileName);
                await File.WriteAllTextAsync(filePath, mythicShopJson);
                return mythicShopJson;
            }
            else if (response != null)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logService.LogWarning($"Mythic Shop request failed with status code: {response.StatusCode}. Content: {errorContent}");
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

        private async Task<HttpResponseMessage> MakeLocalRequestAsync(string endpointPath)
        {
            if (string.IsNullOrEmpty(_appSettings.ApiSettings.Connection.LocalApiUrl))
            {
                _logService.LogError("LocalApiUrl not configured. Cannot make local request.");
                return null;
            }

            var requestUri = $"{_appSettings.ApiSettings.Connection.LocalApiUrl}{endpointPath}";
            var authHeader = GetLocalAuthHeader();

            if (string.IsNullOrEmpty(authHeader))
            {
                _logService.LogError("Local authentication header is empty. Could not make local request.");
                return null;
            }

            var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Add("Authorization", authHeader);

            return await _httpClient.SendAsync(request);
        }

        private async Task<HttpResponseMessage> MakeRemoteRequestAsync(string endpointKey, int retryCount = 1)
        {
            string tokenEndpointKey;
            if (endpointKey == "sales")
            {
                tokenEndpointKey = "entitlementsToken";
            }
            else if (endpointKey == "mythic_shop")
            {
                tokenEndpointKey = "leagueSessionToken";
            }
            else
            {
                _logService.LogError($"No token acquisition strategy defined for endpoint key: {endpointKey}");
                return null;
            }

            string jwt = await GetTokenFromEndpoint(tokenEndpointKey);

            if (string.IsNullOrEmpty(jwt))
            {
                _logService.LogError($"Failed to acquire necessary token ('{tokenEndpointKey}') for remote request.");
                return null;
            }

            // We need to parse the token to get region info for the URL and for the UI.
            _appSettings.ApiSettings.Token.Jwt = jwt;
            ParseJwtPayload(jwt);
            var region = _appSettings.ApiSettings.Token.Region?.ToLower();

            if (string.IsNullOrEmpty(region) || region == "unknown")
            {
                _logService.LogError("Could not determine region from JWT. Cannot make remote request.");
                return null;
            }

            region = Regex.Replace(region, @"\d+$", "");
            var baseUrl = Endpoints.BaseUrlLive.Replace("{region}", region);

            if (!_remoteEndpoints.TryGetValue(endpointKey, out var endpointPath))
            {
                _logService.LogError($"The remote endpoint '{endpointKey}' is not defined in the service.");
                return null;
            }

            var requestUri = $"{baseUrl}{endpointPath}";
            var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Add("Authorization", $"Bearer {jwt}");
            request.Headers.Add("User-Agent", "LeagueOfLegendsClient/15.1.645.4557 (rcp-be-lol-ranked)");
            request.Headers.Add("Accept", "application/json");

            try
            {
                var response = await _httpClient.SendAsync(request);

                if ((response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden) && retryCount > 0)
                {
                    _logService.Log($"Token for {endpointKey} was rejected. Attempting to refresh and retry...");
                    // Re-acquire the specific token needed
                    string refreshedJwt = await GetTokenFromEndpoint(tokenEndpointKey);
                    if (!string.IsNullOrEmpty(refreshedJwt))
                    {
                        // Update the header with the new token for the retry
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", refreshedJwt);
                        return await _httpClient.SendAsync(request); // Use the same request object with the updated header
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
    }
}
