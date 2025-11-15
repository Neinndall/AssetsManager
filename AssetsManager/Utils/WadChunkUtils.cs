using System;
using System.IO;
using System.IO.Compression;
using LeagueToolkit.Core.Wad;
using ZstdSharp;

namespace AssetsManager.Utils
{
    public static class WadChunkUtils
    {
        public static byte[] DecompressChunk(byte[] compressedData, WadChunkCompression? compressionType)
        {
            if (compressionType == null || compressionType == WadChunkCompression.None)
            {
                return compressedData;
            }

            using var compressedStream = new MemoryStream(compressedData);
            using var decompressedStream = new MemoryStream();

            switch (compressionType)
            {
                case WadChunkCompression.GZip:
                    using (var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress))
                    {
                        gzipStream.CopyTo(decompressedStream);
                    }
                    break;
                case WadChunkCompression.ZstdChunked:
                case WadChunkCompression.Zstd:
                    using (var zstdStream = new DecompressionStream(compressedStream))
                    {
                        zstdStream.CopyTo(decompressedStream);
                    }
                    break;
                default:
                    throw new NotSupportedException($"Compression type {compressionType} is not supported for decompression.");
            }

            return decompressedStream.ToArray();
        }
    }
}
