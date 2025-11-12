using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models;
using AssetsManager.Utils;

namespace AssetsManager.Services.Api
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

        public async Task<bool> ReadLockfileAsync()
        {
            if (string.IsNullOrEmpty(_appSettings.LolLiveDirectory) || !Directory.Exists(_appSettings.LolLiveDirectory))
            {
                _logService.LogError("LoL Live Directory is not configured or does not exist.");
                return false;
            }

            var lockfilePath = Path.Combine(_appSettings.LolLiveDirectory, "lockfile");

            if (!File.Exists(lockfilePath))
            {
                _logService.LogError($"Lockfile not found at {lockfilePath}. Make sure the Live client is running.");
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
                _logService.LogError("The lockfile format is incorrect. Could not extract necessary data.");
            }
            catch (Exception ex)
            {             
                _logService.LogError(ex, "Error reading or processing lockfile.");
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

public async Task<bool> AquireJwtAsync()
{
    if (!_localEndpoints.TryGetValue("entitlementsToken", out var tokenEndpointPath))
    {
        _logService.LogError("Internal Error: The 'entitlementsToken' endpoint is not defined in the service.");
        return false;
    }

    try
    {
        var response = await MakeLocalRequestAsync(tokenEndpointPath);
        response.EnsureSuccessStatusCode();

        var jsonResponse = await response.Content.ReadAsStringAsync();
        var jsonDoc = JsonDocument.Parse(jsonResponse);
        if (jsonDoc.RootElement.TryGetProperty("accessToken", out var accessTokenElement))
        {
            var token = accessTokenElement.GetString();
            _appSettings.ApiSettings.Token.Jwt = token;

            // Parse the JWT to get real expiration and region
            ParseJwtPayload(token);

            AppSettings.SaveSettings(_appSettings);
            _logService.LogSuccess("JWT acquired and saved successfully.");
            return true;
        }
        _logService.LogError("The 'accessToken' not found in the JSON response when acquiring JWT.");
    }
    catch (HttpRequestException httpEx)
    {
        _logService.LogError(httpEx, "HTTP error while acquiring JWT.");
    }
    catch (JsonException jsonEx)
    {
        _logService.LogError(jsonEx, "JSON error while acquiring JWT.");
    }
    catch (Exception ex)
    {
        _logService.LogError(ex, "Unexpected error while acquiring JWT.");
    }

    return false;
}

private void ParseJwtPayload(string token)
{
    try
    {
        var payload = token.Split('.')[1];
        var padding = 4 - payload.Length % 4;
        if (padding < 4)
        {
            payload += new string('=', padding);
        }
        var bytes = Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/'));
        var jsonPayload = System.Text.Encoding.UTF8.GetString(bytes);

        using var jsonDoc = JsonDocument.Parse(jsonPayload);
        var root = jsonDoc.RootElement;

        // Expiration & Issued At
        if (root.TryGetProperty("exp", out var expElement) && expElement.TryGetInt64(out var expValue))
            _appSettings.ApiSettings.Token.Expiration = DateTimeOffset.FromUnixTimeSeconds(expValue).UtcDateTime;
        if (root.TryGetProperty("iat", out var iatElement) && iatElement.TryGetInt64(out var iatValue))
            _appSettings.ApiSettings.Token.IssuedAt = DateTimeOffset.FromUnixTimeSeconds(iatValue).UtcDateTime;

        // PUUID
        if (root.TryGetProperty("sub", out var subElement))
            _appSettings.ApiSettings.Token.Puuid = subElement.GetString();

        // Platform
        if (root.TryGetProperty("plt", out var pltElement) && pltElement.TryGetProperty("id", out var idElement))
            _appSettings.ApiSettings.Token.Platform = idElement.GetString();
        else if (root.TryGetProperty("lol.pvpnet.platform", out var pvpPltElement))
            _appSettings.ApiSettings.Token.Platform = pvpPltElement.GetString();

        // Region (with multiple fallbacks)
        if (root.TryGetProperty("lol.pvpnet.region", out var pvpRegElement))
            _appSettings.ApiSettings.Token.Region = pvpRegElement.GetString();
        else if (root.TryGetProperty("dat", out var datRegElement) && datRegElement.TryGetProperty("r", out var rElement))
            _appSettings.ApiSettings.Token.Region = rElement.GetString();
        else if (root.TryGetProperty("reg", out var regElement))
            _appSettings.ApiSettings.Token.Region = regElement.GetString();

        // Summoner ID (with fallback)
        if (root.TryGetProperty("lol.pvpnet.summoner.id", out var pvpIdElement) && pvpIdElement.TryGetInt64(out var pvpId))
            _appSettings.ApiSettings.Token.SummonerId = pvpId;
        else if (root.TryGetProperty("dat", out var datIdElement) && datIdElement.TryGetProperty("u", out var uElement) && uElement.TryGetInt64(out var uId))
            _appSettings.ApiSettings.Token.SummonerId = uId;
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
        var fileName = $"sales_data_{DateTime.Now:yyyyMMddHHmmss}.json";
        var filePath = Path.Combine(_directoriesCreator.ApiCachePath, fileName);
        await File.WriteAllTextAsync(filePath, salesJson);
        _logService.LogSuccess($"Sales data saved to API cache: {filePath}");

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
    if (string.IsNullOrEmpty(_appSettings.ApiSettings.Token.Jwt))
    {
        _logService.LogError("JWT not available to make remote request.");
        return null;
    }

    // Always use PBE base URL as per user's request
    var baseUrl = Endpoints.BaseUrlLive;
    
    if (string.IsNullOrEmpty(baseUrl))
    {
        _logService.LogError("The remote base URL (Live) is not configured.");
        return null;
    }

    if (!_remoteEndpoints.TryGetValue(endpointKey, out var endpointPath))
    {
        _logService.LogError($"The remote endpoint '{endpointKey}' is not defined in the service.");
        return null;
    }

    var requestUri = $"{baseUrl}{endpointPath}";

    var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
    request.Headers.Add("Authorization", $"Bearer {_appSettings.ApiSettings.Token.Jwt}");
    request.Headers.Add("User-Agent", "LeagueOfLegendsClient/15.1.645.4557 (rcp-be-lol-ranked)");
    request.Headers.Add("Accept", "application/json");

    try
    {
        var response = await _httpClient.SendAsync(request);

        if ((response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden) && retryCount > 0)
        {
            _logService.Log("Token expired or invalid. Attempting to refresh and retry...");
            bool refreshed = await AquireJwtAsync();
            if (refreshed)
            {
                return await MakeRemoteRequestAsync(endpointKey, retryCount - 1);
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
