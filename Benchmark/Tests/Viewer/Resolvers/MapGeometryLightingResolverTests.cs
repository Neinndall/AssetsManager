using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.BenchmarkTests.Tests.Viewer.Resolvers
{
    public class MapGeometryLightingResolverTests
    {
        [Fact]
        public void Resolve_MapContainerSunPropertiesCreatesProfile()
        {
            var sun = new BinTreeStruct(
                0,
                Fnv1a.HashLower("MapSunProperties"),
                new BinTreeProperty[]
                {
                    new BinTreeVector3(Fnv1a.HashLower("sunDirection"), new Vector3(0.1f, 0.2f, 0.3f)),
                    new BinTreeVector4(Fnv1a.HashLower("sunColor"), new Vector4(0.8f, 0.6f, 0.4f, 1f)),
                    new BinTreeF32(Fnv1a.HashLower("SunIntensityScale"), 0.5f),
                    new BinTreeVector4(Fnv1a.HashLower("skyLightColor"), new Vector4(0.5f, 0.25f, 1f, 1f)),
                    new BinTreeF32(Fnv1a.HashLower("skyLightScale"), 0.4f),
                    new BinTreeF32(Fnv1a.HashLower("lightMapColorScale"), 0.75f)
                });
            var components = new BinTreeContainer(
                Fnv1a.HashLower("components"),
                BinPropertyType.Struct,
                new BinTreeProperty[] { sun });
            var mapContainer = new BinTreeObject(
                "Maps/Jade/MapContainer",
                "MapContainer",
                new BinTreeProperty[] { components });

            MapLightingProfile profile = MapGeometryLightingResolver.Resolve(
                new BinTree(new[] { mapContainer }, Array.Empty<string>()));

            Assert.NotNull(profile);
            Assert.Equal(Vector3.Normalize(new Vector3(0.1f, 0.2f, -0.3f)), profile.SunDirection);
            Assert.Equal(new Vector3(0.4f, 0.3f, 0.2f), profile.SunColor);
            Assert.Equal(new Vector3(0.2f, 0.1f, 0.4f), profile.AmbientColor);
            Assert.Equal(0.75f, profile.LightMapColorScale);
        }

        [Fact]
        public void Resolve_WithoutMapSunPropertiesReturnsNull()
        {
            Assert.Null(MapGeometryLightingResolver.Resolve(new BinTree()));
            Assert.Null(MapGeometryLightingResolver.Resolve(null));
        }

        [Fact]
        public void Resolve_StandaloneSunUsesSafeDefaults()
        {
            var sun = new BinTreeObject(
                "Maps/Jade/Sun",
                "MapSunProperties",
                Array.Empty<BinTreeProperty>());

            MapLightingProfile profile = MapGeometryLightingResolver.Resolve(
                new BinTree(new[] { sun }, Array.Empty<string>()));

            Assert.NotNull(profile);
            Assert.Equal(Vector3.Normalize(new Vector3(0f, 0.707f, -0.707f)), profile.SunDirection);
            Assert.Equal(Vector3.One, profile.SunColor);
            Assert.Equal(new Vector3(0.705f, 0.88f, 1f) * 0.2f, profile.AmbientColor);
            Assert.Equal(1f, profile.LightMapColorScale);
        }
    }
}
