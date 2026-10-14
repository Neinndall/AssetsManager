using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AssetsManager.Views.Models.Monitor;

public enum HashType : byte
{
    Sha512 = 1,
    Sha256 = 2,
    Hkdf = 3,
    Blake3 = 4
}

public class RmanManifest
{
    public ulong ManifestId { get; set; }
    public List<RmanBundle> Bundles { get; set; } = new();
    public List<RmanLanguage> Languages { get; set; } = new();
    public List<RmanFile> Files { get; set; } = new();
    public List<RmanDirectory> Directories { get; set; } = new();

    private readonly object _lookupLock = new();
    private Dictionary<ulong, RmanChunk> _chunkLookup;

    public void BuildChunkLookup()
    {
        if (Volatile.Read(ref _chunkLookup) != null) return;

        lock (_lookupLock)
        {
            if (_chunkLookup != null) return;

            int chunkCount = 0;
            for (int i = 0; i < Bundles.Count; i++) chunkCount += Bundles[i].Chunks.Count;

            var lookup = new Dictionary<ulong, RmanChunk>(chunkCount);
            for (int i = 0; i < Bundles.Count; i++)
            {
                var bundle = Bundles[i];
                for (int j = 0; j < bundle.Chunks.Count; j++)
                {
                    var chunk = bundle.Chunks[j];
                    if (!lookup.TryAdd(chunk.ChunkId, chunk)
                        && lookup[chunk.ChunkId].UncompressedSize != chunk.UncompressedSize)
                    {
                        throw new InvalidDataException(
                            $"Chunk {chunk.ChunkId:X16} has inconsistent uncompressed sizes across bundles.");
                    }
                }
            }
            Volatile.Write(ref _chunkLookup, lookup);
        }
    }

    public RmanChunk GetChunk(ulong chunkId)
    {
        Dictionary<ulong, RmanChunk> lookup = Volatile.Read(ref _chunkLookup);
        if (lookup == null)
        {
            BuildChunkLookup();
            lookup = Volatile.Read(ref _chunkLookup);
        }
        return lookup.TryGetValue(chunkId, out var chunk) ? chunk : null;
    }
}

public class RmanBundle
{
    public ulong BundleId { get; set; }
    public List<RmanChunk> Chunks { get; set; } = new();
}

public class RmanChunk
{
    public ulong ChunkId { get; set; }
    public ulong BundleId { get; set; }
    public uint CompressedSize { get; set; }
    public uint UncompressedSize { get; set; }
    public uint BundleOffset { get; set; }
    public ulong FileOffset { get; set; }
}

public class RmanFile
{
    public ulong FileId { get; set; }
    public string Name { get; set; } = string.Empty;
    public ulong FileSize { get; set; }
    public ulong DirectoryId { get; set; }
    public HashType HashType { get; set; } = HashType.Sha256;
    public List<byte> LanguageIds { get; set; } = new();
    public List<ulong> ChunkIds { get; set; } = new();
}

public class RmanDirectory
{
    public ulong DirectoryId { get; set; }
    public ulong ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class RmanLanguage
{
    public byte LanguageId { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class RiotVersionInfo
{
    public string Product { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ManifestUrl { get; set; } = string.Empty;
    public DateTimeOffset? CreatedAt { get; set; }
}
