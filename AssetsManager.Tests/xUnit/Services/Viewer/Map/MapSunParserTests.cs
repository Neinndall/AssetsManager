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
    public sealed class MapSunParserTests
    {
        private const string BaseSrx = "Maps/MapGeometry/Map11/Base_SRX";

        [Fact]
        public void ContainerAnswersTheSunComponentAndUsesClassDefaultsForMissingFields()
        {
            BinTreeStruct sun = new(
                0,
                MapSunParser.SunPropertiesClass,
                new BinTreeProperty[]
                {
                    new BinTreeVector3(MapSunParser.SunDirectionField, new Vector3(-0.25f, 0.75f, -0.05f)),
                    new BinTreeVector4(MapSunParser.SunColorField, new Vector4(0.5f, 0.4f, 0.3f, 1f)),
                    new BinTreeF32(MapSunParser.SunIntensityField, 0.9f),
                    new BinTreeVector4(MapSunParser.HorizonColorField, new Vector4(0.6f, 0.5f, 0.4f, 1f)),
                    new BinTreeF32(MapSunParser.SkyScaleField, 1.5f),
                    new BinTreeF32(MapSunParser.LightMapColorScaleField, 1.25f),
                    new BinTreeBool(MapSunParser.FogEnabledField, false),
                    new BinTreeVector4(MapSunParser.FogColorField, new Vector4(0.2f, 0.3f, 0.4f, 1f)),
                    new BinTreeVector4(MapSunParser.FogAlternateColorField, new Vector4(0.1f, 0.2f, 0.3f, 1f)),
                    new BinTreeVector2(MapSunParser.FogStartEndField, new Vector2(100f, -900f)),
                    new BinTreeF32(MapSunParser.FogEmissiveRemapField, 2.1f)
                });
            BinTree tree = Tree(Container(BaseSrx, sun));

            MapSunData parsed = MapSunParser.Parse(tree, MapPath.FromEntryPath(BaseSrx));

            Assert.NotNull(parsed);
            Assert.Equal(new Vector3(-0.25f, 0.75f, -0.05f), parsed.Direction);
            Assert.Equal(new Vector4(0.5f, 0.4f, 0.3f, 1f), parsed.Color);
            Assert.Equal(0.9f, parsed.Intensity);
            Assert.Equal(1.5f, parsed.SkyScale);
            Assert.Equal(new Vector4(0.1f, 0.1f, 0.1f, 1f), parsed.GroundColor);
            Assert.Equal(new Vector4(0.6f, 0.5f, 0.4f, 1f), parsed.HorizonColor);
            Assert.Equal(1.25f, parsed.LightMapColorScale);
            Assert.False(parsed.FogEnabled);
            Assert.Equal(new Vector4(0.2f, 0.3f, 0.4f, 1f), parsed.FogColor);
            Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 1f), parsed.FogAlternateColor);
            Assert.Equal(new Vector2(100f, -900f), parsed.FogStartEnd);
            Assert.Equal(2.1f, parsed.FogEmissiveRemap);
        }

        [Fact]
        public void MissingExactContainerFallsBackToFirstMapContainerLikeLtk()
        {
            const string elsewhere = "Maps/MapGeometry/Map11/Elsewhere";
            BinTree tree = Tree(Container(
                elsewhere,
                new BinTreeStruct(
                    0,
                    MapSunParser.SunPropertiesClass,
                    new BinTreeProperty[] { new BinTreeF32(MapSunParser.SunIntensityField, 0.5f) })));

            MapSunData parsed = MapSunParser.Parse(tree, MapPath.FromEntryPath(BaseSrx));

            Assert.NotNull(parsed);
            Assert.Equal(0.5f, parsed.Intensity);
        }

        [Fact]
        public void ContainerWithoutSunAnswersNull()
        {
            BinTree tree = Tree(Container(
                BaseSrx,
                new BinTreeStruct(0, Fnv1a.HashLower("MapNavGrid"), Array.Empty<BinTreeProperty>())));

            Assert.Null(MapSunParser.Parse(tree, MapPath.FromEntryPath(BaseSrx)));
        }

        private static BinTreeObject Container(string path, params BinTreeStruct[] components) =>
            new(
                Fnv1a.HashLower(path),
                MapSunParser.MapContainerClass,
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        MapSunParser.ComponentsField,
                        BinPropertyType.Struct,
                        components)
                });

        private static BinTree Tree(params BinTreeObject[] objects) =>
            new(objects, Array.Empty<string>());
    }
}
