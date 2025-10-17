using System.Collections.Generic;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Views.Models
{
    public enum ChunkDiffType
    {
        New,
        Removed,
        Modified,
        Renamed
    }

    public class ChunkDiff
    {
        public ChunkDiffType Type { get; set; }
        public WadChunk OldChunk { get; set; }
        public WadChunk NewChunk { get; set; }
        public string OldPath { get; set; }
        public string NewPath { get; set; }
        public string SourceWadFile { get; set; }
    }

    public class SerializableChunkDiff                                
    {                                                                 
        public ChunkDiffType Type { get; set; }
        public string OldPath { get; set; }
        public string NewPath { get; set; }
        public string SourceWadFile { get; set; }
        public ulong? OldUncompressedSize { get; set; }
        public ulong? NewUncompressedSize { get; set; }
        public ulong OldPathHash { get; set; }  
        public ulong NewPathHash { get; set; }
        public string Path => NewPath ?? OldPath;
        public string FileName => System.IO.Path.GetFileName(Path);
        
        public WadChunkCompression? OldCompressionType { get; set; }
        public WadChunkCompression? NewCompressionType { get; set; }
    }                                                                 

    public class WadComparisonData
    {
        public string OldLolPath { get; set; }
        public string NewLolPath { get; set; }
        public List<SerializableChunkDiff> Diffs { get; set; }
    }
}
