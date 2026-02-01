using System.Collections.Generic;
using System.Linq;

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

    private Dictionary<ulong, RmanChunk> _chunkLookup;
    public RmanChunk GetChunk(ulong chunkId)
    {
        if (_chunkLookup == null)
        {
            _chunkLookup = new Dictionary<ulong, RmanChunk>();
            foreach (var bundle in Bundles)
            {
                foreach (var chunk in bundle.Chunks)
                {
                    _chunkLookup[chunk.ChunkId] = chunk;
                }
            }
        }
        return _chunkLookup.ContainsKey(chunkId) ? _chunkLookup[chunkId] : null;
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
}
