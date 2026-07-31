using System;
using System.Numerics;
using System.Windows.Media.Media3D;

namespace AssetsManager.Services.Viewer.Rendering
{
    internal sealed class GlMeshVertexData
    {
        private const int FloatsPerVertex = 8;
        private readonly Vector3[] _normalAccumulator;

        public GlMeshVertexData(int vertexCount)
        {
            if (vertexCount < 0)
                throw new ArgumentOutOfRangeException(nameof(vertexCount));

            VertexCount = vertexCount;
            Data = new float[vertexCount * FloatsPerVertex];
            _normalAccumulator = new Vector3[vertexCount];
        }

        public int VertexCount { get; }
        public float[] Data { get; }

        public void Update(MeshGeometry3D mesh, bool updateTextureCoordinates)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (mesh.Positions == null || mesh.Positions.Count != VertexCount)
                throw new ArgumentException("Mesh positions must match the vertex buffer size.", nameof(mesh));

            Array.Clear(_normalAccumulator, 0, _normalAccumulator.Length);
            AccumulateNormals(mesh);
            WriteVertices(mesh, updateTextureCoordinates);
        }

        private void AccumulateNormals(MeshGeometry3D mesh)
        {
            var indices = mesh.TriangleIndices;
            if (indices == null) return;

            for (int i = 0; i + 2 < indices.Count; i += 3)
            {
                int idx0 = indices[i];
                int idx1 = indices[i + 1];
                int idx2 = indices[i + 2];
                if ((uint)idx0 >= VertexCount ||
                    (uint)idx1 >= VertexCount ||
                    (uint)idx2 >= VertexCount)
                {
                    continue;
                }

                Point3D p0 = mesh.Positions[idx0];
                Point3D p1 = mesh.Positions[idx1];
                Point3D p2 = mesh.Positions[idx2];
                Vector3 edge0 = new(
                    (float)(p1.X - p0.X),
                    (float)(p1.Y - p0.Y),
                    (float)(p1.Z - p0.Z));
                Vector3 edge1 = new(
                    (float)(p2.X - p0.X),
                    (float)(p2.Y - p0.Y),
                    (float)(p2.Z - p0.Z));
                Vector3 normal = Vector3.Cross(edge0, edge1);
                if (normal.LengthSquared() <= float.Epsilon)
                {
                    continue;
                }

                normal = Vector3.Normalize(normal);
                _normalAccumulator[idx0] += normal;
                _normalAccumulator[idx1] += normal;
                _normalAccumulator[idx2] += normal;
            }
        }

        private void WriteVertices(MeshGeometry3D mesh, bool updateTextureCoordinates)
        {
            var textureCoordinates = mesh.TextureCoordinates;
            for (int i = 0; i < VertexCount; i++)
            {
                Point3D position = mesh.Positions[i];
                int offset = i * FloatsPerVertex;
                Data[offset] = (float)position.X;
                Data[offset + 1] = (float)position.Y;
                Data[offset + 2] = (float)position.Z;

                Vector3 normal = _normalAccumulator[i].LengthSquared() > 0f
                    ? Vector3.Normalize(_normalAccumulator[i])
                    : Vector3.UnitY;
                Data[offset + 3] = normal.X;
                Data[offset + 4] = normal.Y;
                Data[offset + 5] = normal.Z;

                if (!updateTextureCoordinates) continue;

                if (textureCoordinates != null && i < textureCoordinates.Count)
                {
                    Data[offset + 6] = (float)textureCoordinates[i].X;
                    Data[offset + 7] = (float)textureCoordinates[i].Y;
                }
                else
                {
                    Data[offset + 6] = 0f;
                    Data[offset + 7] = 0f;
                }
            }
        }
    }
}
