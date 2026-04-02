using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using System.Linq;
using Serilog;

namespace AssetsManager.Services.Core
{
    /// <summary>
    /// Service for interacting with GitHub API to fetch commit history and development builds.
    /// Inspired by professional dev-tracking systems like FModel.
    /// </summary>
    public class GitHubApiService
    {
        private readonly HttpClient _httpClient;
        private readonly LogService _logService;
        private const string RepoOwner = "Neinndall";
        private const string RepoName = "AssetsManager";
        private const string UserAgent = "AssetsManager-Update-Client";

        // ETag Caching
        private readonly Dictionary<string, string> _etags = new Dictionary<string, string>();
        private readonly Dictionary<string, object> _cachedData = new Dictionary<string, object>();

        public GitHubApiService(HttpClient httpClient, LogService logService)
        {
            _httpClient = httpClient;
            _logService = logService;

            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            }
        }

        /// <summary>
        /// Fetches the recent commit history from a specific branch.
        /// </summary>
        public async Task<List<GitHubCommit>> GetCommitsAsync(string branch = "qa", int count = 20)
        {
            string cacheKey = $"commits_{branch}_{count}";
            string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/commits?sha={branch}&per_page={count}";

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (_etags.TryGetValue(cacheKey, out var etag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                }

                var response = await _httpClient.SendAsync(request);

                if (response.StatusCode == System.Net.HttpStatusCode.NotModified && _cachedData.ContainsKey(cacheKey))
                {
                    return (List<GitHubCommit>)_cachedData[cacheKey];
                }

                response.EnsureSuccessStatusCode();

                if (response.Headers.ETag != null)
                {
                    _etags[cacheKey] = response.Headers.ETag.Tag;
                }

                var commits = await response.Content.ReadFromJsonAsync<List<GitHubCommit>>();
                var result = commits ?? new List<GitHubCommit>();
                
                _cachedData[cacheKey] = result;
                return result;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to fetch GitHub commits for {RepoName} on branch {branch}");
                
                if (_cachedData.TryGetValue(cacheKey, out var cached))
                {
                    return (List<GitHubCommit>)cached;
                }
                
                return new List<GitHubCommit>();
            }
        }

        /// <summary>
        /// Fetches a specific release by tag (e.g., 'development' or 'qa').
        /// </summary>
        public async Task<GitHubRelease> GetReleaseAsync(string tag)
        {
            string cacheKey = $"release_{tag}";
            string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/tags/{tag}";

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (_etags.TryGetValue(cacheKey, out var etag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                }

                var response = await _httpClient.SendAsync(request);

                if (response.StatusCode == System.Net.HttpStatusCode.NotModified && _cachedData.ContainsKey(cacheKey))
                {
                    return (GitHubRelease)_cachedData[cacheKey];
                }

                response.EnsureSuccessStatusCode();

                if (response.Headers.ETag != null)
                {
                    _etags[cacheKey] = response.Headers.ETag.Tag;
                }

                var release = await response.Content.ReadFromJsonAsync<GitHubRelease>();
                _cachedData[cacheKey] = release;
                return release;
            }
            catch (Exception ex)
            {
                _logService.LogWarning($"Release with tag {tag} not found or error: {ex.Message}");

                if (_cachedData.TryGetValue(cacheKey, out var cached))
                {
                    return (GitHubRelease)cached;
                }

                return null;
            }
        }

        /// <summary>
        /// Fetches all assets from recent releases to find matching builds for any commit.
        /// </summary>
        public async Task<List<GitHubAsset>> GetAllAssetsAsync()
        {
            string cacheKey = "all_assets";
            string releasesUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=30";

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, releasesUrl);
                if (_etags.TryGetValue(cacheKey, out var etag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                }

                var response = await _httpClient.SendAsync(request);

                if (response.StatusCode == System.Net.HttpStatusCode.NotModified && _cachedData.ContainsKey(cacheKey))
                {
                    return (List<GitHubAsset>)_cachedData[cacheKey];
                }

                response.EnsureSuccessStatusCode();

                if (response.Headers.ETag != null)
                {
                    _etags[cacheKey] = response.Headers.ETag.Tag;
                }

                var releases = await response.Content.ReadFromJsonAsync<List<GitHubRelease>>();
                var allAssets = releases?.SelectMany(r => r.Assets).ToList() ?? new List<GitHubAsset>();
                
                _cachedData[cacheKey] = allAssets;
                return allAssets;
            }
            catch (Exception ex)
            {
                _logService.LogWarning($"Failed to fetch all releases: {ex.Message}");
                
                if (_cachedData.TryGetValue(cacheKey, out var cached))
                {
                    return (List<GitHubAsset>)cached;
                }

                return new List<GitHubAsset>();
            }
        }
    }

    #region Models

    public class GitHubCommit
    {
        [JsonPropertyName("sha")]
        public string Sha { get; set; }

        [JsonPropertyName("commit")]
        public CommitInfo Commit { get; set; }

        [JsonPropertyName("author")]
        public GitHubUser Author { get; set; }

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; }

        // Local helper for UI to link with a build asset
        public GitHubAsset DownloadableAsset { get; set; }
        public GitHubAsset ParentBuildAsset { get; set; }
        public string ParentBuildSha { get; set; }
        public bool HasInheritedBuild => DownloadableAsset == null && ParentBuildAsset != null;
        public bool IsLatest { get; set; }
        public string ShortSha => Sha?.Substring(0, 7) ?? "";
    }

    public class CommitInfo
    {
        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("author")]
        public CommitAuthor Author { get; set; }
    }

    public class CommitAuthor
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("date")]
        public DateTime Date { get; set; }
    }

    public class GitHubUser
    {
        [JsonPropertyName("login")]
        public string Login { get; set; }

        [JsonPropertyName("avatar_url")]
        public string AvatarUrl { get; set; }
    }

    public class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; }
    }

    public class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; }
    }

    #endregion
}
