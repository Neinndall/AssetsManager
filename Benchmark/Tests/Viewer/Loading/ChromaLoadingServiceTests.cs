using System;
using System.IO;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Views.Models.Viewer;
using Serilog;
using Xunit;

namespace AssetsManager.BenchmarkTests.Tests.Viewer.Loading
{
    public sealed class ChromaLoadingServiceTests
    {
        [Fact]
        public async Task LoadFamiliesAsync_GroupsChromasUnderNearestPrimarySkin()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                $"assetsmanager-chroma-groups-{Guid.NewGuid():N}");

            try
            {
                CreateSkin(root, "base", hasModel: true);
                CreateSkin(root, "skin01", hasModel: false);
                CreateSkin(root, "skin02", hasModel: true);
                CreateSkin(root, "skin03", hasModel: false);
                CreateSkin(root, "skin04", hasModel: false);

                using var logger = new LoggerConfiguration().CreateLogger();
                var loader = new ChromaLoadingService(new LogService(logger));

                var families = await loader.LoadFamiliesAsync(root);

                Assert.Collection(
                    families,
                    family => AssertFamily(family, "BASE", "skin01"),
                    family => AssertFamily(family, "SKIN02", "skin03", "skin04"));
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }

        private static void AssertFamily(
            ChromaFamilyModel family,
            string expectedName,
            params string[] expectedChromas)
        {
            Assert.Equal(expectedName, family.Name);
            Assert.Equal(expectedChromas.Length, family.ChromaCount);
            Assert.Equal(
                expectedChromas,
                System.Linq.Enumerable.Select(family.Chromas, chroma => chroma.Name.ToLowerInvariant()));
            Assert.All(family.Chromas, chroma => Assert.Equal(family.ModelPath, chroma.ModelPath));
        }

        private static void CreateSkin(string root, string name, bool hasModel)
        {
            string directory = Path.Combine(root, name);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, $"{name}_tx_cm.tex"), Array.Empty<byte>());
            if (hasModel)
                File.WriteAllBytes(Path.Combine(directory, $"{name}.skn"), Array.Empty<byte>());
        }
    }
}
