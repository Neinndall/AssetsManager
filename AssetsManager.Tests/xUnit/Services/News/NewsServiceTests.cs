using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.News;
using AssetsManager.Utils;
using Serilog;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.News
{
    public sealed class NewsServiceTests : IDisposable
    {
        private readonly string _rootPath;
        private readonly DirectoriesCreator _directories;
        private readonly LogService _logService;

        public NewsServiceTests()
        {
            _rootPath = Path.Combine(Path.GetTempPath(), "AssetsManager.NewsTests", Guid.NewGuid().ToString("N"));
            _directories = new DirectoriesCreator(_rootPath);
            _logService = new LogService(new LoggerConfiguration().CreateLogger());
        }

        [Fact]
        public async Task MarkingArticleAsSeenPersistsUrlAndDate()
        {
            var service = CreateService();
            string url = "https://www.leagueoflegends.com/en-us/news/dev/test-article";
            var published = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Local);

            await service.MarkAsSeenAsync(url, published);

            string json = File.ReadAllText(_directories.NewsSeenPath);
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.GetProperty("items");
            var entry = Assert.Single(items.EnumerateArray());
            Assert.Equal(url, entry.GetProperty("url").GetString());
            Assert.Equal(published.ToString("o"), entry.GetProperty("publishedAt").GetString());
        }

        [Fact]
        public async Task MarkingSameArticleTwiceStoresSingleEntry()
        {
            var service = CreateService();
            string url = "https://www.leagueoflegends.com/en-us/news/dev/test-article";

            await service.MarkAsSeenAsync(url, DateTime.Now);
            await service.MarkAsSeenAsync(url, DateTime.Now);

            string json = File.ReadAllText(_directories.NewsSeenPath);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(1, doc.RootElement.GetProperty("items").GetArrayLength());
        }

        [Fact]
        public async Task MarkingAsSeenIsIgnoredForEmptyOrNullUrls()
        {
            var service = CreateService();

            await service.MarkAsSeenAsync(null, DateTime.Now);
            await service.MarkAsSeenAsync(string.Empty, DateTime.Now);

            Assert.False(File.Exists(_directories.NewsSeenPath));
        }

        private NewsService CreateService()
        {
            return new NewsService(new HttpClient(), _logService, _directories);
        }

        public void Dispose()
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, true);
            }
        }
    }
}
