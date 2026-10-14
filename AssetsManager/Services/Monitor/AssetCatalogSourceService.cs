using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Views.Models.Settings;

namespace AssetsManager.Services.Monitor;

public interface IAssetCatalogSource
{
    Task<AssetCatalogImportBatch> ReadAsync(CancellationToken cancellationToken, AssetCatalogQuery query = null);
}

public sealed class AssetCatalogSourceService : IAssetCatalogSource
{
    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private readonly LogService _log;
    private readonly Dictionary<string, int> _lookupOffsets = new();
    private static readonly string[] Types = { "BUNDLES", "EVENT_PASS", "CURRENCY", "HEXTECH_CRAFTING", "REGALIA_BANNER", "SKIN_AUGMENT" };

    public AssetCatalogSourceService(AppSettings settings, HttpClient http, LogService log)
    {
        _settings = settings;
        _http = http;
        _log = log;
    }

    public async Task<AssetCatalogImportBatch> ReadAsync(CancellationToken cancellationToken, AssetCatalogQuery query = null)
    {
        string categoryId = string.IsNullOrEmpty(query?.CategoryId) ? null : query.CategoryId;
        string inventory = AssetCatalogRouting.GetInventoryType(categoryId);
        if (categoryId != null && inventory == null) throw new ArgumentException("Unknown catalog category.", nameof(query));
        bool Matches(AssetCatalogArticle article) => categoryId == null || article.CategoryId == categoryId;
        bool pbe = _settings.ApiSettings.ClientTarget == ApiClientTarget.PBE;
        string environment = pbe ? "PBE" : "LIVE";
        string directory = pbe ? _settings.LolPbeDirectory : _settings.LolLiveDirectory;
        var batch = new AssetCatalogImportBatch { Environment = environment };
        var api = new Dictionary<string, AssetCatalogArticle>(StringComparer.Ordinal);
        batch.Status = $"{environment}: open and sign in to the selected client, then fetch its catalog.";
        if (string.IsNullOrWhiteSpace(directory)) return batch;

        try
        {
            string lockfile = Path.Combine(directory, "lockfile");
            if (!File.Exists(lockfile)) return batch;
            using var stream = new FileStream(lockfile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string[] parts = (await reader.ReadToEndAsync(cancellationToken)).Trim().Split(':');
            if (parts.Length < 5 || parts[4] != "https" || !int.TryParse(parts[2], out int port) || port is < 1 or > 65535)
                return batch;
            string baseUrl = $"https://127.0.0.1:{port}";
            string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("riot:" + parts[3]));
            using var region = await GetAsync(baseUrl, auth, "/riotclient/region-locale", cancellationToken);
            string actualRegion = region == null ? null : Text(region.RootElement, "region");
            if (string.IsNullOrEmpty(actualRegion) || actualRegion.Equals("PBE", StringComparison.OrdinalIgnoreCase) != pbe)
            {
                batch.Status = $"{environment}: selected client is unavailable or its environment does not match.";
                return batch;
            }
            using var session = await GetAsync(baseUrl, auth, "/lol-login/v1/session", cancellationToken);
            if (session == null || Text(session.RootElement, "state") != "SUCCEEDED")
            {
                batch.Status = $"{environment}: sign in to the client before syncing the catalog.";
                return batch;
            }
            batch.ClientReadSucceeded = true;
            batch.Region = Text(session.RootElement, "platformId") ?? actualRegion;
            using var summoner = await GetOptionalAsync(baseUrl, auth, "/lol-summoner/v1/current-summoner", cancellationToken);
            if (summoner != null && Number(summoner.RootElement, "summonerId", out long summonerId))
                batch.SummonerId = summonerId;
            using var ready = await GetAsync(baseUrl, auth, "/lol-store/v1/store-ready", cancellationToken);
            if (ready?.RootElement.ValueKind != JsonValueKind.True)
            {
                batch.Status = $"{environment}: the client store is not ready yet.";
                return batch;
            }

            string inventoryQuery = Uri.EscapeDataString(JsonSerializer.Serialize(inventory == null ? Types : new[] { inventory }));
            using var catalog = await GetAsync(baseUrl, auth, "/lol-store/v1/catalog?inventoryType=" + inventoryQuery, cancellationToken);
            if (catalog != null)
                foreach (var article in ParseStore(catalog.RootElement, environment).Where(Matches)) api[article.Key] = article;
            batch.ApiReadSucceeded = catalog != null;
            var lookup = new HashSet<(string Type, long Id)>();
            if (query != null) lookup.UnionWith(query.KnownItems.Where(key => key.Id > 0 && Types.Contains(key.Type)
                && (categoryId == null || AssetCatalogRouting.GetArticleCategory(key.Type, key.Id) == categoryId)));
            foreach (var article in api.Values.Where(item => item.ImageUrl == null))
                lookup.Add((article.InventoryType, article.ItemId));
            using var featured = await GetOptionalAsync(baseUrl, auth, "/lol-store/v1/featured", cancellationToken);
            if (featured != null) FindReferences(featured.RootElement, lookup);

            var selected = new List<(string Type, long Id)>();
            foreach (var group in lookup.Where(key => (categoryId == null || AssetCatalogRouting.GetArticleCategory(key.Type, key.Id) == categoryId)
                    && (!api.TryGetValue($"{environment}:{key.Type}:{key.Id}", out var article) || article.ImageUrl == null)).GroupBy(key => key.Type))
            {
                var keys = group.OrderByDescending(key => key.Id).ToArray();
                string offsetKey = environment + ":" + categoryId + ":" + group.Key;
                _lookupOffsets.TryGetValue(offsetKey, out int offset);
                offset %= keys.Length;
                int count = Math.Min(96, keys.Length);
                for (int i = 0; i < count; i++) selected.Add(keys[(offset + i) % keys.Length]);
                _lookupOffsets[offsetKey] = (offset + count) % keys.Length;
            }
            foreach (var keys in selected.Chunk(40))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string keyJson = JsonSerializer.Serialize(keys.Select(key => new { inventoryType = key.Type, itemId = key.Id }));
                using var details = await GetOptionalAsync(baseUrl, auth, "/lol-store/v1/catalog/items/skip-cache?catalogItemKeys=" + Uri.EscapeDataString(keyJson), cancellationToken);
                if (details != null)
                    foreach (var article in ParseStore(details.RootElement, environment).Where(Matches)) api[article.Key] = article;
            }
            batch.ApiReadSucceeded |= api.Count > 0;
            batch.Status = batch.ApiReadSucceeded
                ? $"{environment}: {api.Count} catalog articles loaded; {selected.Count} targeted lookups requested."
                : $"{environment}: the client catalog is unavailable. Try Fetch Catalog again when the store is ready.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or JsonException)
        {
            _log.LogError(ex, "Asset catalog source could not read the selected client. Existing catalog data was retained.");
            batch.Status = $"{environment}: client catalog unavailable; saved data was retained.";
        }
        batch.Articles.AddRange(api.Values);
        return batch;
    }

