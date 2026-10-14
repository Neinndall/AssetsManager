using System;
using System.IO;
using AssetsManager.Services.Viewer.Vfx.Resources;
using CommunityToolkit.HighPerformance.Buffers;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Mesh;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxSkinnedMeshColorTests
    {
        [Fact]
        public void ParticleSknOfTheColorTypeKeepsItsVertexColours()
        {
            string path = Path.Combine(Path.GetTempPath(), $"AssetsManagerVfxColorSkn_{Guid.NewGuid():N}.skn");
            try
            {
                // B8G8R8A8 as stored: a red opaque vertex, a green half-transparent one and a clear one.
                byte[][] bgra = { new byte[] { 0, 0, 255, 255 }, new byte[] { 0, 255, 0, 128 }, new byte[] { 255, 255, 255, 0 } };
                using (SkinnedMesh mesh = CreateColoredTriangle(bgra))
                    mesh.WriteSimpleSkin(path);

                var decoded = VfxMeshDecoder.DecodeMesh(path, Array.Empty<uint>(), Array.Empty<uint>());

                Assert.NotNull(decoded);
                float[] colors = decoded.Value.Colors;
                Assert.Equal(new[] { 1f, 0f, 0f, 1f }, colors[0..4]);
                Assert.Equal(new[] { 0f, 1f, 0f, 128 / 255f }, colors[4..8]);
                Assert.Equal(0f, colors[11]);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static SkinnedMesh CreateColoredTriangle(byte[][] bgra)
        {
            VertexBufferDescription description = SkinnedMeshVertex.COLOR;
            MemoryOwner<byte> vertexOwner = VertexBuffer.AllocateForElements(description.Elements, 3);
            Span<byte> vertices = vertexOwner.Span;
            const int stride = 56; // position 12, blend index 4, blend weight 16, normal 12, uv 8, colour 4
            for (int vertex = 0; vertex < 3; vertex++)
            {
                int at = vertex * stride;
                BitConverter.TryWriteBytes(vertices.Slice(at, 4), (float)vertex);
                BitConverter.TryWriteBytes(vertices.Slice(at + 16, 4), 1f);
                BitConverter.TryWriteBytes(vertices.Slice(at + 36, 4), 1f);
                bgra[vertex].CopyTo(vertices.Slice(at + 52, 4));
            }

            VertexBuffer vertexBuffer = VertexBuffer.Create(description.Usage, description.Elements, vertexOwner);
            MemoryOwner<byte> indexOwner = MemoryOwner<byte>.Allocate(3 * sizeof(ushort));
            for (int index = 0; index < 3; index++)
                BitConverter.TryWriteBytes(indexOwner.Span.Slice(index * sizeof(ushort), sizeof(ushort)), (ushort)index);
            IndexBuffer indexBuffer = IndexBuffer.Create(IndexFormat.U16, indexOwner);

            return new SkinnedMesh(new[] { new SkinnedMeshRange("mesh", 0, 3, 0, 3) }, vertexBuffer, indexBuffer);
        }
    }
}
