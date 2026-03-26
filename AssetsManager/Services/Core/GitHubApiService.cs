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
        private const string RepoOwner = "Neinndall";
        private const string RepoName = "AssetsManager";
        private const string UserAgent = "AssetsManager-Update-Client";

        public GitHubApiService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            // Optional: If you have a GitHub token for higher rate limits
            // _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "YOUR_TOKEN");
        }

        /// <summary>
        /// Fetches the recent commit history from a specific branch.
        /// </summary>
        public async Task<List<GitHubCommit>> GetCommitsAsync(string branch = "qa", int count = 20)
        {
            try
            {
                string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/commits?sha={branch}&per_page={count}";
                var commits = await _httpClient.GetFromJsonAsync<List<GitHubCommit>>(url);
                return commits ?? new List<GitHubCommit>();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to fetch GitHub commits for {RepoName} on branch {Branch}", RepoName, branch);
                return new List<GitHubCommit>();
            }
        }

        /// <summary>
        /// Fetches a specific release by tag (e.g., 'development' or 'qa').
        /// </summary>
        public async Task<GitHubRelease> GetReleaseAsync(string tag)
        {
            try
            {
                string url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/tags/{tag}";
                return await _httpClient.GetFromJsonAsync<GitHubRelease>(url);
            }
            catch (Exception ex)
            {
                Log.Warning("Release with tag {Tag} not found: {Message}", tag, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Fetches all assets from recent releases to find matching builds for any commit.
        /// </summary>
        public async Task<List<GitHubAsset>> GetAllAssetsAsync()
        {
            var allAssets = new List<GitHubAsset>();
            try
            {
                string releasesUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=30";
                var releases = await _httpClient.GetFromJsonAsync<List<GitHubRelease>>(releasesUrl);
                if (releases != null) allAssets.AddRange(releases.SelectMany(r => r.Assets));
                return allAssets;
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to fetch all releases: {Message}", ex.Message);
                return allAssets;
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
