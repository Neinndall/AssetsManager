using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapPostEffectsParserTests
    {
        private const string BaseSrx = "Maps/MapGeometry/Map11/Base_SRX";

        [Fact]
        public void ComponentAnswersAuthoredOptionsAndClassDefaults()
        {
            var options = new BinTreeStruct(
                MapPostEffectsParser.OptionsField,
                Fnv1a.HashLower("PostEffectOptions"),
                new BinTreeProperty[]
                {
                    new BinTreeBool(MapPostEffectsParser.DepthFogEnabled, true),
                    new BinTreeVector4(MapPostEffectsParser.DepthFogColor, new Vector4(0.5f, 0.6f, 0.7f, 1f)),
                    new BinTreeF32(MapPostEffectsParser.HeightFogStart, 120f),
                    new BinTreeBool(MapPostEffectsParser.DofEnabled, true),
                    new BinTreeF32(MapPostEffectsParser.Coc, 4f)
                });
            BinTree tree = Tree(Container(
                BaseSrx,
                new BinTreeStruct(
                    0,
                    MapPostEffectsParser.PostEffectsClass,
                    new BinTreeProperty[] { options })));

            MapPostEffectsData parsed = MapPostEffectsParser.Parse(tree, MapPath.FromEntryPath(BaseSrx));

            Assert.NotNull(parsed);
            Assert.True(parsed.DepthFog.Enabled);
            Assert.Equal(new Vector4(0.5f, 0.6f, 0.7f, 1f), parsed.DepthFog.Color);
            Assert.Equal(120f, parsed.HeightFog.Start);
            Assert.False(parsed.HeightFog.Enabled);
            Assert.True(parsed.DepthOfField.Enabled);
            Assert.Equal(4f, parsed.DepthOfField.Coc);
            Assert.Equal(2000f, parsed.DepthOfField.FocalDistance);
        }

        [Fact]
        public void ComponentWithoutOptionsAnswersClassDefaults()
        {
            BinTree tree = Tree(Container(
                BaseSrx,
                new BinTreeStruct(0, MapPostEffectsParser.PostEffectsClass, Array.Empty<BinTreeProperty>())));

            Assert.Equal(
                MapPostEffectsParser.Defaults,
                MapPostEffectsParser.Parse(tree, MapPath.FromEntryPath(BaseSrx)));
        }

        [Fact]
        public void ContainerWithoutPostEffectsAnswersNull()
        {
            BinTree tree = Tree(Container(
                BaseSrx,
                new BinTreeStruct(0, MapSunParser.SunPropertiesClass, Array.Empty<BinTreeProperty>())));

            Assert.Null(MapPostEffectsParser.Parse(tree, MapPath.FromEntryPath(BaseSrx)));
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
