using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Environment;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapGeometryDataTests
    {
        [Theory]
        [InlineData(0x00, false)]
        [InlineData(0x01, true)]
        [InlineData(0x03, true)]
        [InlineData(0x02, false)]
        [InlineData(0xFF, true)]
        public void DefaultLayerUsesAuthoredLayerZero(byte visibility, bool expected)
        {
            var mesh = new MapGeometryMeshData(
                Vector3.Zero,
                Vector3.One,
                visibility,
                (byte)EnvironmentQuality.AllQualities,
                MapGeometryMeshFlags.None,
                0,
                0,
                EnvironmentAssetMeshRenderFlags.Default,
                0,
                0);

            Assert.Equal(expected, mesh.IsVisibleOnLayer(MapGeometryData.DefaultLayer));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(8)]
        public void InvalidLayerIsNeverVisible(int layer)
        {
            var mesh = new MapGeometryMeshData(
                Vector3.Zero,
                Vector3.One,
                0xFF,
                (byte)EnvironmentQuality.AllQualities,
                MapGeometryMeshFlags.None,
                0,
                0,
                EnvironmentAssetMeshRenderFlags.Default,
                0,
                0);

            Assert.False(mesh.IsVisibleOnLayer(layer));
        }

        [Fact]
        public void LightmapsAreCollectedOnceAcrossBakedAndStationaryChannels()
        {
            var baked = new MapGeometryLightChannelData("maps/test/baked.tex", new Vector2(2f), new Vector2(0.25f));
            var stationary = new MapGeometryLightChannelData("maps/test/stationary.tex", Vector2.One, Vector2.Zero);
            var meshA = new MapGeometryMeshData(
                Vector3.Zero, Vector3.One, 0xFF, 0, MapGeometryMeshFlags.None, 0, 0,
                EnvironmentAssetMeshRenderFlags.Default, 0, 0, baked, stationary);
            var meshB = meshA with { StationaryLight = baked };
            var geometry = new MapGeometryData(
                new[] { Vector3.Zero },
                new[] { Vector3.UnitY },
                new[] { Vector2.Zero },
                new[] { Vector2.Zero },
                new uint[] { 0 },
                new[] { meshA, meshB },
                System.Array.Empty<MapGeometrySubmeshData>(),
                System.Array.Empty<string>());

            Assert.Equal(new[] { "maps/test/baked.tex", "maps/test/stationary.tex" }, geometry.Lightmaps);
        }
    }
}
