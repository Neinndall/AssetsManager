using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Dialogs;

namespace AssetsManager.Services.Core
{
    /// <summary>
    /// Service for interacting with GitHub API to fetch commit history and development builds.
    /// Incorporates persistent disk caching, ETag conditional validation (304 Not Modified),
    /// and graceful rate-limit backoff to protect network quotas on VPNs and shared IPs.
    /// </summary>
    public class GitHubApiService
    {
        private readonly HttpClient _httpClient;
        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private const string RepoOwner = "Neinndall";
        private const string RepoName = "AssetsManager";
        private const string UserAgent = "AssetsManager-Update-Client";

        // Persistent Cache and Rate Limit
        private readonly string _cacheFilePath;
        private readonly object _cacheLock = new object();
        private Dictionary<string, GitHubCacheEntry> _cacheEntries = new Dictionary<string, GitHubCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private DateTimeOffset? _rateLimitResetTime;

        public bool IsRateLimited => _rateLimitResetTime.HasValue && DateTimeOffset.UtcNow < _rateLimitResetTime.Value;
        public DateTimeOffset? RateLimitResetTime => _rateLimitResetTime;

        public GitHubApiService(HttpClient httpClient, LogService logService, DirectoriesCreator directoriesCreator = null)
        {
            _httpClient = httpClient;
            _logService = logService;
            _directoriesCreator = directoriesCreator;

            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            }

            if (_directoriesCreator != null)
            {
                _cacheFilePath = Path.Combine(_directoriesCreator.UpdateCachePath, "github_api_cache.json");
                LoadCacheFromDisk();
            }
        }

