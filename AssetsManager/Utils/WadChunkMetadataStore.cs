using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Utils
{
    public static class WadChunkMetadataStore
    {
        private const uint Magic = 0x43534D41; // AMSC
        private const byte Version = 1;

        public static async Task WriteAsync(
            string chunkPath,
            WadFile wad,
            WadChunk chunk,
            CancellationToken cancellationToken)
        {
            if (chunk.Compression != WadChunkCompression.ZstdChunked || chunk.SubChunkCount == 0) return;

            WadSubchunk[] subchunks = wad.Subchunks.Span
                .Slice(chunk.StartSubChunk, chunk.SubChunkCount)
                .ToArray();

            await WriteAsync(chunkPath, chunk.CompressedSize, chunk.UncompressedSize, subchunks, cancellationToken);
        }

        public static async Task WriteAsync(
            string chunkPath,
            int compressedSize,
            int uncompressedSize,
            IReadOnlyList<WadSubchunk> subchunks,
            CancellationToken cancellationToken)
        {
            if (subchunks == null || subchunks.Count == 0 || subchunks.Count > 15)
                throw new InvalidDataException("Invalid ZstdChunked subchunk table.");

            using var buffer = new MemoryStream(20 + (subchunks.Count * 8));
            using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write((byte)WadChunkCompression.ZstdChunked);
                writer.Write((ushort)0);
                writer.Write(compressedSize);
                writer.Write(uncompressedSize);
                writer.Write(subchunks.Count);
                foreach (var subchunk in subchunks)
                {
                    writer.Write(subchunk.CompressedSize);
                    writer.Write(subchunk.UncompressedSize);
                }
            }

            await File.WriteAllBytesAsync(GetPath(chunkPath), buffer.ToArray(), cancellationToken);
        }

        public static bool TryRead(
            string chunkPath,
            out int uncompressedSize,
            out WadSubchunk[] subchunks)
        {
            uncompressedSize = 0;
            subchunks = null;
            string metadataPath = GetPath(chunkPath);
            if (!File.Exists(metadataPath)) return false;

            using var stream = File.OpenRead(metadataPath);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != Magic || reader.ReadByte() != Version)
                throw new InvalidDataException($"Invalid WAD chunk metadata header: '{metadataPath}'.");
            if ((WadChunkCompression)reader.ReadByte() != WadChunkCompression.ZstdChunked)
                throw new InvalidDataException($"Unsupported WAD chunk metadata compression: '{metadataPath}'.");
            reader.ReadUInt16();
            int compressedSize = reader.ReadInt32();
            uncompressedSize = reader.ReadInt32();
            int count = reader.ReadInt32();
            if (compressedSize != new FileInfo(chunkPath).Length || uncompressedSize <= 0 || count <= 0 || count > 15)
            {
                throw new InvalidDataException($"WAD chunk metadata does not match its archived payload: '{metadataPath}'.");
            }

            var result = new WadSubchunk[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = new WadSubchunk(reader.ReadInt32(), reader.ReadInt32());
            }

            if (stream.Position != stream.Length)
                throw new InvalidDataException($"Unexpected trailing data in WAD chunk metadata: '{metadataPath}'.");

            subchunks = result;
            return true;
        }

        public static bool TryRecover(
            string sourceRoot,
            string relativeWadPath,
            ulong hash,
            string archivedChunkPath,
            int? expectedUncompressedSize,
            out int uncompressedSize,
            out WadSubchunk[] subchunks)
        {
            uncompressedSize = 0;
            subchunks = null;
            if (string.IsNullOrWhiteSpace(sourceRoot)
                || string.IsNullOrWhiteSpace(relativeWadPath)
                || hash == 0
                || !File.Exists(archivedChunkPath))
            {
                return false;
            }

            string sourceWadPath = SupportedFileTypes.IsWadFile(sourceRoot)
                ? sourceRoot
                : Path.Combine(sourceRoot, relativeWadPath);
            if (!File.Exists(sourceWadPath)) return false;

            using var wad = new WadFile(sourceWadPath);
            if (!wad.Chunks.TryGetValue(hash, out var chunk)
                || chunk.Compression != WadChunkCompression.ZstdChunked
                || chunk.SubChunkCount == 0
                || new FileInfo(archivedChunkPath).Length != chunk.CompressedSize
                || (expectedUncompressedSize.HasValue && expectedUncompressedSize.Value != chunk.UncompressedSize))
            {
                return false;
            }

            WadSubchunk[] recovered = wad.Subchunks.Span
                .Slice(chunk.StartSubChunk, chunk.SubChunkCount)
                .ToArray();

            uncompressedSize = chunk.UncompressedSize;
            subchunks = recovered;
            return true;
        }

        private static string GetPath(string chunkPath) => chunkPath + ".meta";
    }
}
