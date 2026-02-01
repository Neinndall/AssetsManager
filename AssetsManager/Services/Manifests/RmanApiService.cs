using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Services.Manifests;

public class RmanApiService
{
    private readonly HttpClient _httpClient;
    private const string GameVersionsUrl = "https://sieve.services.riotcdn.net/api/v1/products/lol/version-sets/PBE1?q[platform]=windows";
    private const string ClientConfigUrl = "https://clientconfig.rpg.riotgames.com/api/v1/config/public?namespace=keystone.products.league_of_legends.patchlines";

    public RmanApiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<RiotVersionInfo>> FetchVersionsAsync()
    {
        var versions = new List<RiotVersionInfo>();

        // 1. Fetch Game Client Versions (LoL Game)
        try 
        {
            var response = await _httpClient.GetStringAsync(GameVersionsUrl);
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("releases", out var releases))
            {
                foreach (var release in releases.EnumerateArray())
                {
                    var artifactId = release.GetProperty("release").GetProperty("labels").GetProperty("riot:artifact_type_id").GetProperty("values")[0].GetString();
                    var version = release.GetProperty("release").GetProperty("labels").GetProperty("riot:artifact_version_id").GetProperty("values")[0].GetString()?.Split('+')[0];
                    var url = release.GetProperty("download").GetProperty("url").GetString();

                    if (!string.IsNullOrEmpty(url))
                    {
                        versions.Add(new RiotVersionInfo 
                        { 
                            Product = "Game Client", 
                            Category = artifactId ?? "unknown", 
                            Version = version ?? "latest", 
                            ManifestUrl = url 
                        });
                    }
                }
            }
        }
        catch { }

        // 2. Fetch Client Configurations (Plugins/LCU)
        try 
        {
            var response = await _httpClient.GetStringAsync(ClientConfigUrl);
            using var doc = JsonDocument.Parse(response);
            var pbeConfig = doc.RootElement.GetProperty("keystone.products.league_of_legends.patchlines.pbe")
                                         .GetProperty("platforms").GetProperty("win")
                                         .GetProperty("configurations");

            foreach (var conf in pbeConfig.EnumerateArray())
            {
                var url = conf.GetProperty("patch_url").GetString();
                if (!string.IsNullOrEmpty(url))
                {
                    versions.Add(new RiotVersionInfo 
                    { 
                        Product = "League Client", 
                        Category = "plugins", 
                        Version = "latest", 
                        ManifestUrl = url 
                    });
                }
            }
        }
        catch { }

        return versions;
    }
}
