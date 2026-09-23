using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Hashing;
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
        public void MaterialsFileDerivesTheSameMapPathFromDataTree()
        {
            string root = NewTempDirectory();
            try
            {
                string file = Path.Combine(root, "data", "maps", "mapgeometry", "map11", "base_srx.materials.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, Array.Empty<byte>());

                Assert.True(MapPath.TryFromMapFile(file, out MapPath map));
                Assert.Equal("Maps/MapGeometry/Map11/Base_SRX", map.Value, ignoreCase: true);
                Assert.False(MapPath.TryFromGeometryFile(file, out _));
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
        public async Task ResolverKeepsSelectedMaterialsAndSiblingGeometryAuthoritative()
        {
            string root = NewTempDirectory();
            try
            {
                string directory = Path.Combine(root, "data", "maps", "mapgeometry", "map11");
                Directory.CreateDirectory(directory);
                string geometry = Path.Combine(directory, "base_srx.mapgeo");
                string materials = Path.Combine(directory, "base_srx.materials.bin");
                File.WriteAllBytes(geometry, new byte[] { 4 });
                File.WriteAllBytes(materials, new byte[] { 5 });

                MapSceneSource source = MapSceneSource.FromMapFile(materials, root);
                var resolver = new MapAssetResolver(null, null);
                MapSceneAssets assets = await resolver.ResolveSceneAssetsAsync(source);

                Assert.Equal(MapAssetOrigin.ProjectFile, assets.Geometry.Origin);
                Assert.Equal(Path.GetFullPath(geometry), assets.Geometry.PhysicalPath);
                Assert.Equal(MapAssetOrigin.SelectedFile, assets.Materials.Origin);
                Assert.Equal(Path.GetFullPath(materials), assets.Materials.PhysicalPath);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ResolverStillReturnsSelectedMaterialsWhenGeometryIsMissing()
        {
            string root = NewTempDirectory();
            try
            {
                string materials = Path.Combine(root, "data", "maps", "mapgeometry", "map11", "base_srx.materials.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(materials)!);
                File.WriteAllBytes(materials, new byte[] { 6 });

                MapSceneSource source = MapSceneSource.FromMapFile(materials, root);
                var resolver = new MapAssetResolver(null, null);
                MapSceneAssets assets = await resolver.ResolveSceneAssetsAsync(source);

                Assert.Null(assets.Geometry);
                Assert.Equal(MapAssetOrigin.SelectedFile, assets.Materials.Origin);
                Assert.Equal(Path.GetFullPath(materials), assets.Materials.PhysicalPath);
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

        [Fact]
        public async Task ResolverFindsFlatHashNamedProjectFileForKnownVirtualPath()
        {
            string root = NewTempDirectory();
            try
            {
                const string virtualPath = "data/characters/test/test.bin";
                ulong hash = XxHash64Ext.Hash(virtualPath);
                string extracted = Path.Combine(root, $"{hash:x16}.bin");
                File.WriteAllBytes(extracted, new byte[] { 7, 8, 9 });

                var resolver = new MapAssetResolver(null, null);
                MapResolvedAsset asset = await resolver.ResolveReferenceAsync(
                    new MapAssetReference(virtualPath, 0),
                    root);

                Assert.NotNull(asset);
                Assert.Equal(MapAssetOrigin.ProjectFile, asset.Origin);
                Assert.Equal(Path.GetFullPath(extracted), asset.PhysicalPath);
                Assert.Equal(virtualPath, asset.VirtualPath);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ResolverFindsNestedProjectFileFromHashOnlyReference()
        {
            string root = NewTempDirectory();
            try
            {
                const string virtualPath = "assets/characters/test/skins/base/animations/idle.anm";
                string extracted = Path.Combine(
                    root,
                    "assets",
                    "characters",
                    "test",
                    "skins",
                    "base",
                    "animations",
                    "idle.anm");
                Directory.CreateDirectory(Path.GetDirectoryName(extracted)!);
                File.WriteAllBytes(extracted, new byte[] { 1, 2, 3, 4 });
                ulong hash = XxHash64Ext.Hash(virtualPath);

                var resolver = new MapAssetResolver(null, null);
                MapResolvedAsset asset = await resolver.ResolveReferenceAsync(
                    new MapAssetReference(null, hash),
                    root);

                Assert.NotNull(asset);
                Assert.Equal(MapAssetOrigin.ProjectFile, asset.Origin);
                Assert.Equal(Path.GetFullPath(extracted), asset.PhysicalPath);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void ResolverFindsEveryCollisionSiblingForTruncatedLinkedProjectBin()
        {
            string root = NewTempDirectory();
            try
            {
                string directory = Path.Combine(root, "data", "characters", "turret");
                Directory.CreateDirectory(directory);
                string extractedStem = new string('t', 236);
                string first = Path.Combine(directory, extractedStem + ".bin");
                string second = Path.Combine(directory, extractedStem + " (1).bin");
                File.WriteAllBytes(first, new byte[] { 1 });
                File.WriteAllBytes(second, new byte[] { 2 });

                string authored = $"DATA/Characters/Turret/{extractedStem}_irreversibly_truncated.bin";
                var resolver = new MapAssetResolver(null, null);
                var resolved = resolver.ResolveLinkedProjectBins(authored, root);

                Assert.Equal(2, resolved.Count);
                Assert.Contains(resolved, asset => string.Equals(asset.PhysicalPath, Path.GetFullPath(first), StringComparison.OrdinalIgnoreCase));
                Assert.Contains(resolved, asset => string.Equals(asset.PhysicalPath, Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase));
                Assert.All(resolved, asset => Assert.Equal(MapAssetOrigin.ProjectFile, asset.Origin));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ResolverFindsFlatHashNamedProjectFileForHashOnlyReference()
        {
            string root = NewTempDirectory();
            try
            {
                const ulong hash = 0x1234567890abcdef;
                string extracted = Path.Combine(root, $"{hash:x16}.tex");
                File.WriteAllBytes(extracted, new byte[] { 0x54, 0x45, 0x58, 0x00 });

                var reference = new MapTextureReference(null, hash);
                var resolver = new MapAssetResolver(null, null);
                var resolved = await resolver.ResolveTexturesAsync(new[] { reference }, root);

                Assert.True(resolved.TryGetValue(reference, out MapResolvedAsset asset));
                Assert.Equal(MapAssetOrigin.ProjectFile, asset.Origin);
                Assert.Equal(Path.GetFullPath(extracted), asset.PhysicalPath);
                Assert.Equal(Path.GetFileName(extracted), asset.VirtualPath);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task BinClosureDerivesDataRootAndFollowsLinkedProjectBinsWithinBudget()
        {
            string root = NewTempDirectory();
            try
            {
                string primaryPath = Path.Combine(
                    root,
                    "data",
                    "characters",
                    "hero",
                    "skins",
                    "skin0.bin");
                string linkedPath = Path.Combine(
                    root,
                    "data",
                    "characters",
                    "hero",
                    "materials.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(primaryPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(linkedPath)!);

                using (var stream = File.Create(primaryPath))
                    new BinTree(Array.Empty<BinTreeObject>(), new[] { "data/characters/hero/materials.bin" }).Write(stream);
                using (var stream = File.Create(linkedPath))
                    new BinTree(Array.Empty<BinTreeObject>(), Array.Empty<string>()).Write(stream);

                var resolver = new MapAssetResolver(null, null);
                var loader = new BinDocumentClosureLoader(resolver);
                MapResolvedAsset primary = MapResolvedAsset.FromPhysical(
                    "data/characters/hero/skins/skin0.bin",
                    primaryPath,
                    MapAssetOrigin.SelectedFile);

                Assert.Equal(Path.GetFullPath(root), BinDocumentClosureLoader.ResolveProjectRoot(primary, null));
                Assert.Equal(
                    2,
                    (await loader.LoadAsync(primary, null, 32, CancellationToken.None)).Count);
                Assert.Single(await loader.LoadAsync(primary, null, 0, CancellationToken.None));
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
