using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.News;

namespace AssetsManager.Services.News
{
    public class NewsService
    {
        private const string NewsApiBaseUrl = "https://soraclee.github.io/riotgames-news-api/data/lol/";
        public const string BrowserUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        private const int MaxSeenEntries = 1000;
        private const int MaxItemsPerCategory = 10;
        private readonly HttpClient _httpClient;
        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly object _cacheSyncRoot = new();
        private readonly object _pendingSyncRoot = new();
        private readonly SemaphoreSlim _stateGate = new(1, 1);
        private readonly Dictionary<NewsCategory, CacheEntry> _cache = new();

        private NewsItemModel _pendingArticle;

        public NewsService(HttpClient httpClient, LogService logService, DirectoriesCreator directoriesCreator)
        {
            _httpClient = httpClient;
            _logService = logService;
            _directoriesCreator = directoriesCreator;
        }

        public async Task<IReadOnlyList<NewsItemModel>> GetNewsAsync(NewsCategory category, bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            lock (_cacheSyncRoot)
            {
                if (!forceRefresh && _cache.TryGetValue(category, out var cached))
                {
                    return cached.Items;
                }
            }

            var items = await FetchAsync(category, cancellationToken);
            var recentItems = items
                .OrderByDescending(item => item.PublishedAt)
                .Take(MaxItemsPerCategory)
                .ToList();

            lock (_cacheSyncRoot)
            {
                _cache[category] = new CacheEntry(recentItems);
            }
            return recentItems;
        }

        /// <summary>
        /// Background discovery of newly published Riot articles.
        /// The first call establishes a baseline (no notifications); subsequent calls
        /// return every item whose ActionUrl was not seen before AND that was published
        /// after the newest article the user already saw, so nothing old is ever
        /// re-notified while every genuinely new article is reported. The seen state
        /// is kept in sync with the current feed after each run. File I/O is
        /// serialized through <see cref="_stateGate"/> together with
        /// <see cref="MarkAsSeenAsync"/>.
        /// </summary>
        public async Task<IReadOnlyList<NewsItemModel>> CheckForNewNewsAsync(CancellationToken cancellationToken = default)
        {
            var items = await FetchAsync(NewsCategory.AllNews, cancellationToken);
            if (items == null || items.Count == 0) return Array.Empty<NewsItemModel>();

            await _stateGate.WaitAsync(cancellationToken);
            try
            {
                var state = await LoadSeenStateAsync();
                if (state.Urls.Count == 0)
                {
                    await PersistSeenStateAsync(items);
                    _logService.LogDebug("News discovery baseline established.");
                    return Array.Empty<NewsItemModel>();
                }

                // Reference date: the newest article the user already saw, either from
                // the persisted state (new format with dates) or, for legacy url-only
                // entries, from the feed items themselves. Anything published at or
                // before this date is old and must never be notified again.
                var newestSeenDate = state.NewestSeenDate;
                foreach (var item in items)
                {
                    if (!string.IsNullOrEmpty(item.ActionUrl) &&
                        state.Urls.Contains(item.ActionUrl) &&
                        item.PublishedAt > newestSeenDate)
                    {
                        newestSeenDate = item.PublishedAt;
                    }
                }

                var newItems = items
                    .Where(item => !string.IsNullOrEmpty(item.ActionUrl) && !state.Urls.Contains(item.ActionUrl))
                    .Where(item => item.PublishedAt > newestSeenDate)
                    .OrderByDescending(item => item.PublishedAt)
                    .ToList();

                // Keep the seen set in sync with the current feed: everything still
                // published is retained (with its publish date), plus previously seen
                // entries, so nothing in the feed can ever be re-detected as new again.
                var merged = items
                    .Concat(state.Items)
                    .Where(item => !string.IsNullOrEmpty(item.ActionUrl))
                    .GroupBy(item => item.ActionUrl, StringComparer.Ordinal)
                    .Select(group => group.OrderByDescending(item => item.PublishedAt).First())
                    .OrderByDescending(item => item.PublishedAt)
                    .Take(MaxSeenEntries);
                await PersistSeenStateAsync(merged);

                if (newItems.Count > 0)
                {
                    _logService.LogDebug($"News discovery found {newItems.Count} new article(s).");
                }

                return newItems;
            }
            finally
            {
                _stateGate.Release();
            }
        }

        /// <summary>
        /// Marks a single article as seen so it is never notified again, without
        /// touching the rest of the seen state. Used when the user dismisses or
        /// executes a news notification. Serialized with
        /// <see cref="CheckForNewNewsAsync"/> through <see cref="_stateGate"/>.
        /// </summary>
        public async Task MarkAsSeenAsync(string url, DateTime publishedAt)
        {
            if (string.IsNullOrEmpty(url)) return;

            await _stateGate.WaitAsync();
            try
            {
                var state = await LoadSeenStateAsync();
                if (state.Urls.Contains(url)) return;

                var entry = NewsItemModel.FromSeenEntry(url, publishedAt);
                state.Items.Add(entry);
                state.Urls.Add(url);
                await PersistSeenStateAsync(state.Items);
            }
            finally
            {
                _stateGate.Release();
            }
        }

