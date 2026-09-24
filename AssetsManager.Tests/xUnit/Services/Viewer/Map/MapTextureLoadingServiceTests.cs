using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Resolvers;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapTextureLoadingServiceTests
    {
        // 1x1 RGBA PNG. Keeping this fixture inline makes the regression independent of
        // League data on the machine running the suite.
        private static readonly byte[] ValidPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z0gAAAABJRU5ErkJggg==");

        [Fact]
        public async Task CorruptTextureDoesNotAbortOtherPreviewTextures()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "AssetsManagerMapTextureTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                const string validVirtualPath = "assets/maps/tests/valid.png";
                const string corruptVirtualPath = "assets/maps/tests/corrupt.png";
                string validPath = Path.Combine(root, validVirtualPath.Replace('/', Path.DirectorySeparatorChar));
                string corruptPath = Path.Combine(root, corruptVirtualPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(validPath)!);
                File.WriteAllBytes(validPath, ValidPng);
                File.WriteAllBytes(corruptPath, new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x00, 0x01, 0x02 });

                var resolver = new MapAssetResolver(null, null);
                using var loader = new MapTextureLoadingService(resolver, null);

                var loaded = await loader.LoadLightmapsPreviewAsync(
                    new[] { validVirtualPath, corruptVirtualPath },
                    root,
                    CancellationToken.None);

                Assert.True(loaded.ContainsKey(validVirtualPath));
                Assert.False(loaded.ContainsKey(corruptVirtualPath));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
