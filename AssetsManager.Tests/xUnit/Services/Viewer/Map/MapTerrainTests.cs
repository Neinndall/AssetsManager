using System;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapTerrainTests
    {
        private const string Container = "Maps/MapGeometry/Map11/Base_SRX";

        [Fact]
        public void DefaultSkinSuppliesGrassTintAndPerStateAlternates()
        {
            BinTree map = MapDocument(
                Skin("AprilFools2019", Container, 0x10, alternates: Array.Empty<BinTreeProperty>()),
                Skin("Default", Container, 0x20, Alternate(0x21, "Fire"), Alternate(0x22, "Ocean"), Alternate(0x23, "Unknown")));

            MapTerrainData terrain = MapTerrainParser.Parse(Materials(new Vector2(14820f, 14880f)), map, Path());

            Assert.Equal(0x20ul, terrain.GrassTint.PathHash);
            Assert.Equal(new Vector2(14820f, 14880f), terrain.BoundsMax);
            // "Unknown" names no visibility flag and is dropped; Fire is bit 1 and Ocean bit 3.
            Assert.Equal(new[] { (0x02, 0x21ul), (0x08, 0x22ul) },
                terrain.GrassTintAlternates.Select(alternate => (alternate.Flag, alternate.Texture.PathHash)));
            Assert.Equal(0x22ul, terrain.AlternateFor(0x08).Texture.PathHash);
            Assert.Null(terrain.AlternateFor(0x01));
        }

        [Fact]
        public void SkinsOfOtherContainersAreIgnored()
        {
            BinTree map = MapDocument(Skin("Default", "Maps/MapGeometry/Map11/Arcade", 0x30));

            Assert.Null(MapTerrainParser.Parse(Materials(Vector2.One), map, Path()));
        }

        [Fact]
        public void TerrainTransformMapsBoundsToUnitSquare()
        {
            var terrain = new MapTerrainData(
                new Vector2(100f, 200f),
                new Vector2(1100f, 2200f),
                new MapTextureReference(null, 1),
                Array.Empty<MapGrassTintAlternate>());

            Vector4 transform = terrain.TerrainTransform;

            Assert.Equal(0f, 100f * transform.X + transform.Z, 5);
            Assert.Equal(1f, 1100f * transform.X + transform.Z, 5);
            Assert.Equal(0f, 200f * transform.Y + transform.W, 5);
            Assert.Equal(1f, 2200f * transform.Y + transform.W, 5);
            Assert.Equal(Vector4.Zero, (terrain with { BoundsMax = terrain.BoundsMin }).TerrainTransform);
        }

        [Fact]
        public void GrassTintSamplersBindTheSceneTintsWithAlternateFallback()
        {
            var both = new GameShaderRuntime.TerrainFrame(Vector4.One, 7, 9, 1f);
            var baseOnly = both with { GrassTintAlternate = 0 };

            Assert.Equal(7u, GameShaderRuntime.GrassTintFor(GameShaderRuntime.GrassTintTexture, both));
            Assert.Equal(9u, GameShaderRuntime.GrassTintFor(GameShaderRuntime.GrassTintAlternateTexture, both));
            Assert.Equal(7u, GameShaderRuntime.GrassTintFor(GameShaderRuntime.GrassTintAlternateTexture, baseOnly));
            Assert.Equal(0u, GameShaderRuntime.GrassTintFor("SAMPLER_BACK_BUFFER_COPY_SharedTexture", both));
        }

        private static MapPath Path() =>
            MapPath.TryFromEntryPath(Container, out MapPath path) ? path : throw new InvalidOperationException();

        private static BinTree Materials(Vector2 boundsMax) =>
            new(new[]
                {
                    new BinTreeObject(
                        Fnv1a.HashLower(Container),
                        MapVariantParser.MapContainerClass,
                        new BinTreeProperty[]
                        {
                            new BinTreeString(MapVariantParser.MapPathField, Container),
                            new BinTreeVector2(Fnv1a.HashLower("boundsMax"), boundsMax)
                        })
                },
                Array.Empty<string>());

        private static BinTree MapDocument(params BinTreeObject[] skins)
        {
            string[] flags = { "Base", "Fire", "Earth", "Ocean" };
            var owner = new BinTreeObject(
                Fnv1a.HashLower("Maps/Shipping/Map11"),
                MapVariantParser.MapClass,
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        MapVariantParser.MapSkinsField,
                        BinPropertyType.ObjectLink,
                        skins.Select(skin => (BinTreeProperty)new BinTreeObjectLink(0, skin.PathHash)).ToArray()),
                    new BinTreeStruct(
                        Fnv1a.HashLower("VisibilityFlagDefines"),
                        Fnv1a.HashLower("MapVisibilityFlagDefinitions"),
                        new BinTreeProperty[]
                        {
                            new BinTreeContainer(
                                Fnv1a.HashLower("FlagDefinitions"),
                                BinPropertyType.Struct,
                                flags.Select((name, bit) => (BinTreeProperty)new BinTreeStruct(
                                    0,
                                    Fnv1a.HashLower("MapVisibilityFlagDefinition"),
                                    new BinTreeProperty[]
                                    {
                                        new BinTreeHash(Fnv1a.HashLower("name"), Fnv1a.HashLower(name)),
                                        new BinTreeU8(Fnv1a.HashLower("BitIndex"), (byte)bit)
                                    })).ToArray())
                        })
                });
            return new BinTree(skins.Prepend(owner).ToArray(), Array.Empty<string>());
        }

        private static BinTreeObject Skin(string name, string container, ulong grassTint, params BinTreeProperty[] alternates) =>
            new(
                Fnv1a.HashLower("MapSkin/" + name),
                MapVariantParser.MapSkinClass,
                new BinTreeProperty[]
                {
                    new BinTreeString(MapVariantParser.SkinNameField, name),
                    new BinTreeString(MapVariantParser.ContainerLinkField, container),
                    new BinTreeWadChunkLink(Fnv1a.HashLower("mGrassTintTexture"), grassTint),
                    new BinTreeStruct(
                        Fnv1a.HashLower("mAlternateAssets"),
                        Fnv1a.HashLower("MapAlternateAssets"),
                        new BinTreeProperty[]
                        {
                            new BinTreeContainer(Fnv1a.HashLower("mAlternateAssets"), BinPropertyType.Struct, alternates)
                        })
                });

        private static BinTreeProperty Alternate(ulong grassTint, string flag) =>
            new BinTreeStruct(
                0,
                Fnv1a.HashLower("MapAlternateAsset"),
                new BinTreeProperty[]
                {
                    new BinTreeWadChunkLink(Fnv1a.HashLower("mGrassTintTextureName"), grassTint),
                    new BinTreeHash(Fnv1a.HashLower("mVisibilityFlagName"), Fnv1a.HashLower(flag))
                });
    }
}
