using System;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapSsaoParserTests
    {
        private const string Crepe = "Maps/MapGeometry/Map12/Crepe";

        [Fact]
        public void ComponentAnswersAuthoredSettingsAndClassDefaults()
        {
            var settings = new BinTreeStruct(
                MapSsaoParser.SettingsField,
                Fnv1a.HashLower("MapSSAOSettings"),
                new BinTreeProperty[]
                {
                    new BinTreeU32(MapSsaoParser.SampleQualityField, 1u),
                    new BinTreeF32(MapSsaoParser.SampleRadiusField, 75f),
                    new BinTreeF32(MapSsaoParser.PowerField, 20f),
                    new BinTreeBool(MapSsaoParser.EdgeAwareBlurField, false)
                });
            var renderer = new BinTreeStruct(
                MapSsaoParser.RendererField,
                Fnv1a.HashLower("MapSSAORenderer"),
                new BinTreeProperty[] { settings });
            BinTree tree = Tree(Container(
                Crepe,
                new BinTreeStruct(
                    0,
                    MapSsaoParser.MapSsaoClass,
                    new BinTreeProperty[] { renderer })));

            MapSsaoData parsed = MapSsaoParser.Parse(tree, MapPath.FromEntryPath(Crepe));

            Assert.NotNull(parsed);
            Assert.Equal(1u, parsed.SampleQuality);
            Assert.Equal(8, parsed.SampleCount);
            Assert.Equal(75f, parsed.SampleRadius);
            Assert.Equal(3f, parsed.Bias);
            Assert.Equal(20f, parsed.Power);
            Assert.Equal(1f, parsed.Intensity);
            Assert.Equal(0.5f, parsed.BufferScale);
            Assert.False(parsed.EdgeAwareBlur);
        }

        [Fact]
        public void ComponentWithoutSettingsAnswersClassDefaults()
        {
            BinTree tree = Tree(Container(
                Crepe,
                new BinTreeStruct(0, MapSsaoParser.MapSsaoClass, Array.Empty<BinTreeProperty>())));

            Assert.Equal(
                MapSsaoParser.Defaults,
                MapSsaoParser.Parse(tree, MapPath.FromEntryPath(Crepe)));
        }

        [Fact]
        public void ContainerWithoutSsaoAnswersNull()
        {
            BinTree tree = Tree(Container(
                Crepe,
                new BinTreeStruct(0, MapSunParser.SunPropertiesClass, Array.Empty<BinTreeProperty>())));

            Assert.Null(MapSsaoParser.Parse(tree, MapPath.FromEntryPath(Crepe)));
        }

        private static BinTreeObject Container(string path, params BinTreeStruct[] components) =>
            new(
                Fnv1a.HashLower(path),
                MapPostEffectsParser.MapContainerClass,
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        MapPostEffectsParser.ComponentsField,
                        BinPropertyType.Struct,
                        components)
                });

        private static BinTree Tree(params BinTreeObject[] objects) =>
            new(objects, Array.Empty<string>());
    }
}
