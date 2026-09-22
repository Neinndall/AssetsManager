using System;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapVariantParserTests
    {
        private const string MapEntry = "Maps/Shipping/Map11";
        private const string DefaultSkin = "Maps/Shipping/Map11/MapSkins/Default";
        private const string OdysseySkin = "Maps/Shipping/Map11/MapSkins/Odyssey";
        private const string BaseSrx = "Maps/MapGeometry/Map11/Base_SRX";

        [Fact]
        public void ContainerAnswersTheMapItStates()
        {
            BinTree tree = Tree(new BinTreeObject(
                Fnv1a.HashLower(BaseSrx),
                MapVariantParser.MapContainerClass,
                new BinTreeProperty[]
                {
                    new BinTreeString(MapVariantParser.MapPathField, BaseSrx)
                }));

            MapVariantData variant = Assert.Single(new MapVariantParser().Parse(tree, Fnv1a.HashLower(BaseSrx)));

            Assert.Null(variant.Skin);
            Assert.Equal(BaseSrx, variant.Map.Value);
            Assert.Equal("Base_SRX", variant.Label);
        }

        [Fact]
        public void SkinAnswersItsContainerUnderItsOwnName()
        {
            BinTree tree = Tree(Skin(DefaultSkin, "Default", BaseSrx));

            MapVariantData variant = Assert.Single(new MapVariantParser().Parse(tree, Fnv1a.HashLower(DefaultSkin)));

            Assert.Equal("Default", variant.Skin);
            Assert.Equal(BaseSrx, variant.Map.Value);
            Assert.Equal("Default", variant.Label);
        }

        [Fact]
        public void SkinWithoutContainerDrawsNothing()
        {
            BinTree tree = Tree(
                Skin(OdysseySkin, "Odyssey", null),
                Skin(DefaultSkin, "Default", string.Empty));
            var parser = new MapVariantParser();

            Assert.Empty(parser.Parse(tree, Fnv1a.HashLower(OdysseySkin)));
            Assert.Empty(parser.Parse(tree, Fnv1a.HashLower(DefaultSkin)));
        }

        [Fact]
        public void MapAnswersDrawableSkinsInAuthoredOrder()
        {
            BinTree tree = Tree(
                MapListing(OdysseySkin, DefaultSkin, "Maps/Shipping/Map11/MapSkins/Elsewhere"),
                Skin(DefaultSkin, "Default", BaseSrx),
                Skin(OdysseySkin, "Odyssey", null));

            var variants = new MapVariantParser().Parse(tree, Fnv1a.HashLower(MapEntry));

            MapVariantData variant = Assert.Single(variants);
            Assert.Equal("Default", variant.Skin);
            Assert.Equal(BaseSrx, variant.Map.Value);
        }

        [Fact]
        public void OpeningVariantPrefersDefaultCaseInsensitivelyThenFirst()
        {
            var variants = new[]
            {
                new MapVariantData("Odyssey", MapPath.FromEntryPath("Maps/MapGeometry/Map11/Odyssey")),
                new MapVariantData("dEfAuLt", MapPath.FromEntryPath(BaseSrx))
            };

            Assert.Same(variants[1], MapVariantData.Opening(variants));
            Assert.Same(variants[0], MapVariantData.Opening(new[] { variants[0] }));
            Assert.Null(MapVariantData.Opening(Array.Empty<MapVariantData>()));
        }

        [Fact]
        public void OtherOrMissingObjectDrawsNothing()
        {
            const string material = "Some/Material";
            BinTree tree = Tree(new BinTreeObject(
                Fnv1a.HashLower(material),
                Fnv1a.HashLower("StaticMaterialDef"),
                Array.Empty<BinTreeProperty>()));
            var parser = new MapVariantParser();

            Assert.Empty(parser.Parse(tree, Fnv1a.HashLower(material)));
            Assert.Empty(parser.Parse(tree, Fnv1a.HashLower("Not/Declared")));
        }

        private static BinTreeObject Skin(string entry, string name, string container)
        {
            var properties = new System.Collections.Generic.List<BinTreeProperty>
            {
                new BinTreeString(MapVariantParser.SkinNameField, name)
            };
            if (container != null)
                properties.Add(new BinTreeString(MapVariantParser.ContainerLinkField, container));
            return new BinTreeObject(Fnv1a.HashLower(entry), MapVariantParser.MapSkinClass, properties);
        }

        private static BinTreeObject MapListing(params string[] skins)
        {
            var links = new BinTreeProperty[skins.Length];
            for (int i = 0; i < skins.Length; i++)
                links[i] = new BinTreeObjectLink(0, Fnv1a.HashLower(skins[i]));

            return new BinTreeObject(
                Fnv1a.HashLower(MapEntry),
                MapVariantParser.MapClass,
                new BinTreeProperty[]
                {
                    new BinTreeUnorderedContainer(
                        MapVariantParser.MapSkinsField,
                        BinPropertyType.ObjectLink,
                        links)
                });
        }

        private static BinTree Tree(params BinTreeObject[] objects) =>
            new(objects, Array.Empty<string>());
    }
}
