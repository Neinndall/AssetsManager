using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json.Serialization;
using AssetsManager.Utils;
using LeagueToolkit.Core.Wad;
using AssetsManager.Views.Helpers;

namespace AssetsManager.Views.Models.Wad
{
    public enum ChunkDiffType
    {
        New,
        Removed,
        Modified,
        Renamed,
        Dependency
    }

    public class ChunkDiff
    {
        public ChunkDiffType Type { get; set; }
        public WadChunk OldChunk { get; set; }
        public WadChunk NewChunk { get; set; }
        public string OldPath { get; set; }
        public string NewPath { get; set; }
        public string SourceWadFile { get; set; }

        // Transient audit field populated during comparison when the diff is an
        // audio bank. Holds a human-readable summary of HIRC changes (e.g.
        // "Events: 142 → 143 (+1), WEMs: 87 → 88 (+1)"). Not serialized to the
        // JSON index — used solely for the post-comparison audit log.
        public string HircDiffSummary { get; set; }
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

    public class SerializableChunkDiff : INotifyPropertyChanged, IMultiSelectable
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private bool _isSelected;
        private bool _isMultiSelected;

        [JsonIgnore]
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
        }

        [JsonIgnore]
        public bool IsMultiSelected
        {
            get => _isMultiSelected;
            set { if (_isMultiSelected != value) { _isMultiSelected = value; OnPropertyChanged(); } }
        }

        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public ChunkDiffType Type { get; set; }
        public string OldPath { get; set; }
        public string NewPath { get; set; }
        public string SourceWadFile { get; set; }
        public ulong? OldUncompressedSize { get; set; }
        public ulong? NewUncompressedSize { get; set; }
        public ulong OldPathHash { get; set; }
        public ulong NewPathHash { get; set; }
        [JsonIgnore]
        public string Path => NewPath ?? OldPath;

        [JsonIgnore]
        public string FileName => System.IO.Path.GetFileName(Path);

        [JsonIgnore]
        public string DisplayPath
        {
            get
            {
                string dir = System.IO.Path.GetDirectoryName(Path);
                return string.IsNullOrEmpty(dir) ? "N/A" : dir;
            }
        }

        [JsonIgnore]
        public string OldSizeString => OldUncompressedSize.HasValue ? FormatUtils.FormatSize((long)OldUncompressedSize.Value) : "N/A";
        
        [JsonIgnore]
        public string NewSizeString => NewUncompressedSize.HasValue ? FormatUtils.FormatSize((long)NewUncompressedSize.Value) : "N/A";

        [JsonIgnore]
        public string SizeDifferenceString
        {
            get
            {
                if (Type == ChunkDiffType.Dependency) return "N/A";

                long diff = Type switch
                {
                    ChunkDiffType.New => (long)(NewUncompressedSize ?? 0),
                    ChunkDiffType.Removed => -(long)(OldUncompressedSize ?? 0),
                    ChunkDiffType.Modified => (long)(NewUncompressedSize ?? 0) - (long)(OldUncompressedSize ?? 0),
                    ChunkDiffType.Renamed => (long)(NewUncompressedSize ?? 0) - (long)(OldUncompressedSize ?? 0),
                    _ => 0
                };

                if (diff == 0)
                {
                    return (Type == ChunkDiffType.Modified || Type == ChunkDiffType.Renamed) ? "±0 B" : "0 B";
                }
                
                return (diff > 0 ? "+" : "-") + FormatUtils.FormatSize(Math.Abs(diff));
            }
        }

        public WadChunkCompression? OldCompressionType { get; set; }
        public WadChunkCompression? NewCompressionType { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<AssociatedDependency> Dependencies { get; set; }

        [JsonIgnore]
        public string BackupChunkPath { get; set; }

        private System.Windows.Media.ImageSource _imagePreview;
        [JsonIgnore]
        public System.Windows.Media.ImageSource ImagePreview
        {
            get => _imagePreview;
            set { if (_imagePreview != value) { _imagePreview = value; OnPropertyChanged(); } }
        }
    }

    public class WadComparisonData
    {
        public string OldLolPath { get; set; }
        public string NewLolPath { get; set; }
        public string Version { get; set; }
        public List<SerializableChunkDiff> Diffs { get; set; }
    }
}
