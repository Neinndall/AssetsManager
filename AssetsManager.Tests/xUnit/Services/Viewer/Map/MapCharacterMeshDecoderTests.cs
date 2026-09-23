using System;
using System.IO;
using System.Numerics;
using CommunityToolkit.HighPerformance.Buffers;
using AssetsManager.Services.Viewer.Parsing;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Mesh;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapCharacterMeshDecoderTests
    {
        [Fact]
        public void ClassicSkinnedMeshKeepsAbsoluteIndices()
        {
            using SkinnedMesh mesh = CreateTwoRangeMesh(new ushort[] { 0, 1, 2, 3, 4, 5 });

            var decoded = MapCharacterMeshDecoder.Decode(mesh, majorVersion: 4);

            Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, decoded.Indices);
            Assert.Equal(2, decoded.Ranges.Count);
            Assert.Equal(0, decoded.Ranges[0].StartIndex);
            Assert.Equal(3, decoded.Ranges[1].StartIndex);
            Assert.True(decoded.HasTangents);
            Assert.All(decoded.Tangents, tangent =>
            {
                Assert.True(float.IsFinite(tangent.X));
                Assert.True(float.IsFinite(tangent.Y));
                Assert.True(float.IsFinite(tangent.Z));
                Assert.True(MathF.Abs(tangent.W) == 1f);
            });
        }

        [Fact]
        public void NormalizedSkinnedMeshAddsEachRangeStartVertex()
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                $"AssetsManagerMapNormalizedSkn_{Guid.NewGuid():N}.skn");
            try
            {
                using (SkinnedMesh mesh = CreateTwoRangeMesh(new ushort[] { 0, 1, 2, 0, 1, 2 }))
                    mesh.WriteSimpleSkin(path);
                PatchSimpleSkinFlags(path, SkinnedMeshIndexSemantics.NormalizedIndicesFlag);

                using Stream stream = File.OpenRead(path);
                var decoded = new MapCharacterMeshDecoder().Decode(stream);

                Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, decoded.Indices);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void ClassicRangeMayReferenceSharedVerticesOutsideItsDeclaredVertexSpan()
        {
            using SkinnedMesh mesh = CreateTwoRangeMesh(new ushort[] { 0, 1, 2, 2, 4, 5 });

            var decoded = MapCharacterMeshDecoder.Decode(mesh, majorVersion: 4);

            Assert.Equal(new uint[] { 0, 1, 2, 2, 4, 5 }, decoded.Indices);
        }

        [Fact]
        public void RuntimeTangentBakeBuildsOrthogonalTbnWithHandedness()
        {
            Vector3[] positions =
            {
                new(0f, 0f, 0f),
                new(1f, 0f, 0f),
                new(1f, 1f, 0f),
                new(0f, 1f, 0f)
            };
            Vector3[] normals =
            {
                Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ
            };
            Vector2[] uv =
            {
                new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)
            };
            uint[] indices = { 0, 1, 2, 0, 2, 3 };

            Vector4[] tangents = MapCharacterMeshDecoder.BakeTangents(positions, normals, uv, indices);

            Assert.Equal(4, tangents.Length);
            foreach (Vector4 tangent in tangents)
            {
                Vector3 t = new(tangent.X, tangent.Y, tangent.Z);
                Assert.Equal(1f, t.Length(), 5);
                Assert.Equal(0f, Vector3.Dot(Vector3.UnitZ, t), 5);
                Assert.Equal(1f, tangent.W);
                Assert.True(Vector3.Dot(Vector3.Cross(Vector3.UnitZ, t) * tangent.W, Vector3.UnitY) > 0.999f);
            }
        }

        [Fact]
        public void ResolvedIndexStillMustFitSharedVertexBuffer()
        {
            using SkinnedMesh mesh = CreateTwoRangeMesh(new ushort[] { 0, 1, 2, 3, 4, 9 });

            InvalidDataException error = Assert.Throws<InvalidDataException>(
                () => MapCharacterMeshDecoder.Decode(mesh, majorVersion: 4));

            Assert.Contains("mesh vertices", error.Message, StringComparison.Ordinal);
        }
        private static SkinnedMesh CreateTwoRangeMesh(ushort[] storedIndices)
        {
            Assert.Equal(6, storedIndices.Length);
            VertexBufferDescription description = SkinnedMeshVertex.BASIC;
            MemoryOwner<byte> vertexOwner = VertexBuffer.AllocateForElements(description.Elements, 6);
            Span<byte> vertices = vertexOwner.Span;
            const int stride = 52;
            for (int vertex = 0; vertex < 6; vertex++)
            {
                int at = vertex * stride;
                BitConverter.TryWriteBytes(vertices.Slice(at, 4), (float)vertex);
                BitConverter.TryWriteBytes(vertices.Slice(at + 4, 4), vertex * 2f);
                BitConverter.TryWriteBytes(vertices.Slice(at + 8, 4), vertex * 3f);
                BitConverter.TryWriteBytes(vertices.Slice(at + 16, 4), 1f);
                BitConverter.TryWriteBytes(vertices.Slice(at + 36, 4), 1f);
            }

            VertexBuffer vertexBuffer = VertexBuffer.Create(description.Usage, description.Elements, vertexOwner);
            MemoryOwner<byte> indexOwner = MemoryOwner<byte>.Allocate(storedIndices.Length * sizeof(ushort));
            Span<byte> indices = indexOwner.Span;
            for (int index = 0; index < storedIndices.Length; index++)
                BitConverter.TryWriteBytes(indices.Slice(index * sizeof(ushort), sizeof(ushort)), storedIndices[index]);
            IndexBuffer indexBuffer = IndexBuffer.Create(IndexFormat.U16, indexOwner);

            return new SkinnedMesh(
                new[]
                {
                    new SkinnedMeshRange("body", 0, 3, 0, 3),
                    new SkinnedMeshRange("cape", 3, 3, 3, 3)
                },
                vertexBuffer,
                indexBuffer);
        }

        private static void PatchSimpleSkinFlags(string path, uint flags)
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            stream.Position = 8;
            uint rangeCount = reader.ReadUInt32();
            stream.Position = checked(12L + (rangeCount * 80L));
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(flags);
        }
    }
}
