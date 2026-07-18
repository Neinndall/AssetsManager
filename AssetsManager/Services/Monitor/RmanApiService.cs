using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Services.Monitor;

public sealed class RmanApiService
{
    private const string GameVersionsUrl = "https://sieve.services.riotcdn.net/api/v1/products/lol/version-sets/PBE1?q[platform]=windows";
    private const string ClientConfigUrl = "https://clientconfig.rpg.riotgames.com/api/v1/config/public?namespace=keystone.products.league_of_legends.patchlines";
    private const string PbeConfigurationKey = "keystone.products.league_of_legends.patchlines.pbe";

    private readonly HttpClient _httpClient;
    private readonly LogService _logService;

    public RmanApiService(HttpClient httpClient, LogService logService)
    {
        _httpClient = httpClient;
        _logService = logService;
    }

    public async Task<List<RiotVersionInfo>> FetchVersionsAsync(CancellationToken cancellationToken = default)
    {
        Task<List<RiotVersionInfo>> gameVersions = FetchGameVersionsAsync(cancellationToken);
        Task<List<RiotVersionInfo>> clientVersions = FetchClientVersionsAsync(cancellationToken);
        await Task.WhenAll(gameVersions, clientVersions).ConfigureAwait(false);

        return gameVersions.Result
            .Concat(clientVersions.Result)
            .DistinctBy(version => (version.Product, version.Category, version.ManifestUrl))
            .ToList();
    }

    private async Task<List<RiotVersionInfo>> FetchGameVersionsAsync(CancellationToken cancellationToken)
    {
        var versions = new List<RiotVersionInfo>();
        try
        {
            string response = await _httpClient.GetStringAsync(GameVersionsUrl, cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty("releases", out JsonElement releases)
                || releases.ValueKind != JsonValueKind.Array)
            {
                return versions;
            }

            foreach (JsonElement release in releases.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!release.TryGetProperty("release", out JsonElement releaseInfo)
                    || !releaseInfo.TryGetProperty("labels", out JsonElement labels)
                    || !TryGetLabel(labels, "riot:artifact_type_id", out string artifactId)
                    || !release.TryGetProperty("download", out JsonElement download)
                    || !TryGetString(download, "url", out string manifestUrl))
                {
                    continue;
                }

                string version = TryGetLabel(labels, "riot:artifact_version_id", out string fullVersion)
                    ? fullVersion.Split('+', 2)[0]
                    : "latest";
                versions.Add(new RiotVersionInfo
                {
                    Product = "Game Client",
                    Category = artifactId,
                    Version = version,
                    ManifestUrl = manifestUrl
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logService.LogError(ex, "Failed to fetch game manifests from Riot Sieve API.");
        }

        return versions;
    }

    private async Task<List<RiotVersionInfo>> FetchClientVersionsAsync(CancellationToken cancellationToken)
    {
        var versions = new List<RiotVersionInfo>();
        try
        {
            string response = await _httpClient.GetStringAsync(ClientConfigUrl, cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty(PbeConfigurationKey, out JsonElement pbe)
                || !pbe.TryGetProperty("platforms", out JsonElement platforms)
                || !platforms.TryGetProperty("win", out JsonElement windows)
                || !windows.TryGetProperty("configurations", out JsonElement configurations)
                || configurations.ValueKind != JsonValueKind.Array)
            {
                return versions;
            }

            foreach (JsonElement configuration in configurations.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetString(configuration, "patch_url", out string manifestUrl)) continue;
                versions.Add(new RiotVersionInfo
                {
                    Product = "League Client",
                    Category = "plugins",
                    Version = "latest",
                    ManifestUrl = manifestUrl
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logService.LogError(ex, "Failed to fetch client manifests from Riot Client Config API.");
        }

        return versions;
    }

    private static bool TryGetLabel(JsonElement labels, string name, out string value)
    {
        value = null;
        return labels.TryGetProperty(name, out JsonElement label)
               && label.TryGetProperty("values", out JsonElement values)
               && values.ValueKind == JsonValueKind.Array
               && values.GetArrayLength() > 0
               && values[0].ValueKind == JsonValueKind.String
               && !string.IsNullOrWhiteSpace(value = values[0].GetString());
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = null;
        return element.TryGetProperty(name, out JsonElement property)
               && property.ValueKind == JsonValueKind.String
               && !string.IsNullOrWhiteSpace(value = property.GetString());
    }
}
