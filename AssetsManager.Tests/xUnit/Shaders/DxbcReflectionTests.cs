using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetsManager.Shaders;
using Xunit;

namespace AssetsManager.Tests.xUnit.Shaders
{
    public sealed class DxbcReflectionTests
    {
        [Fact]
        public void TrimKeepsDeclaredContainerAndRejectsInvalidInput()
        {
            byte[] blob = Container();
            byte[] padded = blob.Concat(new byte[] { 0x35 }).ToArray();

            Assert.Equal(blob, DxbcReflection.TrimContainer(padded));
            Assert.Throws<InvalidDataException>(() => DxbcReflection.TrimContainer("XXXX"u8));
            Assert.Throws<InvalidDataException>(() => DxbcReflection.TrimContainer(blob.AsSpan(0, blob.Length - 1)));
        }

        [Fact]
        public void ConstantBuffersCarryMembersAtAuthoredOffsets()
        {
            ShaderReflectionData reflection = DxbcReflection.Reflect(Container());

            Assert.Equal(5, reflection.ShaderModelMajor);
            Assert.Equal(0, reflection.ShaderModelMinor);

            ShaderConstantBufferData globals = reflection.ConstantBuffers[0];
            Assert.Equal("$Globals", globals.Name);
            Assert.Equal(32u, globals.Size);
            Assert.Equal(0u, globals.Bind);
            Assert.Equal(2, globals.Members.Count);
            Assert.Equal("Tint", globals.Members[0].Name);
            Assert.Equal(0u, globals.Members[0].Offset);
            Assert.True(globals.Members[0].Used);
            Assert.Equal(ShaderTypeClass.Vector, globals.Members[0].Type.Class);
            Assert.Equal((ushort)4, globals.Members[0].Type.Columns);
            Assert.Equal("Time", globals.Members[1].Name);
            Assert.Equal(16u, globals.Members[1].Offset);
            Assert.False(globals.Members[1].Used);

            ShaderConstantBufferData perFrame = reflection.ConstantBuffers[1];
            Assert.Equal("PerFrameVertexCB", perFrame.Name);
            Assert.Equal(560u, perFrame.Size);
            Assert.Equal(1u, perFrame.Bind);
            Assert.Equal(ShaderTypeClass.MatrixColumns, perFrame.Members[0].Type.Class);
            Assert.Equal((ushort)4, perFrame.Members[0].Type.Rows);
        }

        [Fact]
        public void ResourcesAreResolvedByKindDimensionAndRegister()
        {
            ShaderReflectionData reflection = DxbcReflection.Reflect(Container());

            ShaderResourceData texture = reflection.Resources.Single(item => item.Kind == ShaderResourceKind.Texture);
            Assert.Equal("Diffuse_Texture__TX", texture.Name);
            Assert.Equal(ShaderResourceDimension.Texture2D, texture.Dimension);
            Assert.Equal(2u, texture.Bind);

            ShaderResourceData structured = reflection.Resources.Single(item => item.Kind == ShaderResourceKind.Structured);
            Assert.Equal("CLUSTER_DATA", structured.Name);
            Assert.Equal(ShaderResourceDimension.Buffer, structured.Dimension);
            Assert.Equal(0u, structured.Bind);

            ShaderResourceData sampler = reflection.Resources.Single(item => item.Kind == ShaderResourceKind.Sampler);
            Assert.Equal("Diffuse_Texture__SMP", sampler.Name);
            Assert.Equal(1u, sampler.Bind);
        }

        [Fact]
        public void SignaturesCarrySemanticsRegistersAndMasks()
        {
            ShaderReflectionData reflection = DxbcReflection.Reflect(Container());

            Assert.Equal(2, reflection.Inputs.Count);
            Assert.Equal("POSITION0", reflection.Inputs[0].Label);
            Assert.Equal(0u, reflection.Inputs[0].Register);
            Assert.Equal(0b0111, reflection.Inputs[0].Mask);
            Assert.Equal("TEXCOORD0", reflection.Inputs[1].Label);
            Assert.Equal(1u, reflection.Inputs[1].Register);
            Assert.Equal("SV_Position", reflection.Outputs[0].Semantic);
            Assert.Equal(0b0011, reflection.Outputs[1].Used);
        }

        [Fact]
        public void ContainerWithoutReflectionIsRejected()
        {
            var bytes = new Bytes();
            bytes.Raw(Encoding.ASCII.GetBytes("DXBC"));
            bytes.Raw(new byte[16]);
            bytes.U32(1).U32(32).U32(0);

            InvalidDataException error = Assert.Throws<InvalidDataException>(() => DxbcReflection.Reflect(bytes.ToArray()));
            Assert.Contains("RDEF", error.Message, StringComparison.Ordinal);
        }

        private static byte[] Container()
        {
            var chunks = new (string Tag, byte[] Body)[]
            {
                ("RDEF", Rdef()),
                ("ISGN", Signature(new[]
                {
                    ("POSITION", 0u, 0u, (byte)0b0111),
                    ("TEXCOORD", 0u, 1u, (byte)0b0011)
                })),
                ("OSGN", Signature(new[]
                {
                    ("SV_Position", 0u, 0u, (byte)0b1111),
                    ("TEXCOORD", 0u, 1u, (byte)0b0011)
                }))
            };

            const uint tableAt = 32;
            uint at = tableAt + 4u * (uint)chunks.Length;
            var offsets = new List<uint>();
            foreach ((_, byte[] body) in chunks)
            {
                offsets.Add(at);
                at += 8u + (uint)body.Length;
            }

            var bytes = new Bytes();
            bytes.Raw(Encoding.ASCII.GetBytes("DXBC"));
            bytes.Raw(new byte[16]);
            bytes.U32(1).U32(at).U32((uint)chunks.Length);
            foreach (uint offset in offsets) bytes.U32(offset);
            foreach ((string tag, byte[] body) in chunks)
            {
                bytes.Raw(Encoding.ASCII.GetBytes(tag));
                bytes.U32((uint)body.Length);
                bytes.Raw(body);
            }
            return bytes.ToArray();
        }

