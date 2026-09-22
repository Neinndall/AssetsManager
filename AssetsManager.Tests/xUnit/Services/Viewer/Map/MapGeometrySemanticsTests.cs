using System.Numerics;
using AssetsManager.Services.Viewer.Map.Semantics;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapGeometrySemanticsTests
    {
        [Fact]
        public void OriginUsesMedianOfDefaultLayerGeometry()
        {
            var geometry = new MapGeometryData(
                new[]
                {
                    new Vector3(-100f, 0f, 0f),
                    new Vector3(0f, 10f, 0f),
                    new Vector3(100f, 20f, 0f),
                    new Vector3(50_000f, 900f, 50_000f)
                },
                new[] { Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY },
                new[] { Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero },
                null,
                new uint[] { 0, 1, 2, 3 },
                new[]
                {
                    Mesh(visibility: 1, firstSubmesh: 0, submeshCount: 3),
                    Mesh(visibility: 2, firstSubmesh: 3, submeshCount: 1)
                },
                new[]
                {
                    new MapGeometrySubmeshData(0, 1, 0),
                    new MapGeometrySubmeshData(1, 1, 0),
                    new MapGeometrySubmeshData(2, 1, 0),
                    new MapGeometrySubmeshData(3, 1, 0)
                },
                new[] { "Maps/Test/Material" });

            Vector3? origin = MapGeometrySemantics.CalculateOrigin(geometry);

            Assert.Equal(new Vector3(0f, 10f, 0f), origin);
        }

        [Fact]
        public void OriginUsesNearbyGroundMedianForHeight()
        {
            var geometry = new MapGeometryData(
                new[]
                {
                    new Vector3(-100f, 0f, 0f),
                    new Vector3(0f, 10f, 0f),
                    new Vector3(100f, 20f, 0f),
                    new Vector3(0f, 5_000f, 2_000f)
                },
                new[] { Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY },
                new[] { Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero },
                null,
                new uint[] { 0, 1, 2, 3 },
                new[] { Mesh(visibility: 1, firstSubmesh: 0, submeshCount: 4) },
                new[]
                {
                    new MapGeometrySubmeshData(0, 1, 0),
                    new MapGeometrySubmeshData(1, 1, 0),
                    new MapGeometrySubmeshData(2, 1, 0),
                    new MapGeometrySubmeshData(3, 1, 0)
                },
                new[] { "Maps/Test/Material" });

            Vector3? origin = MapGeometrySemantics.CalculateOrigin(geometry);

            Assert.NotNull(origin);
            Assert.Equal(10f, origin.Value.Y);
        }

        private static MapGeometryMeshData Mesh(
            byte visibility,
            int firstSubmesh,
            int submeshCount) =>
            new(
                Vector3.Zero,
                Vector3.One,
                visibility,
                0,
                MapGeometryMeshFlags.None,
                firstSubmesh,
                submeshCount,
                default,
                0,
                0);
    }
}
