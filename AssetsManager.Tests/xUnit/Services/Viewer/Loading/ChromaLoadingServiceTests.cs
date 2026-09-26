using System;
using System.IO;
using AssetsManager.Utils;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Views.Models.Viewer;
using Serilog;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Loading
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

        [Theory]
        [InlineData(false, "skin02")]
        [InlineData(true, "skin02")]
        [InlineData(false, "skin05")]
        [InlineData(true, "skin05")]
        public async Task LoadFamiliesAsync_PreservesFolderFamilyWhileUsingDeclaredMesh(bool hashedMesh, string parent)
        {
            string project = Path.Combine(Path.GetTempPath(), $"assetsmanager-chroma-binding-{Guid.NewGuid():N}");
            string root = Path.Combine(project, "assets", "characters", "test", "skins");
            try
            {
                CreateSkin(root, "skin02", hasModel: true);
                CreateSkin(root, "skin03", hasModel: true);
                CreateSkin(root, "skin04", hasModel: false);
                CreateSkin(root, "skin05", hasModel: true);
                WriteChromaBin(project, parent, hashedMesh);
                using var logger = new LoggerConfiguration().CreateLogger();
                var loader = new ChromaLoadingService(new LogService(logger));

                var families = await loader.LoadFamiliesAsync(root);

                ChromaFamilyModel family = Assert.Single(families);
                Assert.Equal("SKIN03", family.Name);
                Assert.Equal(Path.Combine(root, "skin03", "skin03.skn"), family.ModelPath);
                ChromaSkinModel chroma = Assert.Single(family.Chromas);
                Assert.Equal("SKIN04", chroma.Name);
                Assert.Equal(Path.Combine(root, parent, $"{parent}.skn"), chroma.ModelPath);
                Assert.Equal(Path.Combine(root, "skin04"), chroma.TexturePath);
            }
            finally
            {
                if (Directory.Exists(project))
                    Directory.Delete(project, true);
            }
        }

        [Fact]
        public async Task LoadFamiliesAsync_DoesNotSubstituteAnotherMeshWhenDeclaredMeshIsMissing()
        {
            string project = Path.Combine(Path.GetTempPath(), $"assetsmanager-chroma-missing-{Guid.NewGuid():N}");
            string root = Path.Combine(project, "assets", "characters", "test", "skins");
            try
            {
                CreateSkin(root, "skin03", hasModel: true);
                CreateSkin(root, "skin04", hasModel: false);
                WriteChromaBin(project, "skin02", false);
                using var logger = new LoggerConfiguration().CreateLogger();
                var loader = new ChromaLoadingService(new LogService(logger));

                Assert.Empty(await loader.LoadFamiliesAsync(root));
            }
            finally
            {
                if (Directory.Exists(project))
                    Directory.Delete(project, true);
            }
        }

        private static void WriteChromaBin(string project, string parent, bool hashedMesh)
        {
            string virtualPath = $"assets/characters/test/skins/{parent}/{parent}.skn";
            uint field = Fnv1a.HashLower("simpleSkin");
            BinTreeProperty model = hashedMesh
                ? new BinTreeWadChunkLink(field, XxHash64Ext.Hash(virtualPath))
                : new BinTreeString(field, virtualPath.ToUpperInvariant());
            var mesh = new BinTreeStruct(
                Fnv1a.HashLower("skinMeshProperties"),
                Fnv1a.HashLower("SkinMeshDataProperties"),
                new[] { model });
            var skin = new BinTreeObject("Characters/Test/Skins/Skin4", "SkinCharacterDataProperties",
                new BinTreeProperty[] { mesh });
            var tree = new BinTree(new[] { skin }, Array.Empty<string>());
            string path = Path.Combine(project, "data", "characters", "test", "skins", "skin4.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            tree.Write(stream);
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