        public void SetPendingArticle(NewsItemModel article)
        {
            lock (_pendingSyncRoot)
            {
                _pendingArticle = article;
            }
        }

        public NewsItemModel TakePendingArticle()
        {
            lock (_pendingSyncRoot)
            {
                var article = _pendingArticle;
                _pendingArticle = null;
                return article;
            }
        }

        private async Task<SeenState> LoadSeenStateAsync()
        {
            try
            {
                if (!File.Exists(_directoriesCreator.NewsSeenPath)) return new SeenState();

                string json = await File.ReadAllTextAsync(_directoriesCreator.NewsSeenPath);
                using var doc = JsonDocument.Parse(json);

                var state = new SeenState();

                if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    // Current format: entries with url + publish date.
                    foreach (var entry in items.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String) continue;
                        string url = urlElement.GetString();
                        if (string.IsNullOrEmpty(url)) continue;

                        var model = NewsItemModel.FromSeenEntry(url,
                            entry.TryGetProperty("publishedAt", out var dateElement) &&
                            DateTimeOffset.TryParse(dateElement.GetString(), out var parsed)
                                ? parsed.LocalDateTime
                                : DateTime.MinValue);

                        state.Items.Add(model);
                        state.Urls.Add(url);
                        if (model.PublishedAt > state.NewestSeenDate) state.NewestSeenDate = model.PublishedAt;
                    }
                }
                else if (doc.RootElement.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
                {
                    // Legacy format (urls only): keep entries without dates; the first
                    // persist run migrates the file to the new format.
                    foreach (var entry in ids.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.String) continue;
                        string url = entry.GetString();
                        if (string.IsNullOrEmpty(url)) continue;

                        state.Items.Add(NewsItemModel.FromSeenEntry(url, DateTime.MinValue));
                        state.Urls.Add(url);
                    }
                }

