using System.IO;
using System.Numerics;
using System.Text;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Memory;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapGeometryDecoderTests
    {
        [Theory]
        [InlineData("Maps/Test/Material", "Maps/Test/Material")]
        [InlineData("Maps/Test/Material\0", "Maps/Test/Material")]
        [InlineData("Maps/Test/Material\0\0\0", "Maps/Test/Material")]
        public void MaterialPathsDropOnlyTrailingMapgeoPadding(string authored, string expected)
        {
            Assert.Equal(expected, MapGeometryDecoder.CanonicalMaterialPath(authored));
        }

        [Fact]
        public void Decode_ToleratesUnreferencedVertexBuffers()
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            // Magic and version
            bw.Write(Encoding.ASCII.GetBytes("OEGM"));
            bw.Write(17); // version 17

            // Sampler defs count = 0
            bw.Write(0);

            // Vertex declarations count = 1
            bw.Write((uint)1);
            bw.Write((uint)0); // usage = Static (0)
            bw.Write((uint)1); // vertexElementCount = 1
            bw.Write((uint)ElementName.Position); // name = 0 (Position)
            bw.Write((uint)ElementFormat.XYZ_Float32); // format = 2 (XYZ_Float32)
            for (int i = 0; i < 14; i++)
            {
                bw.Write((uint)0);
                bw.Write((uint)ElementFormat.XYZ_Float32);
            }

            // Vertex buffers: 2 buffers declared in file, but mesh only references buffer 0.
            // Buffer 1 is completely UNREFERENCED, matching what LTK commit e3f95cf tolerates.
            bw.Write((uint)2);

            // Buffer 0 (referenced): 1 vertex of Float3 = 12 bytes
            bw.Write((byte)1); // visibility
            bw.Write((uint)12); // bufferSize
            bw.Write(10.0f);
            bw.Write(20.0f);
            bw.Write(30.0f);

            // Buffer 1 (UNREFERENCED): extra buffer bytes
            bw.Write((byte)1); // visibility
            bw.Write((uint)24); // bufferSize
            bw.Write(new byte[24]);

            // Index buffers: 1 buffer with 1 index
            bw.Write((uint)1);
            bw.Write((byte)1); // visibility
            bw.Write((int)2); // bufferSize
            bw.Write((ushort)0);

            // Meshes: 1 mesh
            bw.Write((uint)1);
            bw.Write(1); // vertexCount = 1
            bw.Write((uint)1); // vertexDeclarationCount = 1
            bw.Write(0); // vertexDeclarationId = 0
            bw.Write(0); // vertexBufferId = 0 (only buffer 0 is referenced!)

            bw.Write((uint)1); // indexCount = 1
            bw.Write(0); // indexBufferId = 0
            bw.Write((byte)1); // visibilityFlags (version >= 13)
            bw.Write((uint)0); // visibilityControllerPathHash (version >= 15)

            // Submeshes: 1
            bw.Write((uint)1);
            bw.Write((uint)0); // hash
            bw.Write(10); // material length
            bw.Write(Encoding.ASCII.GetBytes("Test/Mat01"));
            bw.Write(0); // startIndex
            bw.Write(1); // indexCount
            bw.Write(0); // minVertex
            bw.Write(0); // maxVertex

            bw.Write(false); // disableBackfaceCulling
            // BoundingBox: min (0,0,0), max (10,20,30)
            bw.Write(0f); bw.Write(0f); bw.Write(0f);
            bw.Write(10f); bw.Write(20f); bw.Write(30f);
            // Transform: Identity (Matrix4x4 row-major)
            bw.Write(1f); bw.Write(0f); bw.Write(0f); bw.Write(0f);
            bw.Write(0f); bw.Write(1f); bw.Write(0f); bw.Write(0f);
            bw.Write(0f); bw.Write(0f); bw.Write(1f); bw.Write(0f);
            bw.Write(0f); bw.Write(0f); bw.Write(0f); bw.Write(1f);
            bw.Write((byte)0xFF); // quality
            bw.Write((byte)0); // layer transition behavior (version >= 14)
            bw.Write((ushort)0); // render flags (version >= 16)

            // BakedLight channel
            bw.Write(0); // texture length
            bw.Write(1f); bw.Write(1f); // scale
            bw.Write(0f); bw.Write(0f); // bias

            // StationaryLight channel
            bw.Write(0); // texture length
            bw.Write(1f); bw.Write(1f); // scale
            bw.Write(0f); bw.Write(0f); // bias

            // TextureOverrides (version >= 17)
            bw.Write(0); // count = 0
            bw.Write(1f); bw.Write(1f); // bakedPaintScale
            bw.Write(0f); bw.Write(0f); // bakedPaintBias

            // Scene graphs (version >= 15)
            bw.Write(0); // count = 0

            // Planar reflectors (version >= 13)
            bw.Write((uint)0); // count = 0

            bw.Flush();
            ms.Position = 0;

            var decoder = new MapGeometryDecoder();
            MapGeometryData data = decoder.Decode(ms);

            Assert.NotNull(data);
            Assert.Single(data.Positions);
            Assert.Single(data.Indices);
            Assert.Equal(new Vector3(10f, 20f, 30f), data.Positions[0]);
        }

        [Fact]
        public void Decode_ReadsVertexColorAndWorldSpaceGrassPivot()
        {
            // VertexDeform grass bends each blade around TEXCOORD5 and weights it by COLOR0.
            using MemoryStream stream = SingleVertexMapgeo(withGrassStreams: true, translation: new Vector3(100f, 0f, 0f));

            MapGeometryData data = new MapGeometryDecoder().Decode(stream);

            Assert.True(data.HasColors);
            Assert.True(data.HasPivots);
            Assert.Equal(new Vector3(110f, 20f, 30f), data.Positions[0]);
            // BGRA memory (b=0, g=128, r=255, a=255) reaches the shader as RGBA.
            Assert.Equal(new Vector4(1f, 128f / 255f, 0f, 1f), data.Colors[0]);
            Assert.Equal(new Vector3(105f, 0f, 30f), data.Pivots[0]);
        }

        [Fact]
        public void Decode_LeavesStreamsOutWhenNoMeshCarriesThem()
        {
            using MemoryStream stream = SingleVertexMapgeo(withGrassStreams: false, translation: Vector3.Zero);

            MapGeometryData data = new MapGeometryDecoder().Decode(stream);

            Assert.False(data.HasColors);
            Assert.False(data.HasPivots);
        }

        private static MemoryStream SingleVertexMapgeo(bool withGrassStreams, Vector3 translation)
        {
            var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            bw.Write(Encoding.ASCII.GetBytes("OEGM"));
            bw.Write(17);
            bw.Write(0);

            (ElementName Name, ElementFormat Format)[] elements = withGrassStreams
                ? new[]
                {
                    (ElementName.Position, ElementFormat.XYZ_Float32),
                    (ElementName.PrimaryColor, ElementFormat.BGRA_Packed8888),
                    (ElementName.Texcoord5, ElementFormat.XYZ_Float32)
                }
                : new[] { (ElementName.Position, ElementFormat.XYZ_Float32) };
            bw.Write((uint)1);
            bw.Write((uint)0);
            bw.Write((uint)elements.Length);
            foreach ((ElementName name, ElementFormat format) in elements)
            {
                bw.Write((uint)name);
                bw.Write((uint)format);
            }
            for (int i = elements.Length; i < 15; i++)
            {
                bw.Write((uint)0);
                bw.Write((uint)ElementFormat.XYZ_Float32);
            }

            bw.Write((uint)1);
            bw.Write((byte)1);
            bw.Write((uint)(withGrassStreams ? 28 : 12));
            bw.Write(10f); bw.Write(20f); bw.Write(30f);
            if (withGrassStreams)
            {
                bw.Write((byte)0); bw.Write((byte)128); bw.Write((byte)255); bw.Write((byte)255);
                bw.Write(5f); bw.Write(0f); bw.Write(30f);
            }

            bw.Write((uint)1);
            bw.Write((byte)1);
            bw.Write(2);
            bw.Write((ushort)0);

            bw.Write((uint)1);
            bw.Write(1);
            bw.Write((uint)1);
            bw.Write(0);
            bw.Write(0);
            bw.Write((uint)1);
            bw.Write(0);
            bw.Write((byte)1);
            bw.Write((uint)0);
            bw.Write((uint)1);
            bw.Write((uint)0);
            bw.Write(10);
            bw.Write(Encoding.ASCII.GetBytes("Test/Mat01"));
            bw.Write(0); bw.Write(1); bw.Write(0); bw.Write(0);
            bw.Write(false);
            bw.Write(0f); bw.Write(0f); bw.Write(0f);
            bw.Write(10f); bw.Write(20f); bw.Write(30f);
            bw.Write(1f); bw.Write(0f); bw.Write(0f); bw.Write(0f);
            bw.Write(0f); bw.Write(1f); bw.Write(0f); bw.Write(0f);
            bw.Write(0f); bw.Write(0f); bw.Write(1f); bw.Write(0f);
            bw.Write(translation.X); bw.Write(translation.Y); bw.Write(translation.Z); bw.Write(1f);
            bw.Write((byte)0xFF);
            bw.Write((byte)0);
            bw.Write((ushort)0);
            for (int channel = 0; channel < 2; channel++)
            {
                bw.Write(0);
                bw.Write(1f); bw.Write(1f);
                bw.Write(0f); bw.Write(0f);
            }
            bw.Write(0);
            bw.Write(1f); bw.Write(1f);
            bw.Write(0f); bw.Write(0f);
            bw.Write(0);
            bw.Write((uint)0);
            bw.Flush();
            ms.Position = 0;
            return ms;
        }
    }
}
