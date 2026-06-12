using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using AssetsManager.Services.Hashes;

namespace AssetsManager.Services.Parsers
{
    public class StringTableParser
    {
        private readonly HashResolverService _hashResolver;

        public StringTableParser(HashResolverService hashResolver)
        {
            _hashResolver = hashResolver;
        }

        // Record para encapsular los datos parseados y resueltos
        private record ParsedStringTableData(
            Dictionary<ulong, string> RstEntries,
            Dictionary<ulong, string> TruncatedLut,
            int HashBits,
            int Version
        );

        // Método auxiliar para extraer la lógica común de parseo y resolución de hashes
        private ParsedStringTableData GetParsedStringTableData(Stream stream, int gameVersion)
        {
            var (rstEntries, hashBits, fileVersion) = Parse(stream, gameVersion);

            var referenceHashes = (gameVersion >= 1415)
                ? _hashResolver.RstXxh3Hashes
                : _hashResolver.RstXxh64Hashes;

            var truncatedLut = new Dictionary<ulong, string>();
            ulong hashMask = (1UL << hashBits) - 1;
            foreach (var pair in referenceHashes)
            {
                truncatedLut[pair.Key & hashMask] = pair.Value;
            }
            return new ParsedStringTableData(rstEntries, truncatedLut, hashBits, fileVersion);
        }

        public Task WriteStringTableAsJsonAsync(Stream outputStream, Stream inputStream, int gameVersion = 1502)
        {
            return Task.Run(() => WriteStringTableAsJson(outputStream, inputStream, gameVersion));
        }

        private void WriteStringTableAsJson(Stream outputStream, Stream inputStream, int gameVersion = 1502)
        {
            var options = new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            using var writer = new Utf8JsonWriter(outputStream, options);

            var parsedData = GetParsedStringTableData(inputStream, gameVersion);

            writer.WriteStartObject();
            foreach (var entry in parsedData.RstEntries)
            {
                string key;
                if (parsedData.TruncatedLut.TryGetValue(entry.Key, out var resolvedKey))
                {
                    key = resolvedKey;
                }
                else
                {
                    key = $"{{{entry.Key:x10}}}";
                }
                writer.WriteString(key, entry.Value);
            }
            writer.WriteEndObject();
            writer.Flush();
        }

        private (Dictionary<ulong, string> Entries, int HashBits, int Version) Parse(Stream stream, int gameVersion = 1502)
        {
            var entries = new Dictionary<ulong, string>();
            int hashBits;
            int version;
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                var magic = reader.ReadBytes(3);
                if (Encoding.ASCII.GetString(magic) != "RST")
                {
                    throw new InvalidDataException("Invalid magic code. Expected 'RST'.");
                }

                version = reader.ReadByte();

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
                int dataLength = (int)(reader.BaseStream.Length - dataOffset);
                byte[] data = ArrayPool<byte>.Shared.Rent(dataLength);
                try
                {
                    int bytesRead = 0;
                    while (bytesRead < dataLength)
                    {
                        int r = reader.Read(data, bytesRead, dataLength - bytesRead);
                        if (r == 0) break;
                        bytesRead += r;
                    }

                    foreach (var (offset, hash) in entryInfos)
                    {
                        long relativeOffset = (long)offset;

                        if (relativeOffset < 0 || relativeOffset >= dataLength)
                        {
                            // Invalid offset, skip this entry
                            continue;
                        }

                        if (hasTrenc && data[relativeOffset] == 0xFF)
                        {
                            int size = BitConverter.ToUInt16(data, (int)relativeOffset + 1);
                            entries[hash] = Convert.ToBase64String(data, (int)relativeOffset + 3, size);
                        }
                        else
                        {
                            int end = Array.IndexOf(data, (byte)0, (int)relativeOffset, dataLength - (int)relativeOffset);
                            if (end == -1)
                            {
                                end = dataLength;
                            }
                            string text = Encoding.UTF8.GetString(data, (int)relativeOffset, end - (int)relativeOffset);
                            entries[hash] = text;
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(data);
                }
            }
            return (entries, hashBits, version);
        }
    }
}
