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
                    // Mejora: Unwrap directo sobre el Span.
                    return _zstdDecompressor.Value.Unwrap(compressedData).ToArray();

                case WadChunkCompression.GZip:
                    // Para GZip, convertimos el Span a array (necesario para MemoryStream)
                    using (var compressedStream = new MemoryStream(compressedData.ToArray()))
                    using (var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress))
                    using (var decompressedStream = new MemoryStream())
                    {
                        gzipStream.CopyTo(decompressedStream);
                        return decompressedStream.ToArray();
                    }

                default:
                    throw new NotSupportedException($"Compression type {compressionType} is not supported for decompression.");
            }
        }

        // Sobrecarga para mantener compatibilidad con byte[]
        public static byte[] DecompressChunk(byte[] compressedData, WadChunkCompression? compressionType)
            => DecompressChunk(compressedData.AsSpan(), compressionType);
    }
}
