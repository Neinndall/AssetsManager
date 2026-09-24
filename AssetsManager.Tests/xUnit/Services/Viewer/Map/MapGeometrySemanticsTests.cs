using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Semantics;
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

        [Fact]
        public void OriginFollowsTheCurrentlyDrawnVisibilityFlags()
        {
            var indices = new uint[54];
            indices[0] = 0;
            indices[9] = 1;
            indices[18] = 2;
            indices[27] = 3;
            indices[36] = 4;
            indices[45] = 5;
            var geometry = new MapGeometryData(
                new[]
                {
                    new Vector3(-100f, 0f, 0f),
                    new Vector3(0f, 0f, 0f),
                    new Vector3(100f, 0f, 0f),
                    new Vector3(900f, 50f, 1000f),
                    new Vector3(1000f, 50f, 1000f),
                    new Vector3(1100f, 50f, 1000f)
                },
                Enumerable.Repeat(Vector3.UnitY, 6).ToArray(),
                Enumerable.Repeat(Vector2.Zero, 6).ToArray(),
                null,
                indices,
                new[]
                {
                    Mesh(visibility: 1, firstSubmesh: 0, submeshCount: 1),
                    Mesh(visibility: 2, firstSubmesh: 1, submeshCount: 1)
                },
                new[]
                {
                    new MapGeometrySubmeshData(0, 27, 0),
                    new MapGeometrySubmeshData(27, 27, 0)
                },
                new[] { "Maps/Test/Material" });

            Assert.Equal(new Vector3(0f, 0f, 0f), MapGeometrySemantics.CalculateOriginForFlags(geometry, 1));
            Assert.Equal(new Vector3(1000f, 50f, 1000f), MapGeometrySemantics.CalculateOriginForFlags(geometry, 2));
        }

        [Fact]
        public void LayersCountSharedMeshTrianglesOnEveryNamedLayer()
        {
            MapGeometryData geometry = Layered(
                (0b0000_0100, 10),
                (0b0100_0100, 5));

            IReadOnlyList<MapGeometryLayerData> layers = MapGeometrySemantics.Layers(geometry);

            Assert.Collection(
                layers,
                layer => Assert.Equal(new MapGeometryLayerData(2, 15), layer),
                layer => Assert.Equal(new MapGeometryLayerData(6, 5), layer));
        }

        [Fact]
        public void OpeningFlagsUseBaseLayerWhileItDrawsAtLeastHalfTheMap()
        {
            MapGeometryData geometry = Layered(
                (0b0000_0001, 250),
                (0b0000_1000, 180),
                (0b1111_1111, 80));

            Assert.Equal(0b0000_0001, MapGeometrySemantics.OpeningFlags(geometry));
        }

        [Fact]
        public void OpeningFlagsUseFullestLayerForVariantBoards()
        {
            MapGeometryData geometry = Layered(
                (0b0000_1000, 5860),
                (0b0100_0000, 5995),
                (0b1111_1111, 8));

            Assert.Equal(0b0100_0000, MapGeometrySemantics.OpeningFlags(geometry));
        }

        [Fact]
        public void OpeningFlagsBreakEqualTriangleTiesTowardTheLowerLayer()
        {
            MapGeometryData geometry = Layered(
                (0b0000_0100, 100),
                (0b0000_1000, 100));

            Assert.Equal(0b0000_0100, MapGeometrySemantics.OpeningFlags(geometry));
        }

        [Fact]
        public void OpeningFlagsAreZeroWhenNoMeshNamesALayer()
        {
            Assert.Equal(0, MapGeometrySemantics.OpeningFlags(Layered((0, 3))));
        }

        private static MapGeometryData Layered(params (byte Visibility, int Triangles)[] meshes)
        {
            var submeshes = new List<MapGeometrySubmeshData>();
            var meshData = new List<MapGeometryMeshData>();
            int start = 0;
            foreach ((byte visibility, int triangles) in meshes)
            {
                int at = submeshes.Count;
                int indices = triangles * 3;
                submeshes.Add(new MapGeometrySubmeshData(start, indices, 0));
                meshData.Add(Mesh(visibility, at, 1));
                start += indices;
            }

            return new MapGeometryData(
                Array.Empty<Vector3>(),
                Array.Empty<Vector3>(),
                Array.Empty<Vector2>(),
                null,
                new uint[start],
                meshData,
                submeshes,
                new[] { "Maps/Test/Material" });
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
