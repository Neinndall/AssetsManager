using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AssetsManager.Services.Viewer.Loading
{
    /// <summary>Indexes BIN object identities without decoding unrelated property trees.</summary>
    internal static class BinObjectHeaders
    {
        internal static uint[] Read(Stream stream)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            string Magic() => Encoding.ASCII.GetString(reader.ReadBytes(4));
            string magic = Magic();
            if (magic == "PTCH")
            {
                if (reader.ReadUInt32() != 1) throw new InvalidDataException("Unsupported BIN patch header.");
                reader.ReadUInt32();
                magic = Magic();
            }
            if (magic != "PROP") throw new InvalidDataException("Not a property BIN.");
            uint version = reader.ReadUInt32();
            if (version is < 1 or > 3) throw new InvalidDataException("Unsupported BIN version.");
            if (version >= 2)
            {
                uint dependencies = reader.ReadUInt32();
                if (dependencies > stream.Length / 2) throw new InvalidDataException("Invalid BIN dependency count.");
                for (uint index = 0; index < dependencies; index++)
                {
                    ushort size = reader.ReadUInt16();
                    if (size > stream.Length - stream.Position) throw new EndOfStreamException();
                    stream.Position += size;
                }
            }
            uint count = reader.ReadUInt32();
            if (count > (stream.Length - stream.Position) / 4) throw new InvalidDataException("Invalid BIN object count.");
            stream.Position += count * 4L;
            var result = new uint[count];
            for (int index = 0; index < count; index++)
            {
                uint size = reader.ReadUInt32();
                if (size < 6 || size > stream.Length - stream.Position) throw new InvalidDataException("Invalid BIN object size.");
                long end = stream.Position + size;
                result[index] = reader.ReadUInt32();
                stream.Position = end;
            }
            return result;
        }
    }
}