        private void LoadCacheFromDisk()
        {
            try
            {
                if (!string.IsNullOrEmpty(_cacheFilePath) && File.Exists(_cacheFilePath))
                {
                    string json = File.ReadAllText(_cacheFilePath);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, GitHubCacheEntry>>(json);
                    if (loaded != null)
                    {
                        lock (_cacheLock)
                        {
                            _cacheEntries = loaded;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogDebug($"[GitHubApiService] Could not read disk cache: {ex.Message}");
            }
        }

        private void SaveCacheToDisk()
        {
            try
            {
                if (string.IsNullOrEmpty(_cacheFilePath)) return;
                string dir = Path.GetDirectoryName(_cacheFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                lock (_cacheLock)
                {
                    string json = JsonSerializer.Serialize(_cacheEntries);
                    File.WriteAllText(_cacheFilePath, json);
                }
            }
            catch (Exception ex)
            {
                _logService.LogDebug($"[GitHubApiService] Could not write disk cache: {ex.Message}");
            }
        }

        private bool TryGetEtag(string key, out string etag)
        {
            lock (_cacheLock)
            {
                if (_cacheEntries.TryGetValue(key, out var entry) && !string.IsNullOrEmpty(entry.ETag))
                {
                    etag = entry.ETag;
                    return true;
                }
                etag = null;
                return false;
            }
        }

        private bool TryGetCachedData<T>(string key, out T data)
        {
            lock (_cacheLock)
            {
                if (_cacheEntries.TryGetValue(key, out var entry) && !string.IsNullOrEmpty(entry.JsonPayload))
                {
                    try
                    {
                        data = JsonSerializer.Deserialize<T>(entry.JsonPayload);
                        return data != null;
                    }
                    catch { }
                }
                data = default;
                return false;
            }
        }

        private void SetCachedData<T>(string key, string etag, T data)
        {
            if (data == null) return;
            try
            {
                string json = JsonSerializer.Serialize(data);
                lock (_cacheLock)
                {
                    _cacheEntries[key] = new GitHubCacheEntry
                    {
                        ETag = etag,
                        JsonPayload = json,
                        LastUpdated = DateTime.UtcNow
                    };
                }
                SaveCacheToDisk();
            }
            catch (Exception ex)
            {
                _logService.LogDebug($"[GitHubApiService] Failed to cache data for {key}: {ex.Message}");
            }
        }

        private void HandleRateLimit(HttpResponseMessage response)
        {
            try
            {
                if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues))
                {
                    string resetRaw = resetValues.FirstOrDefault();
                    if (long.TryParse(resetRaw, out long resetUnix))
                    {
                        _rateLimitResetTime = DateTimeOffset.FromUnixTimeSeconds(resetUnix);
                        return;
                    }
                }
            }
            catch { }

            _rateLimitResetTime = DateTimeOffset.UtcNow.AddMinutes(15);
        }

        private async Task<T> SendCachedGetAsync<T>(string url, string cacheKey, string descriptor)
        {
            if (IsRateLimited)
            {
                if (TryGetCachedData<T>(cacheKey, out var rateLimitedFallback))
                {
                    return rateLimitedFallback;
                }
                return default;
            }

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (TryGetEtag(cacheKey, out var etag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                }

                var response = await _httpClient.SendAsync(request);

                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    if (TryGetCachedData<T>(cacheKey, out var cachedData))
                    {
                        return cachedData;
                    }
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    HandleRateLimit(response);
                    if (TryGetCachedData<T>(cacheKey, out var cachedFallback))
                    {
                        _logService.Log($"GitHub API rate limit in effect for this IP. Serving cached {descriptor} (resets at {_rateLimitResetTime:HH:mm:ss}).");
                        return cachedFallback;
                    }
                    _logService.LogWarning($"GitHub API rate limit in effect for this IP (resets at {_rateLimitResetTime:HH:mm:ss}).");
                    return default;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return default;
                }

                response.EnsureSuccessStatusCode();

                string responseEtag = response.Headers.ETag?.Tag;
                var data = await response.Content.ReadFromJsonAsync<T>();
                if (data != null)
                {
                    SetCachedData(cacheKey, responseEtag, data);
                }
                return data;
            }
            catch (Exception ex)
            {
                if (TryGetCachedData<T>(cacheKey, out var cachedFallback))
                {
                    return cachedFallback;
                }
                _logService.LogWarning($"Could not load {descriptor}: {ex.Message}");
                return default;
            }
        }

        /// <summary>
        /// Fetches the recent commit history from a specific branch with persistent caching and rate-limit mitigation.
        /// </summary>
        public async Task<List<GitHubCommit>> GetCommitsAsync(string branch = "qa", int count = 100)
        {
            string cacheKey = $"commits_{branch}_{count}";
            string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/commits?sha={branch}&per_page={count}";
            return await SendCachedGetAsync<List<GitHubCommit>>(url, cacheKey, $"commits from branch '{branch}'") ?? new List<GitHubCommit>();
        }

        /// <summary>
        /// Fetches a specific release by tag (e.g., 'development' or 'qa') with disk fallback.
        /// </summary>
        public async Task<GitHubRelease> GetReleaseAsync(string tag)
        {
            string cacheKey = $"release_{tag}";
            string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/tags/{tag}";
            return await SendCachedGetAsync<GitHubRelease>(url, cacheKey, $"release '{tag}'");
        }

        /// <summary>
        /// Fetches all assets from recent releases to find matching builds for any commit.
        /// </summary>
        public async Task<List<GitHubAsset>> GetAllAssetsAsync()
        {
            string cacheKey = "all_assets";
            string releasesUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=30";
            var releases = await SendCachedGetAsync<List<GitHubRelease>>(releasesUrl, cacheKey, "releases") ?? new List<GitHubRelease>();
            return releases.SelectMany(r => r.Assets).ToList();
        }

        /// <summary>
        /// Fetches commits and automatically links them with their direct or inherited builds.
        /// Centralizes revision domain logic in the service layer for a cleaner architecture.
        /// </summary>
        public async Task<List<GitHubCommit>> GetEnrichedCommitsAsync(string branch = "qa", string releaseTag = "qa-testing", int count = 100)
        {
            try
            {
                // 1. Fetch raw data
                var commits = await GetCommitsAsync(branch, count);
                var release = await GetReleaseAsync(releaseTag);
                var assets = release?.Assets?.OrderByDescending(a => a.CreatedAt).ToList() ?? new List<GitHubAsset>();

                // 2. Direct Linking
                foreach (var commit in commits)
                {
                    commit.DownloadableAsset = assets.FirstOrDefault(a => 
                        !string.IsNullOrEmpty(commit.Sha) && a.Name.Contains(commit.Sha, StringComparison.OrdinalIgnoreCase));
                    
                    commit.IsLatest = commits.IndexOf(commit) == 0;
                }

                // 3. Build Inheritance Logic
                // Links commits without a direct ZIP to the nearest future build containing their changes.
                GitHubAsset currentActiveAsset = null;
                string currentActiveSha = null;

                foreach (var commit in commits.OrderByDescending(c => c.Commit.Author.Date))
                {
                    if (commit.DownloadableAsset != null)
                    {
                        currentActiveAsset = commit.DownloadableAsset;
                        currentActiveSha = commit.ShortSha;
                    }
                    else if (currentActiveAsset != null)
                    {
                        commit.ParentBuildAsset = currentActiveAsset;
                        commit.ParentBuildSha = currentActiveSha;
                    }
                }

                // 4. Virtual Commits for Orphaned Assets
                // Ensures builds that don't have a corresponding commit in the recent list are still visible.
                foreach (var asset in assets)
                {
                    bool isOrphan = !commits.Any(c => c.DownloadableAsset?.DownloadUrl == asset.DownloadUrl);
                    if (isOrphan)
                    {
                        string sha = asset.Name.Replace(".zip", "");
                        if (sha.Contains("qa_")) sha = sha.Split("qa_").Last();
                        
                        commits.Add(new GitHubCommit
                        {
                            Sha = sha,
                            Commit = new CommitInfo 
                            { 
                                Message = $"Build found in QA release ({asset.Name})",
                                Author = new CommitAuthor { Name = "GitHub Action", Date = asset.CreatedAt }
                            },
                            DownloadableAsset = asset,
                            IsLatest = false
                        });
                    }
                }

                return commits.OrderByDescending(c => c.Commit.Author.Date).ToList();
            }
            catch (Exception ex)
            {
                _logService.LogWarning($"Could not load full commit history: {ex.Message}");
                return new List<GitHubCommit>();
            }
        }
    }

    public class GitHubCacheEntry
    {
        public string ETag { get; set; }
        public string JsonPayload { get; set; }
        public DateTime LastUpdated { get; set; }
    }
}
