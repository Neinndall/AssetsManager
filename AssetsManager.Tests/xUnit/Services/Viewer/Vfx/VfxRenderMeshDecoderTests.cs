using System;
using System.IO;
using System.Linq;
using System.Text;
using AssetsManager.Services.Viewer.Vfx.Resources;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxRenderMeshDecoderTests
    {
        [Theory]
        [InlineData(".gmesh", "GMSH", true)]
        [InlineData(".tmesh", "????", true)]
        [InlineData(".bin", "GMSH", false)]
        public void LoadsRenderMeshesThroughTheSharedVfxDecoder(string extension, string magic, bool half)
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + extension);
            try
            {
                File.WriteAllBytes(path, Fixture(magic, half));
                var mesh = VfxMeshDecoder.DecodeMesh(path, Array.Empty<uint>(), Array.Empty<uint>());
                Assert.NotNull(mesh);
                Assert.Equal(new float[] { 0, 0, 0, 1, 2, 3, 2, 4, 6 }, mesh.Value.Positions);
                Assert.Equal(new float[] { 0, 1, 0, 0, 1, 0, 0, 1, 0 }, mesh.Value.Normals);
                Assert.Equal(new float[] { 0, 0, 0.5f, -0.25f, 1, -0.5f }, mesh.Value.Uvs);
                Assert.Equal(9, mesh.Value.Indices.Length);
                Assert.Equal(3, mesh.Value.Ranges.Length);
                Assert.All(mesh.Value.Colors, c => Assert.Equal(1f, c));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void PreservesAuthoredSelectionAndAlwaysVisibleSubmeshes()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".gmesh");
            try
            {
                File.WriteAllBytes(path, Fixture("GMSH", true));
                uint gold = Fnv1a.HashLower("Gold"), persistent = Fnv1a.HashLower("Persistent");
                var mesh = VfxMeshDecoder.DecodeMesh(path, new[] { gold }, new[] { persistent }).Value;
                Assert.Equal(new uint[] { 0, 1, 2, 0, 1, 2 }, mesh.Indices);
                Assert.Equal(new[] { persistent, gold }, mesh.Ranges.Select(r => r.Hash));
                Assert.Equal(new[] { 0, 3 }, mesh.Ranges.Select(r => r.StartIndex));
            }
            finally { File.Delete(path); }
        }

        private static byte[] Fixture(string magic, bool half)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(Encoding.ASCII.GetBytes(magic)); writer.Write(1u);
            writer.Write(3u); writer.Write(9u);
            for (int i = 0; i < 6; i++) writer.Write(0f);
            writer.Write(2u);
            Layout(writer, (0u, 2u));
            Layout(writer, (2u, half ? 8u : 2u), (7u, half ? 7u : 1u));
            writer.Write(36u);
            for (int v = 0; v < 3; v++) { writer.Write((float)v); writer.Write(v * 2f); writer.Write(v * 3f); }
            writer.Write(half ? 36u : 60u);
            for (int v = 0; v < 3; v++)
            {
                if (half)
                    foreach (float value in new float[] { 0, 1, 0, 0, v * 0.5f, -v * 0.25f })
                        writer.Write(BitConverter.HalfToUInt16Bits((Half)value));
                else
                    foreach (float value in new float[] { 0, 1, 0, v * 0.5f, -v * 0.25f }) writer.Write(value);
            }
            for (int i = 0; i < 9; i++) writer.Write((ushort)(i % 3));
            writer.Write(3u);
            int start = 0;
            foreach (string material in new[] { "Persistent", "Dark", "Gold" })
            {
                byte[] name = Encoding.UTF8.GetBytes(material);
                writer.Write((uint)name.Length); writer.Write(name);
                writer.Write((uint)start); writer.Write(3u); writer.Write(0u); writer.Write(2u);
                start += 3;
            }
            return stream.ToArray();
        }

        private static void Layout(BinaryWriter writer, params (uint Name, uint Format)[] elements)
        {
            writer.Write(0u); writer.Write((uint)elements.Length);
            for (int i = 0; i < 15; i++)
            {
                writer.Write(i < elements.Length ? elements[i].Name : 0u);
                writer.Write(i < elements.Length ? elements[i].Format : 3u);
            }
        }
    }
}
