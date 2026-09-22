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
    }
}
