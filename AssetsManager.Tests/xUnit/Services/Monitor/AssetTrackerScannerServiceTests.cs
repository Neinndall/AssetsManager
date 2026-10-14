using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Monitor;
using AssetsManager.Views.Models.Monitor;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Monitor
{
    public class AssetTrackerScannerServiceTests
    {
        [Fact]
        public void BuildCandidateIds_PrioritizesFailedIdsAndFillsToWindow()
        {
            var service = new AssetTrackerScannerService(new HttpClient(new ProbeHandler(_ => Response(HttpStatusCode.NotFound))));
            var category = Category();
            category.ForwardScanWindow = 3;
            category.Entries[100] = new AssetTrackerEntry { AssetId = 100, State = TrackedAssetState.Available, WasCdnProbed = true };
            category.Entries[101] = new AssetTrackerEntry { AssetId = 101, State = TrackedAssetState.Missing, WasCdnProbed = true };

            var candidates = service.BuildCandidateIds(category);

            Assert.Equal(new long[] { 101, 102, 103 }, candidates);
        }

        [Fact]
        public async Task ScanAsync_FallsBackToSecondExtensionAndMarksAvailable()
        {
            var service = new AssetTrackerScannerService(new HttpClient(new ProbeHandler(request =>
                request.RequestUri.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    ? Response(HttpStatusCode.OK, "image/png")
                    : Response(HttpStatusCode.NotFound))));
            var category = Category();
            category.Extensions = new() { "jpg", "png" };

            AssetTrackerScanResult result = await service.ScanAsync(category, new long[] { 100 }, CancellationToken.None);

            Assert.Equal(1, result.NewDiscoveries);
            Assert.Equal(TrackedAssetState.Available, category.Entries[100].State);
            Assert.EndsWith("100.png", category.Entries[100].Url);
        }

        [Fact]
        public async Task ScanAsync_DoesNotTreatServerErrorsAsMissing()
        {
            var service = new AssetTrackerScannerService(new HttpClient(new ProbeHandler(_ => Response(HttpStatusCode.ServiceUnavailable))));
            var category = Category();

            await service.ScanAsync(category, new long[] { 100 }, CancellationToken.None);

            Assert.Equal(TrackedAssetState.TemporaryError, category.Entries[100].State);
            Assert.Equal(1, category.Entries[100].FailureCount);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("text/html")]
        [InlineData("application/json")]
        public async Task ProbeUrl_DoesNotAccept200WithoutImageContent(string mediaType)
        {
            using var http = new HttpClient(new ProbeHandler(_ => Response(HttpStatusCode.OK, mediaType)));
            var result = await new AssetTrackerScannerService(http).ProbeUrlAsync("https://cdn.example/assets/100.png");
            Assert.False(result.IsAvailable);
            Assert.False(result.IsTemporaryFailure);
        }

        [Fact]
        public async Task NumericBundleDiscoveryCanFindSuffixedImageBeforeCatalogMetadata()
        {
            using var http = new HttpClient(new ProbeHandler(request => request.RequestUri.AbsolutePath.EndsWith("99901659_1.png", StringComparison.Ordinal)
                ? Response(HttpStatusCode.OK, "image/png") : Response(HttpStatusCode.Forbidden)));
            var category = AssetsManager.Views.Models.Monitor.DefaultCategories.Get().Single(item => item.Id == "2");
            var result = await new AssetTrackerScannerService(http).ScanAsync(category, new long[] { 99901659 }, default);
            Assert.Equal(1, result.NewDiscoveries);
            Assert.Equal(99901659, category.Entries.Single().Key);
            Assert.EndsWith("99901659_1.png", category.Entries.Single().Value.Url);
        }

        [Theory]
        [InlineData(HttpStatusCode.TooManyRequests)]
        [InlineData(HttpStatusCode.ServiceUnavailable)]
        public async Task ProbeUrl_RetriesTemporaryFailuresBeforeAcceptingImage(HttpStatusCode status)
        {
            int count = 0;
            using var http = new HttpClient(new ProbeHandler(_ => ++count < 3 ? Response(status) : Response(HttpStatusCode.OK, "image/png")));
            var result = await new AssetTrackerScannerService(http).ProbeUrlAsync("https://cdn.example/assets/100.png");
            Assert.True(result.IsAvailable);
            Assert.False(result.IsTemporaryFailure);
            Assert.Equal(3, result.AttemptCount);
        }

        private static AssetCategory Category() => new()
        {
            Id = "test",
            BaseUrl = "https://cdn.example/assets/",
            Start = 100,
            Extension = "jpg",
            Extensions = new() { "jpg" },
            ForwardScanWindow = 1,
            MaxConcurrency = 2
        };

        private static HttpResponseMessage Response(HttpStatusCode status, string mediaType = null)
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            if (mediaType != null) response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
            return response;
        }

        private sealed class ProbeHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;
            public ProbeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) => _response = response;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(_response(request));
        }
    }
}
