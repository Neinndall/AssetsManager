using System;
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
                if (Type == ChunkDiffType.Renamed || Type == ChunkDiffType.Dependency) return "N/A";

                long diff = Type switch
                {
                    ChunkDiffType.New => (long)(NewUncompressedSize ?? 0),
                    ChunkDiffType.Removed => -(long)(OldUncompressedSize ?? 0),
                    ChunkDiffType.Modified => (long)(NewUncompressedSize ?? 0) - (long)(OldUncompressedSize ?? 0),
                    _ => 0
                };

                if (diff == 0 && Type == ChunkDiffType.Modified) return "N/A";
                
                return (diff > 0 ? "+" : "") + FormatSize((ulong)Math.Abs(diff));
            }
        }

        private string FormatSize(ulong? sizeInBytes)
        {
            if (sizeInBytes == null) return "N/A";
            
            if (sizeInBytes < 1024)
            {
                return $"{sizeInBytes} Bytes";
            }

            double sizeInKB = (double)sizeInBytes / 1024.0;
            if (sizeInKB < 1024)
            {
                return $"{sizeInKB:F2} KB";
            }

            double sizeInMB = sizeInKB / 1024.0;
            return $"{sizeInMB:F2} MB";
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