                return state;
            }
            catch (Exception ex)
            {
                _logService.LogWarning($"Failed to load seen news state: {ex.Message}");
                return new SeenState();
            }
        }

        private async Task PersistSeenStateAsync(IEnumerable<NewsItemModel> items)
        {
            try
            {
                string directory = Path.GetDirectoryName(_directoriesCreator.NewsSeenPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var payload = items
                    .Where(item => !string.IsNullOrEmpty(item.ActionUrl))
                    .OrderByDescending(item => item.PublishedAt)
                    .Take(MaxSeenEntries)
                    .Select(item => new
                    {
                        url = item.ActionUrl,
                        publishedAt = item.PublishedAt.ToString("o")
                    })
                    .ToList();

                string json = JsonSerializer.Serialize(new { items = payload });
                await File.WriteAllTextAsync(_directoriesCreator.NewsSeenPath, json);
            }
            catch (Exception ex)
            {
                _logService.LogWarning($"Failed to persist seen news state: {ex.Message}");
            }
        }

        private sealed class SeenState
        {
            public List<NewsItemModel> Items { get; } = new();
            public HashSet<string> Urls { get; } = new(StringComparer.Ordinal);
            public DateTime NewestSeenDate { get; set; }
        }

        private async Task<List<NewsItemModel>> FetchAsync(NewsCategory category, CancellationToken cancellationToken)
        {
            string url = NewsApiBaseUrl + GetEndpoint(category);
            var items = new List<NewsItemModel>();
            try
            {
                using var response = await _httpClient.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                if (doc.RootElement.ValueKind != JsonValueKind.Array) return items;

                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    var item = NewsItemModel.FromJson(element);
                    if (item != null) items.Add(item);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logService.LogError(ex, $"Failed to fetch news feed for category: {category}");
                throw;
            }
            return items;
        }

        public async Task<ArticleFullContent> FetchArticleFullContentAsync(string articleUrl, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(articleUrl)) return null;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, articleUrl);
                request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) return null;

                string html = await response.Content.ReadAsStringAsync(cancellationToken);
                _logService.LogDebug($"FetchArticleFullContentAsync: status={response.StatusCode}, len={html?.Length ?? 0} from {articleUrl}");

                var content = ExtractNextDataArticleContent(html);
                if (content != null)
                {
                    _logService.LogDebug($"FetchArticleFullContentAsync: __NEXT_DATA__ content extracted (html={content.Html?.Length ?? 0}).");
                    return content;
                }

                // Fallback: server-rendered HTML extraction (plain text).
                string fallback = ExtractArticleBodyFromHtml(html);
                _logService.LogDebug($"FetchArticleFullContentAsync: fallback text extraction (len={fallback?.Length ?? 0}).");
                return new ArticleFullContent { PlainText = fallback };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logService.LogWarning($"Failed to fetch full article content from {articleUrl}: {ex.Message}");
                return null;
            }
        }

        private static ArticleFullContent ExtractNextDataArticleContent(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;

            const string marker = "id=\"__NEXT_DATA__\"";
            int markerIndex = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) return null;

            int jsonStart = html.IndexOf('>', markerIndex);
            if (jsonStart < 0) return null;
            jsonStart++;

            int jsonEnd = html.IndexOf("</script>", jsonStart, StringComparison.OrdinalIgnoreCase);
            if (jsonEnd < 0) return null;

            try
            {
                using var doc = JsonDocument.Parse(html.Substring(jsonStart, jsonEnd - jsonStart));
                if (!doc.RootElement.TryGetProperty("props", out var props) ||
                    !props.TryGetProperty("pageProps", out var pageProps) ||
                    !pageProps.TryGetProperty("page", out var page) ||
                    !page.TryGetProperty("blades", out var blades) ||
                    blades.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var content = new ArticleFullContent();
                var bodies = new List<string>();

                foreach (var blade in blades.EnumerateArray())
                {
                    if (!blade.TryGetProperty("type", out var typeElement)) continue;
                    string bladeType = typeElement.GetString() ?? string.Empty;

                    if (string.Equals(bladeType, "articleMasthead", StringComparison.OrdinalIgnoreCase))
                    {
                        if (blade.TryGetProperty("banner", out var banner) && banner.TryGetProperty("url", out var bannerUrl))
                        {
                            content.BannerUrl = bannerUrl.GetString();
                        }
                        if (blade.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array)
                        {
                            content.Authors = authors.EnumerateArray()
                                .Where(a => a.TryGetProperty("name", out var authorName))
                                .Select(a => a.GetProperty("name").GetString())
                                .Where(a => !string.IsNullOrWhiteSpace(a))
                                .ToList();
                        }
                        continue;
                    }

                    // Article body: any rich text blade (articleRichText, patchNotesRichText, ...).
                    if (!bladeType.EndsWith("RichText", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!blade.TryGetProperty("richText", out var richText) ||
                        !richText.TryGetProperty("body", out var body) ||
                        body.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string bodyHtml = body.GetString();
                    if (!string.IsNullOrWhiteSpace(bodyHtml)) bodies.Add(bodyHtml);
                }

                if (bodies.Count > 0) content.Html = string.Join("\n", bodies);
                return content.Html != null || content.BannerUrl != null ? content : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string ExtractArticleBodyFromHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;

            // Remove script, style, nav, footer, header, svg, iframe tags and their contents
            html = System.Text.RegularExpressions.Regex.Replace(html, @"<(script|style|nav|footer|header|svg|iframe)\b[^>]*>.*?</\1>", "", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Convert HTML formatting to text linebreaks
            html = System.Text.RegularExpressions.Regex.Replace(html, @"</?(h1|h2|h3|h4|h5|h6)\b[^>]*>", "\n\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            html = System.Text.RegularExpressions.Regex.Replace(html, @"</?p\b[^>]*>", "\n\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            html = System.Text.RegularExpressions.Regex.Replace(html, @"<li\b[^>]*>", "\n\u2022 ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            html = System.Text.RegularExpressions.Regex.Replace(html, @"<br\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            html = System.Text.RegularExpressions.Regex.Replace(html, @"<hr\s*/?>", "\n---\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Strip remaining HTML tags
            html = System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
            
            // Decode HTML entities
            html = System.Net.WebUtility.HtmlDecode(html);

            // Filter out short JS strings/noise lines and keep real article sentences
            var lines = html.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(line => line.Trim())
                            .Where(line => line.Length > 15 && 
                                           !line.StartsWith("window.", StringComparison.OrdinalIgnoreCase) &&
                                           !line.StartsWith("function", StringComparison.OrdinalIgnoreCase) &&
                                           !line.StartsWith("self.__next", StringComparison.OrdinalIgnoreCase) &&
                                           !line.Contains("Cookie", StringComparison.OrdinalIgnoreCase) &&
                                           !line.Contains("Privacy Notice", StringComparison.OrdinalIgnoreCase));

            string result = string.Join("\n\n", lines);
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        private static string GetEndpoint(NewsCategory category) => category switch
        {
            NewsCategory.Dev => "devEn.json",
            NewsCategory.Esports => "esportsEn.json",
            NewsCategory.GameUpdates => "gameUpdatesEn.json",
            NewsCategory.Lore => "loreEn.json",
            NewsCategory.Media => "mediaEn.json",
            NewsCategory.PatchNotes => "patchNoteEn.json",
            _ => "allNewsEn.json"
        };

        private sealed class CacheEntry
        {
            public IReadOnlyList<NewsItemModel> Items { get; }

            public CacheEntry(IReadOnlyList<NewsItemModel> items)
            {
                Items = items;
            }
        }
    }

    public class ArticleFullContent
    {
        public string Html { get; set; }
        public string PlainText { get; set; }
        public string BannerUrl { get; set; }
        public List<string> Authors { get; set; }
    }
}
