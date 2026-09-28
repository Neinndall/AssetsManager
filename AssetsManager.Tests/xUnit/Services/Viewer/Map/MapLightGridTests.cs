using System;
using System.IO;
using System.Numerics;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapLightGridTests
    {
        [Fact]
        public void GridUsesFileOffsetBgrScaleNearestCellAndSceneMirror()
        {
            using MemoryStream stream = Grid(2, 1, 48);
            MapLightGridData grid = MapLightGridParser.Decode(stream);
            Span<Vector3> faces = stackalloc Vector3[6];
            grid.SampleSceneCube(new Vector3(-75, 0, 0), faces);
            Assert.Equal(2, grid.Width);
            Assert.Equal(2f, grid.Scale);
            Assert.Equal(0.25f, grid.FullBright);
            Assert.Equal(new Vector3(61, 51, 41) * (2f / 255f), faces[0]);
            Assert.Equal(new Vector3(60, 50, 40) * (2f / 255f), faces[1]);
            grid.SampleSceneCube(new Vector3(1000, 0, -1000), faces);
            Assert.Equal(new Vector3(21, 11, 1) * (2f / 255f), faces[0]);
            grid.SampleSceneCube(new Vector3(-1000, 0, 1000), faces);
            Assert.Equal(new Vector3(65, 55, 45) * (2f / 255f), faces[5]);
        }

        [Fact]
        public void CharacterVertexBlockUsesGridInsteadOfSun()
        {
            using MemoryStream stream = Grid(2, 1, 32);
            MapLightGridData grid = MapLightGridParser.Decode(stream);
            var frame = new GameShaderRuntime.Frame(Matrix4x4.Identity, Matrix4x4.Identity,
                Vector3.Zero, 0, null, grid, new Vector3(-75, 0, 0));
            var data = new float[64];
            GameShaderRuntime.WriteCharacterPerDrawVertex(data, frame, System.Numerics.Matrix4x4.Identity);
            Assert.Equal(61 * (2f / 255f), data[16]);
            Assert.Equal(51 * (2f / 255f), data[17]);
            Assert.Equal(41 * (2f / 255f), data[18]);
            Assert.Equal(1f, data[19]);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(-1, 1)]
        [InlineData(int.MaxValue, int.MaxValue)]
        public void InvalidDimensionsAreRejectedWithoutAllocatingCells(int width, int height)
        {
            using MemoryStream stream = Grid(1, 1, 32);
            stream.Position = 8;
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(width);
                writer.Write(height);
            }
            stream.Position = 0;
            Assert.Throws<InvalidDataException>(() => MapLightGridParser.Decode(stream));
        }

        [Fact]
        public void TruncatedCellsAreRejected()
        {
            using MemoryStream stream = Grid(2, 1, 32);
            stream.SetLength(stream.Length - 1);
            Assert.Throws<InvalidDataException>(() => MapLightGridParser.Decode(stream));
        }

        private static MemoryStream Grid(int width, int height, int offset)
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(3u);
                writer.Write((uint)offset);
                writer.Write(width);
                writer.Write(height);
                writer.Write(100f);
                writer.Write(100f);
                writer.Write(0.5f);
                writer.Write(0.25f);
                while (stream.Position < offset) writer.Write((byte)0);
                for (int cell = 0; cell < width * height; cell++)
                for (int face = 0; face < 6; face++)
                {
                    writer.Write((byte)(cell * 40 + face));
                    writer.Write((byte)(cell * 40 + face + 10));
                    writer.Write((byte)(cell * 40 + face + 20));
                    writer.Write((byte)0);
                }
            }
            stream.Position = 0;
            return stream;
        }
    }
}
