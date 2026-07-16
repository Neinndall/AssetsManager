using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Monitor;
using AssetsManager.Views.Models.Monitor;
using Xunit;

namespace AssetsManager.BenchmarkTests.Tests.Monitor
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