        private static byte[] Rdef()
        {
            const uint header = 60;
            const uint resource = 40;
            const uint cbuffer = 24;
            const uint variable = 40;
            const uint type = 36;
            uint resourcesAt = header;
            uint cbuffersAt = resourcesAt + 5 * resource;
            uint variablesAt = cbuffersAt + 2 * cbuffer;
            uint typesAt = variablesAt + 3 * variable;
            uint stringsAt = typesAt + 3 * type;

            var strings = new Bytes();
            uint globals = stringsAt + strings.String("$Globals");
            uint perFrame = stringsAt + strings.String("PerFrameVertexCB");
            uint diffuse = stringsAt + strings.String("Diffuse_Texture__TX");
            uint cluster = stringsAt + strings.String("CLUSTER_DATA");
            uint sampler = stringsAt + strings.String("Diffuse_Texture__SMP");
            uint tint = stringsAt + strings.String("Tint");
            uint time = stringsAt + strings.String("Time");
            uint proj = stringsAt + strings.String("mProj");

            var bytes = new Bytes();
            bytes.U32(2).U32(cbuffersAt).U32(5).U32(resourcesAt);
            bytes.U8(0).U8(5).U16(1).U32(0).U32(0);
            bytes.Raw(Encoding.ASCII.GetBytes("RD11"));
            bytes.U32(header).U32(cbuffer).U32(resource).U32(variable).U32(type).U32(12).U32(0);
            Assert.Equal(resourcesAt, (uint)bytes.Length);

            void Resource(uint name, uint kind, uint dimension, uint bind)
            {
                bytes.U32(name).U32(kind).U32(0).U32(dimension).U32(0).U32(bind).U32(1).U32(0);
                bytes.U32(0).U32(0);
            }
            Resource(globals, 0, 0, 0);
            Resource(perFrame, 0, 0, 1);
            Resource(diffuse, 2, 4, 2);
            Resource(cluster, 5, 1, 0);
            Resource(sampler, 3, 0, 1);
            Assert.Equal(cbuffersAt, (uint)bytes.Length);

            bytes.U32(globals).U32(2).U32(variablesAt).U32(32).U32(0).U32(0);
            bytes.U32(perFrame).U32(1).U32(variablesAt + 2 * variable).U32(560).U32(0).U32(0);
            Assert.Equal(variablesAt, (uint)bytes.Length);

            void Variable(uint name, uint offset, uint size, bool used, uint typeAt)
            {
                bytes.U32(name).U32(offset).U32(size).U32(used ? 2u : 0u).U32(typeAt);
                bytes.U32(0).U32(0).U32(0).U32(0).U32(0);
            }
            Variable(tint, 0, 16, true, typesAt);
            Variable(time, 16, 4, false, typesAt + type);
            Variable(proj, 0, 64, true, typesAt + 2 * type);
            Assert.Equal(typesAt, (uint)bytes.Length);

            void Type(ushort @class, ushort scalar, ushort rows, ushort columns)
            {
                bytes.U16(@class).U16(scalar).U16(rows).U16(columns).U16(0).U16(0).U32(0);
                bytes.U32(0).U32(0).U32(0).U32(0).U32(0);
            }
            Type(1, 3, 1, 4);
            Type(0, 3, 1, 1);
            Type(3, 3, 4, 4);
            Assert.Equal(stringsAt, (uint)bytes.Length);

            bytes.Raw(strings.ToArray());
            return bytes.ToArray();
        }

        private static byte[] Signature((string Semantic, uint Index, uint Register, byte Mask)[] entries)
        {
            var bytes = new Bytes();
            bytes.U32((uint)entries.Length).U32(8);
            uint stringsAt = 8u + 24u * (uint)entries.Length;
            var strings = new Bytes();
            foreach ((string semantic, uint index, uint register, byte mask) in entries)
            {
                uint name = stringsAt + strings.String(semantic);
                bytes.U32(name).U32(index).U32(0).U32(3).U32(register);
                bytes.U8(mask).U8(mask).U16(0);
            }
            bytes.Raw(strings.ToArray());
            return bytes.ToArray();
        }

        private sealed class Bytes
        {
            private readonly List<byte> _bytes = new();
            public int Length => _bytes.Count;

            public Bytes U32(uint value)
            {
                _bytes.AddRange(BitConverter.GetBytes(value));
                return this;
            }

            public Bytes U16(ushort value)
            {
                _bytes.AddRange(BitConverter.GetBytes(value));
                return this;
            }

            public Bytes U8(byte value)
            {
                _bytes.Add(value);
                return this;
            }

            public Bytes Raw(byte[] value)
            {
                _bytes.AddRange(value);
                return this;
            }

            public uint String(string value)
            {
                uint at = (uint)_bytes.Count;
                _bytes.AddRange(Encoding.UTF8.GetBytes(value));
                _bytes.Add(0);
                return at;
            }

            public byte[] ToArray() => _bytes.ToArray();
        }
    }
}
