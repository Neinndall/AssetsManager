using System;
using System.Numerics;

namespace AssetsManager.Services.Viewer.Rendering
{
    internal static class MeshTangentBuilder
    {
        internal static Vector4[] Build(
            Vector3[] positions,
            Vector3[] normals,
            Vector2[] uv,
            uint[] indices)
        {
            int vertexCount = positions?.Length ?? 0;
            if (vertexCount == 0 || uv == null || uv.Length != vertexCount || indices == null)
                return null;

            Vector3[] basisNormals = normals != null && normals.Length == vertexCount
                ? normals
                : BuildNormals(positions, indices);
            var tangentSum = new Vector3[vertexCount];
            var bitangentSum = new Vector3[vertexCount];

            for (int index = 0; index + 2 < indices.Length; index += 3)
            {
                int a = checked((int)indices[index]);
                int b = checked((int)indices[index + 1]);
                int c = checked((int)indices[index + 2]);
                if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount)
                    continue;

                Vector3 edge1 = positions[b] - positions[a];
                Vector3 edge2 = positions[c] - positions[a];
                Vector2 duv1 = uv[b] - uv[a];
                Vector2 duv2 = uv[c] - uv[a];
                float determinant = duv1.X * duv2.Y - duv1.Y * duv2.X;
                if (!float.IsFinite(determinant) || MathF.Abs(determinant) <= 1e-12f)
                    continue;

                float reciprocal = 1f / determinant;
                Vector3 tangent = (edge1 * duv2.Y - edge2 * duv1.Y) * reciprocal;
                Vector3 bitangent = (edge2 * duv1.X - edge1 * duv2.X) * reciprocal;
                if (!IsFinite(tangent) || !IsFinite(bitangent))
                    continue;

                tangentSum[a] += tangent;
                tangentSum[b] += tangent;
                tangentSum[c] += tangent;
                bitangentSum[a] += bitangent;
                bitangentSum[b] += bitangent;
                bitangentSum[c] += bitangent;
            }

            var result = new Vector4[vertexCount];
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                Vector3 normal = basisNormals[vertex];
                if (!IsFinite(normal) || normal.LengthSquared() <= 1e-12f)
                    normal = Vector3.UnitY;
                else
                    normal = Vector3.Normalize(normal);

                Vector3 tangent = tangentSum[vertex] - normal * Vector3.Dot(normal, tangentSum[vertex]);
                if (!IsFinite(tangent) || tangent.LengthSquared() <= 1e-12f)
                {
                    Vector3 axis = MathF.Abs(normal.Y) < 0.999f ? Vector3.UnitY : Vector3.UnitX;
                    tangent = Vector3.Cross(axis, normal);
                }
                tangent = Vector3.Normalize(tangent);
                float handedness = Vector3.Dot(Vector3.Cross(normal, tangent), bitangentSum[vertex]) < 0f ? -1f : 1f;
                result[vertex] = new Vector4(tangent, handedness);
            }
            return result;
        }

        private static Vector3[] BuildNormals(Vector3[] positions, uint[] indices)
        {
            var normals = new Vector3[positions.Length];
            for (int index = 0; index + 2 < indices.Length; index += 3)
            {
                int a = checked((int)indices[index]);
                int b = checked((int)indices[index + 1]);
                int c = checked((int)indices[index + 2]);
                if ((uint)a >= (uint)positions.Length || (uint)b >= (uint)positions.Length || (uint)c >= (uint)positions.Length)
                    continue;

                Vector3 normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                if (!IsFinite(normal) || normal.LengthSquared() <= 1e-12f)
                    continue;
                normals[a] += normal;
                normals[b] += normal;
                normals[c] += normal;
            }
            return normals;
        }

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}
