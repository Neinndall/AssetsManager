using System.Collections.Generic;
using System.Text.Json.Serialization;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Views.Models.Wad
{
    public enum ChunkDiffType
    {
        New,
        Removed,
        Modified,
        Renamed,
        Dependency // Represents a chunk that hasn't changed / an associated dependency
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

    public class AssociatedDependency
    {
        public string Path { get; set; }
        public string SourceWad { get; set; }
        public ulong OldPathHash { get; set; }
        public ulong NewPathHash { get; set; }
        public WadChunkCompression CompressionType { get; set; }
        public ChunkDiffType? Type { get; set; }
        public bool WasTopLevelDiff { get; set; }
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
        public string DisplayPath
        {
            get
            {
                string dir = System.IO.Path.GetDirectoryName(Path);
                return string.IsNullOrEmpty(dir) ? "N/A" : dir;
            }
        }

        [JsonIgnore]
        public string OldSizeString => FormatSize(OldUncompressedSize);
        [JsonIgnore]
        public string NewSizeString => FormatSize(NewUncompressedSize);

        [JsonIgnore]
        public string SizeDifferenceString
        {
            get
            {
                if (Type != ChunkDiffType.Modified || OldUncompressedSize == null || NewUncompressedSize == null)
                    return null;

                long diff = (long)NewUncompressedSize - (long)OldUncompressedSize;
                if (diff == 0) return "No change";
                
                double diffKB = diff / 1024.0;
                string sign = diff > 0 ? "+" : "";
                return $"{sign}{diffKB:F2} KB";
            }
        }

        private string FormatSize(ulong? sizeInBytes)
        {
            if (sizeInBytes == null) return "N/A";
            double sizeInKB = (double)sizeInBytes / 1024.0;
            return $"{sizeInKB:F2} KB";
        }

        public WadChunkCompression? OldCompressionType { get; set; }
        public WadChunkCompression? NewCompressionType { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<AssociatedDependency> Dependencies { get; set; }

        [JsonIgnore]
        public string BackupChunkPath { get; set; }
    }

    public class WadComparisonData
    {
        public string OldLolPath { get; set; }
        public string NewLolPath { get; set; }
        public List<SerializableChunkDiff> Diffs { get; set; }
    }
}
