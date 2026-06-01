using System;
using System.IO;
using System.IO.Compression;
using LeagueToolkit.Core.Wad;
using ZstdSharp;
using System.Threading;

namespace AssetsManager.Utils
{
    public static class WadChunkUtils
    {
        // Reutilizamos el descompresor para evitar instanciaciones masivas en el GC (Thread-safe)
        private static readonly ThreadLocal<Decompressor> _zstdDecompressor = new(() => new Decompressor());

        public static byte[] DecompressChunk(ReadOnlySpan<byte> compressedData, WadChunkCompression? compressionType)
        {
            if (compressionType == null || compressionType == WadChunkCompression.None)
            {
                return compressedData.ToArray();
            }

            switch (compressionType)
            {
                case WadChunkCompression.ZstdChunked:
                case WadChunkCompression.Zstd:
                    return _zstdDecompressor.Value.Unwrap(compressedData).ToArray();

                case WadChunkCompression.GZip:
                    using (var decompressedStream = new MemoryStream())
                    {
                        using (var compressedStream = new MemoryStream(compressedData.ToArray()))
                        using (var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress))
                        {
                            gzipStream.CopyTo(decompressedStream);
                        }
                        return decompressedStream.ToArray();
                    }

                default:
                    throw new NotSupportedException($"Compression type {compressionType} is not supported for decompression.");
            }
        }

        public static byte[] DecompressChunk(byte[] compressedData, WadChunkCompression? compressionType)
            => DecompressChunk(compressedData.AsSpan(), compressionType);

        // Reads the WAD file header to extract the chunk count without instantiating WadFile.
        // Delegates to LeagueToolkit's WadFile.GetChunkCount - single source of truth for the
        // WAD format. Kept as a wrapper to preserve call sites and to allow future fallback logic
        // if the library API ever changes.
        public static int ReadWadChunkCount(string wadFilePath) => WadFile.GetChunkCount(wadFilePath);
    }
}