    internal static List<AssetCatalogArticle> ParseStore(JsonElement root, string environment)
    {
        var result = new List<AssetCatalogArticle>();
        if (root.ValueKind != JsonValueKind.Array) return result;
        foreach (var row in root.EnumerateArray())
        {
            if (!Number(row, "itemId", out long id)) continue;
            string type = Text(row, "inventoryType"), subtype = Text(row, "subInventoryType");
            string category = AssetCatalogRouting.GetArticleCategory(type, id, subtype);
            if (category == null) continue;
            string name = Text(row, "name");
            if (row.TryGetProperty("localizations", out var languages) && languages.ValueKind == JsonValueKind.Object)
                foreach (var language in languages.EnumerateObject())
                {
                    string localized = Text(language.Value, "name");
                    if (!string.IsNullOrWhiteSpace(localized)) { name = localized; break; }
                }
            result.Add(new AssetCatalogArticle
            {
                Environment = environment, InventoryType = type, SubInventoryType = subtype, ItemId = id,
                CategoryId = category, Name = name, Source = "LCU", ImageUrl = AssetCatalogRouting.NormalizeCdnUrl(Text(row, "iconUrl")),
                Active = row.TryGetProperty("active", out var active) && active.ValueKind is JsonValueKind.True or JsonValueKind.False ? active.GetBoolean() : null,
                FirstSeenApi = DateTime.UtcNow, LastSeenApi = DateTime.UtcNow
            });
        }
        return result;
    }

    private static void FindReferences(JsonElement node, HashSet<(string Type, long Id)> result)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            string type = Text(node, "inventoryType");
            if (Types.Contains(type) && Number(node, "itemId", out long id)) result.Add((type, id));
            foreach (var property in node.EnumerateObject()) FindReferences(property.Value, result);
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) FindReferences(item, result);
    }

    private async Task<JsonDocument> GetOptionalAsync(string baseUrl, string auth, string path, CancellationToken token)
    {
        try { return await GetAsync(baseUrl, auth, path, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            _log.LogWarning($"Optional catalog request {path.Split('?')[0]} was unavailable; retrieved articles were retained.");
            return null;
        }
    }

    private async Task<JsonDocument> GetAsync(string baseUrl, string auth, string path, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning($"Asset catalog request {path.Split('?')[0]} returned HTTP {(int)response.StatusCode}.");
            return null;
        }
        if (response.Content.Headers.ContentLength > 16 * 1024 * 1024) throw new InvalidDataException("Client catalog exceeds its size limit.");
        using var content = await response.Content.ReadAsStreamAsync(timeout.Token);
        return await JsonDocument.ParseAsync(content, cancellationToken: timeout.Token);
    }

    private static string Text(JsonElement row, string property) => row.ValueKind == JsonValueKind.Object
        && row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Number(JsonElement row, string property, out long number)
    {
        number = 0;
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(property, out var value)) return false;
        return value.ValueKind == JsonValueKind.Number ? value.TryGetInt64(out number)
            : value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number);
    }
}
