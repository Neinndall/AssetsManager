using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AssetsManager.Utils
{
    public static class StringTableUtils
    {
        public static (Dictionary<ulong, string> Entries, int HashBits) Parse(Stream stream, int gameVersion = 1502)
        {
            var entries = new Dictionary<ulong, string>();
            int hashBits;
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                var magic = reader.ReadBytes(3);
                if (Encoding.ASCII.GetString(magic) != "RST")
                {
                    throw new InvalidDataException("Invalid magic code. Expected 'RST'.");
                }

                var version = reader.ReadByte();


                switch (version)
                {
                    case 2:
                        if (reader.ReadBoolean())
                        {
                            var fontConfigLength = reader.ReadUInt32();
                            // We don't use font_config, so we just skip it.
                            reader.BaseStream.Seek(fontConfigLength, SeekOrigin.Current);
                        }
                        hashBits = 40;
                        break;
                    case 3:
                        hashBits = 40;
                        break;
                    case 4:
                    case 5:
                        hashBits = 39;
                        if (gameVersion >= 1502)
                        {
                            hashBits = 38;
                        }
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported RST version: {version}");
                }

                ulong hashMask = (1UL << hashBits) - 1;
                var count = reader.ReadUInt32();
                var entryInfos = new List<(long offset, ulong hash)>();

                for (int i = 0; i < count; i++)
                {
                    var value = reader.ReadUInt64();
                    var offset = value >> hashBits;
                    var hash = value & hashMask;
                    entryInfos.Add(((long)offset, hash));
                }

                bool hasTrenc = false;
                if (version < 5)
                {
                    hasTrenc = reader.ReadBoolean();
                }

                // The rest of the stream is the data block
                long dataOffset = reader.BaseStream.Position;
                byte[] data = reader.ReadBytes((int)(reader.BaseStream.Length - dataOffset));
                
                foreach (var (offset, hash) in entryInfos)
                {
                    // Adjust offset to be relative to the start of the data block
                    long relativeOffset = offset - dataOffset;

                    if (relativeOffset < 0 || relativeOffset >= data.Length)
                    {
                        // Invalid offset, skip this entry
                        continue;
                    }

                    if (hasTrenc && data[relativeOffset] == 0xFF)
                    {
                        int size = BitConverter.ToUInt16(data, (int)relativeOffset + 1);
                        byte[] base64Data = new byte[size];
                        Array.Copy(data, (int)relativeOffset + 3, base64Data, 0, size);
                        entries[hash] = Convert.ToBase64String(base64Data);
                    }
                    else
                    {
                        int end = Array.IndexOf(data, (byte)0, (int)relativeOffset);
                        if (end == -1)
                        {
                            end = data.Length;
                        }
                        string text = Encoding.UTF8.GetString(data, (int)relativeOffset, end - (int)relativeOffset);
                        entries[hash] = text;
                    }
                }
            }
            return (entries, hashBits);
        }
    }
}
