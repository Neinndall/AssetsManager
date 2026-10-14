using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AssetsManager.Shaders
{
    public enum ShaderResourceKind
    {
        Cbuffer,
        Tbuffer,
        Texture,
        Sampler,
        Structured,
        Other
    }

    public enum ShaderResourceDimension
    {
        Unknown,
        Buffer,
        Texture1D,
        Texture1DArray,
        Texture2D,
        Texture2DArray,
        Texture3D,
        Cube,
        CubeArray,
        Other
    }

    public enum ShaderScalarKind
    {
        Bool,
        Int,
        UInt,
        Float,
        Other
    }

    public enum ShaderTypeClass
    {
        Scalar,
        Vector,
        MatrixRows,
        MatrixColumns,
        Object,
        Struct,
        Other
    }

    public sealed record ShaderResourceData(
        string Name,
        ShaderResourceKind Kind,
        ShaderResourceDimension Dimension,
        uint Bind,
        uint Count,
        uint RawKind,
        uint RawDimension);

    public sealed record ShaderMemberTypeData(
        ShaderScalarKind Scalar,
        ushort Rows,
        ushort Columns,
        ushort Elements,
        ShaderTypeClass Class,
        ushort RawScalar,
        ushort RawClass);

    public sealed record ShaderMemberData(
        string Name,
        uint Offset,
        uint Size,
        bool Used,
        ShaderMemberTypeData Type);

    public sealed record ShaderConstantBufferData(
        string Name,
        uint Size,
        uint? Bind,
        IReadOnlyList<ShaderMemberData> Members);

    public sealed record ShaderSignatureData(
        string Semantic,
        uint Index,
        uint SystemValue,
        uint Register,
        byte Mask,
        byte Used)
    {
        public string Label => $"{Semantic}{Index}";
    }

    public sealed record ShaderReflectionData(
        byte ShaderModelMajor,
        byte ShaderModelMinor,
        IReadOnlyList<ShaderResourceData> Resources,
        IReadOnlyList<ShaderConstantBufferData> ConstantBuffers,
        IReadOnlyList<ShaderSignatureData> Inputs,
        IReadOnlyList<ShaderSignatureData> Outputs);

    public static class DxbcReflection
    {
        private const int DxbcSizeOffset = 24;
        private const int DxbcChunkCountOffset = 28;
        private const int DxbcChunkTableOffset = 32;

        public static byte[] TrimContainer(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < DxbcSizeOffset + sizeof(uint) ||
                bytes[0] != (byte)'D' || bytes[1] != (byte)'X' ||
                bytes[2] != (byte)'B' || bytes[3] != (byte)'C')
            {
                throw new InvalidDataException("Not a DXBC container.");
            }

            int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(DxbcSizeOffset, sizeof(uint))));
            if (size < DxbcSizeOffset + sizeof(uint) || size > bytes.Length)
                throw new InvalidDataException($"DXBC container is truncated at byte {size}.");
            return bytes.Slice(0, size).ToArray();
        }

        public static ShaderReflectionData Reflect(ReadOnlySpan<byte> dxbc)
        {
            byte[] container = TrimContainer(dxbc);
            IReadOnlyDictionary<string, byte[]> chunks = ReadDxbcChunks(container);
            if (!chunks.TryGetValue("RDEF", out byte[] rdef))
                throw new InvalidDataException("DXBC container has no RDEF chunk.");

            ReadRdef(
                rdef,
                out byte major,
                out byte minor,
                out IReadOnlyList<ShaderResourceData> resources,
                out IReadOnlyList<ShaderConstantBufferData> constantBuffers);
            IReadOnlyList<ShaderSignatureData> inputs = chunks.TryGetValue("ISGN", out byte[] isgn)
                ? ReadSignature(isgn)
                : Array.Empty<ShaderSignatureData>();
            IReadOnlyList<ShaderSignatureData> outputs = chunks.TryGetValue("OSGN", out byte[] osgn)
                ? ReadSignature(osgn)
                : Array.Empty<ShaderSignatureData>();

            return new ShaderReflectionData(major, minor, resources, constantBuffers, inputs, outputs);
        }

        private static IReadOnlyDictionary<string, byte[]> ReadDxbcChunks(ReadOnlySpan<byte> bytes)
        {
            uint count = ReadU32(bytes, DxbcChunkCountOffset);
            var chunks = new Dictionary<string, byte[]>(checked((int)count), StringComparer.Ordinal);
            for (uint index = 0; index < count; index++)
            {
                int tableOffset = checked(DxbcChunkTableOffset + checked((int)index * sizeof(uint)));
                int chunkOffset = checked((int)ReadU32(bytes, tableOffset));
                string tag = Encoding.ASCII.GetString(ReadSlice(bytes, chunkOffset, 4));
                int chunkSize = checked((int)ReadU32(bytes, checked(chunkOffset + 4)));
                chunks[tag] = ReadSlice(bytes, checked(chunkOffset + 8), chunkSize).ToArray();
            }
            return chunks;
        }

        private static void ReadRdef(
            ReadOnlySpan<byte> chunk,
            out byte major,
            out byte minor,
            out IReadOnlyList<ShaderResourceData> resources,
            out IReadOnlyList<ShaderConstantBufferData> constantBuffers)
        {
            int constantBufferCount = checked((int)ReadU32(chunk, 0));
            int constantBufferOffset = checked((int)ReadU32(chunk, 4));
            int resourceCount = checked((int)ReadU32(chunk, 8));
            int resourceOffset = checked((int)ReadU32(chunk, 12));
            minor = ReadByte(chunk, 16);
            major = ReadByte(chunk, 17);

            bool rd11 = HasAsciiTag(chunk, 28, "RD11");
            int resourceStride = rd11 ? checked((int)ReadU32(chunk, 40)) : 32;
            int variableStride = rd11 ? checked((int)ReadU32(chunk, 44)) : 24;
            if (resourceStride <= 0 || variableStride <= 0)
                throw new InvalidDataException("DXBC RDEF contains an invalid record stride.");

            var resourceList = new List<ShaderResourceData>(resourceCount);
            for (int index = 0; index < resourceCount; index++)
            {
                int at = checked(resourceOffset + checked(index * resourceStride));
                uint rawKind = ReadU32(chunk, checked(at + 4));
                uint rawDimension = ReadU32(chunk, checked(at + 12));
                resourceList.Add(new ShaderResourceData(
                    ReadCString(chunk, checked((int)ReadU32(chunk, at))),
                    ResolveResourceKind(rawKind),
                    ResolveResourceDimension(rawDimension),
                    ReadU32(chunk, checked(at + 20)),
                    ReadU32(chunk, checked(at + 24)),
                    rawKind,
                    rawDimension));
            }

            var bufferList = new List<ShaderConstantBufferData>(constantBufferCount);
            for (int index = 0; index < constantBufferCount; index++)
            {
                int at = checked(constantBufferOffset + checked(index * 24));
                string name = ReadCString(chunk, checked((int)ReadU32(chunk, at)));
                int memberCount = checked((int)ReadU32(chunk, checked(at + 4)));
                int memberOffset = checked((int)ReadU32(chunk, checked(at + 8)));
                uint size = ReadU32(chunk, checked(at + 12));

                var members = new List<ShaderMemberData>(memberCount);
                for (int memberIndex = 0; memberIndex < memberCount; memberIndex++)
                {
                    int memberAt = checked(memberOffset + checked(memberIndex * variableStride));
                    int typeAt = checked((int)ReadU32(chunk, checked(memberAt + 16)));
                    members.Add(new ShaderMemberData(
                        ReadCString(chunk, checked((int)ReadU32(chunk, memberAt))),
                        ReadU32(chunk, checked(memberAt + 4)),
                        ReadU32(chunk, checked(memberAt + 8)),
                        (ReadU32(chunk, checked(memberAt + 12)) & 2u) != 0,
                        ReadMemberType(chunk, typeAt)));
                }

                ShaderResourceData binding = resourceList.FirstOrDefault(resource =>
                    (resource.Kind == ShaderResourceKind.Cbuffer || resource.Kind == ShaderResourceKind.Tbuffer) &&
                    string.Equals(resource.Name, name, StringComparison.Ordinal));
                bufferList.Add(new ShaderConstantBufferData(name, size, binding?.Bind, members));
            }

            resources = resourceList;
            constantBuffers = bufferList;
        }

        private static ShaderMemberTypeData ReadMemberType(ReadOnlySpan<byte> chunk, int at)
        {
            ushort rawClass = ReadU16(chunk, at);
            ushort rawScalar = ReadU16(chunk, checked(at + 2));
            return new ShaderMemberTypeData(
                ResolveScalarKind(rawScalar),
                ReadU16(chunk, checked(at + 4)),
                ReadU16(chunk, checked(at + 6)),
                ReadU16(chunk, checked(at + 8)),
                ResolveTypeClass(rawClass),
                rawScalar,
                rawClass);
        }

        private static IReadOnlyList<ShaderSignatureData> ReadSignature(ReadOnlySpan<byte> chunk)
        {
            int count = checked((int)ReadU32(chunk, 0));
            var entries = new List<ShaderSignatureData>(count);
            for (int index = 0; index < count; index++)
            {
                int at = checked(8 + checked(index * 24));
                entries.Add(new ShaderSignatureData(
                    ReadCString(chunk, checked((int)ReadU32(chunk, at))),
                    ReadU32(chunk, checked(at + 4)),
                    ReadU32(chunk, checked(at + 8)),
                    ReadU32(chunk, checked(at + 16)),
                    ReadByte(chunk, checked(at + 20)),
                    ReadByte(chunk, checked(at + 21))));
            }
            return entries;
        }

        private static ShaderResourceKind ResolveResourceKind(uint raw) => raw switch
        {
            0 => ShaderResourceKind.Cbuffer,
            1 => ShaderResourceKind.Tbuffer,
            2 => ShaderResourceKind.Texture,
            3 => ShaderResourceKind.Sampler,
            5 => ShaderResourceKind.Structured,
            _ => ShaderResourceKind.Other
        };

        private static ShaderResourceDimension ResolveResourceDimension(uint raw) => raw switch
        {
            0 => ShaderResourceDimension.Unknown,
            1 => ShaderResourceDimension.Buffer,
            2 => ShaderResourceDimension.Texture1D,
            3 => ShaderResourceDimension.Texture1DArray,
            4 => ShaderResourceDimension.Texture2D,
            5 => ShaderResourceDimension.Texture2DArray,
            8 => ShaderResourceDimension.Texture3D,
            9 => ShaderResourceDimension.Cube,
            10 => ShaderResourceDimension.CubeArray,
            _ => ShaderResourceDimension.Other
        };

        private static ShaderScalarKind ResolveScalarKind(ushort raw) => raw switch
        {
            1 => ShaderScalarKind.Bool,
            2 => ShaderScalarKind.Int,
            3 => ShaderScalarKind.Float,
            19 => ShaderScalarKind.UInt,
            _ => ShaderScalarKind.Other
        };

        private static ShaderTypeClass ResolveTypeClass(ushort raw) => raw switch
        {
            0 => ShaderTypeClass.Scalar,
            1 => ShaderTypeClass.Vector,
            2 => ShaderTypeClass.MatrixRows,
            3 => ShaderTypeClass.MatrixColumns,
            4 => ShaderTypeClass.Object,
            5 => ShaderTypeClass.Struct,
            _ => ShaderTypeClass.Other
        };

        private static bool HasAsciiTag(ReadOnlySpan<byte> bytes, int at, string tag)
        {
            if (tag == null || tag.Length != 4 || at < 0 || at > bytes.Length - 4)
                return false;
            return bytes[at] == (byte)tag[0] &&
                   bytes[at + 1] == (byte)tag[1] &&
                   bytes[at + 2] == (byte)tag[2] &&
                   bytes[at + 3] == (byte)tag[3];
        }

        private static string ReadCString(ReadOnlySpan<byte> bytes, int at)
        {
            if (at < 0 || at >= bytes.Length)
                throw new InvalidDataException($"DXBC container is truncated at byte {at}.");
            ReadOnlySpan<byte> tail = bytes.Slice(at);
            int end = tail.IndexOf((byte)0);
            if (end < 0)
                throw new InvalidDataException($"DXBC container is truncated at byte {at}.");
            return Encoding.UTF8.GetString(tail.Slice(0, end));
        }

        private static byte ReadByte(ReadOnlySpan<byte> bytes, int at)
        {
            if ((uint)at >= (uint)bytes.Length)
                throw new InvalidDataException($"DXBC container is truncated at byte {at}.");
            return bytes[at];
        }

        private static ushort ReadU16(ReadOnlySpan<byte> bytes, int at) =>
            BinaryPrimitives.ReadUInt16LittleEndian(ReadSlice(bytes, at, sizeof(ushort)));

        private static uint ReadU32(ReadOnlySpan<byte> bytes, int at) =>
            BinaryPrimitives.ReadUInt32LittleEndian(ReadSlice(bytes, at, sizeof(uint)));

        private static ReadOnlySpan<byte> ReadSlice(ReadOnlySpan<byte> bytes, int at, int count)
        {
            if (at < 0 || count < 0 || at > bytes.Length - count)
                throw new InvalidDataException($"DXBC container is truncated at byte {Math.Max(0, at)}.");
            return bytes.Slice(at, count);
        }
    }
}

