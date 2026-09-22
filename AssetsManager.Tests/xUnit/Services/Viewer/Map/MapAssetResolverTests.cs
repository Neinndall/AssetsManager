using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapAssetResolverTests
    {
        [Fact]
        public void MapPathBuildsCanonicalMapFiles()
        {
            MapPath map = MapPath.FromEntryPath("Maps/MapGeometry/Map11/Base_SRX");

            Assert.Equal("Maps/MapGeometry/Map11/Base_SRX", map.Value);
            Assert.Equal("data/maps/mapgeometry/map11/base_srx.mapgeo", map.GeometryVirtualPath);
            Assert.Equal("data/maps/mapgeometry/map11/base_srx.materials.bin", map.MaterialsVirtualPath);
        }

        [Theory]
        [InlineData("DATA/Maps/MapGeometry/Map11/Base_SRX.mapgeo")]
        [InlineData("data/maps/mapgeometry/map11/base_srx.materials.bin")]
        public void MapPathAcceptsCanonicalFilePaths(string input)
        {
            Assert.True(MapPath.TryFromEntryPath(input, out MapPath map));
            Assert.Equal("Maps/MapGeometry/Map11/Base_SRX", map.Value, ignoreCase: true);
        }

        [Fact]
        public void GeometryFileDerivesMapPathFromDataTree()
        {
            string root = NewTempDirectory();
            try
            {
                string file = Path.Combine(root, "DATA", "Maps", "MapGeometry", "Map11", "Base_SRX.mapgeo");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, Array.Empty<byte>());

                Assert.True(MapPath.TryFromGeometryFile(file, out MapPath map));
                Assert.Equal("Maps/MapGeometry/Map11/Base_SRX", map.Value, ignoreCase: true);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void GeometryFileOutsideDataTreeIsNotAMapFile()
        {
            string root = NewTempDirectory();
            try
            {
                string file = Path.Combine(root, "Maps", "MapGeometry", "Map11", "Base_SRX.mapgeo");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, Array.Empty<byte>());

                Assert.False(MapPath.TryFromGeometryFile(file, out _));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ResolverKeepsSelectedGeometryAndSiblingMaterialsAuthoritative()
        {
            string root = NewTempDirectory();
            try
            {
                string directory = Path.Combine(root, "DATA", "Maps", "MapGeometry", "Map11");
                Directory.CreateDirectory(directory);
                string geometry = Path.Combine(directory, "Base_SRX.mapgeo");
                string materials = Path.Combine(directory, "Base_SRX.materials.bin");
                File.WriteAllBytes(geometry, new byte[] { 1 });
                File.WriteAllBytes(materials, new byte[] { 2 });

                MapSceneSource source = MapSceneSource.FromGeometryFile(geometry, root);
                var resolver = new MapAssetResolver(null, null);
                MapSceneAssets assets = await resolver.ResolveSceneAssetsAsync(source);

                Assert.Equal(MapAssetOrigin.SelectedFile, assets.Geometry.Origin);
                Assert.Equal(Path.GetFullPath(geometry), assets.Geometry.PhysicalPath);
                Assert.Equal(MapAssetOrigin.ProjectFile, assets.Materials.Origin);
                Assert.Equal(Path.GetFullPath(materials), assets.Materials.PhysicalPath);
                Assert.Equal(new byte[] { 2 }, await resolver.ReadBytesAsync(assets.Materials));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ResolverFindsCanonicalProjectOverrideWhenThereIsNoSiblingFile()
        {
            string root = NewTempDirectory();
            try
            {
                string selectedDirectory = Path.Combine(root, "selected");
                Directory.CreateDirectory(selectedDirectory);
                string selectedGeometry = Path.Combine(selectedDirectory, "Base_SRX.mapgeo");
                File.WriteAllBytes(selectedGeometry, new byte[] { 1 });

                string canonicalMaterials = Path.Combine(
                    root,
                    "data",
                    "maps",
                    "mapgeometry",
                    "map11",
                    "base_srx.materials.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(canonicalMaterials)!);
                File.WriteAllBytes(canonicalMaterials, new byte[] { 3 });

                MapSceneSource source = new(
                    MapPath.FromEntryPath("Maps/MapGeometry/Map11/Base_SRX"),
                    selectedGeometry,
                    root);
                var resolver = new MapAssetResolver(null, null);
                MapSceneAssets assets = await resolver.ResolveSceneAssetsAsync(source);

                Assert.Equal(MapAssetOrigin.ProjectFile, assets.Materials.Origin);
                Assert.Equal(Path.GetFullPath(canonicalMaterials), assets.Materials.PhysicalPath);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ResolverFindsProjectTextureOverridesBeforeInstallation()
        {
            string root = NewTempDirectory();
            try
            {
                string texture = Path.Combine(root, "assets", "maps", "test", "stone_cm.tex");
                Directory.CreateDirectory(Path.GetDirectoryName(texture)!);
                File.WriteAllBytes(texture, new byte[] { 1, 2, 3, 4 });

                var reference = new MapTextureReference("assets/maps/test/stone_cm.tex", 0);
                var resolver = new MapAssetResolver(null, null);
                var resolved = await resolver.ResolveTexturesAsync(new[] { reference }, root);

                Assert.True(resolved.TryGetValue(reference, out MapResolvedAsset asset));
                Assert.Equal(MapAssetOrigin.ProjectFile, asset.Origin);
                Assert.Equal(Path.GetFullPath(texture), asset.PhysicalPath);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Theory]
        [InlineData(new byte[] { 0x54, 0x45, 0x58, 0x00 }, ".tex")]
        [InlineData(new byte[] { 0x44, 0x44, 0x53, 0x20 }, ".dds")]
        [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, ".png")]
        public void TextureLoaderDetectsHashOnlyTextureFormats(byte[] magic, string expected)
        {
            using var stream = new MemoryStream(magic);

            Assert.Equal(expected, MapTextureLoadingService.DetectTextureExtension(stream, null));
            Assert.Equal(0, stream.Position);
        }

        [Fact]
        public void TextureLoadingUsesLtkPreviewAndFullWidths()
        {
            Assert.Equal(64, AssetsManager.Services.Viewer.Loading.MapTextureLoadingService.PreviewTextureSize);
            Assert.Equal(1024, AssetsManager.Services.Viewer.Loading.MapTextureLoadingService.FullTextureSize);
        }

        [Fact]
        public void InstallationRootsHonorPreferredClientAndRemoveDuplicates()
        {
            string pbe = NewTempDirectory();
            string live = NewTempDirectory();
            try
            {
                var settings = new AppSettings
                {
                    PreferredClient = PreferredClient.LIVE,
                    LolPbeDirectory = pbe,
                    LolLiveDirectory = live
                };

                string[] roots = MapAssetResolver.GetInstallationRoots(settings).ToArray();

                Assert.Equal(Path.GetFullPath(live), roots[0]);
                Assert.Equal(Path.GetFullPath(pbe), roots[1]);

                settings.LolPbeDirectory = live;
                roots = MapAssetResolver.GetInstallationRoots(settings).ToArray();
                Assert.Single(roots);
            }
            finally
            {
                Directory.Delete(pbe, recursive: true);
                Directory.Delete(live, recursive: true);
            }
        }

        private static string NewTempDirectory()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "AssetsManagerMapTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
