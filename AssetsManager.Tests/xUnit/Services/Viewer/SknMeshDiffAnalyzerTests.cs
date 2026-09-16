using System.Windows.Media.Media3D;
using AssetsManager.Views.Dialogs;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer
{
    public class SknMeshDiffAnalyzerTests
    {
        [Fact]
        public void EquivalentGeometryIgnoresVertexOrderAndTriangleWinding()
        {
            MeshGeometry3D source = CreateMesh(
                new[]
                {
                    new Point3D(0, 0, 0),
                    new Point3D(1, 0, 0),
                    new Point3D(0, 1, 0)
                },
                0, 1, 2);
            MeshGeometry3D target = CreateMesh(
                new[]
                {
                    new Point3D(0, 1, 0),
                    new Point3D(0, 0, 0),
                    new Point3D(1, 0, 0)
                },
                2, 1, 0);

            Assert.True(SknMeshDiffAnalyzer.AreEquivalent(source, target));
            Assert.Null(SknMeshDiffAnalyzer.ExtractDifferenceMesh(source, target));
        }

        [Fact]
        public void DifferenceDetectsRetriangulationWithSameVertexSet()
        {
            Point3D[] square =
            {
                new(0, 0, 0),
                new(1, 0, 0),
                new(1, 1, 0),
                new(0, 1, 0)
            };
            MeshGeometry3D source = CreateMesh(square, 0, 1, 2, 0, 2, 3);
            MeshGeometry3D target = CreateMesh(square, 0, 1, 3, 1, 2, 3);

            MeshGeometry3D added = SknMeshDiffAnalyzer.ExtractDifferenceMesh(source, target);
            MeshGeometry3D removed = SknMeshDiffAnalyzer.ExtractDifferenceMesh(target, source);

            Assert.False(SknMeshDiffAnalyzer.AreEquivalent(source, target));
            Assert.Equal(2, SknMeshDiffAnalyzer.GetTriangleCount(added));
            Assert.Equal(2, SknMeshDiffAnalyzer.GetTriangleCount(removed));
        }

        [Fact]
        public void DifferencePreservesDuplicateTriangleMultiplicity()
        {
            Point3D[] triangle =
            {
                new(0, 0, 0),
                new(1, 0, 0),
                new(0, 1, 0)
            };
            MeshGeometry3D source = CreateMesh(triangle, 0, 1, 2);
            MeshGeometry3D target = CreateMesh(triangle, 0, 1, 2, 0, 1, 2);

            MeshGeometry3D added = SknMeshDiffAnalyzer.ExtractDifferenceMesh(source, target);

            Assert.Equal(1, SknMeshDiffAnalyzer.GetTriangleCount(added));
        }

        private static MeshGeometry3D CreateMesh(Point3D[] positions, params int[] indices)
        {
            var mesh = new MeshGeometry3D();
            foreach (Point3D position in positions)
                mesh.Positions.Add(position);
            foreach (int index in indices)
                mesh.TriangleIndices.Add(index);
            return mesh;
        }
    }
}
