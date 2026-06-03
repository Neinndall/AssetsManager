using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json.Serialization;
using AssetsManager.Utils;
using LeagueToolkit.Core.Wad;

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

    public class SerializableChunkDiff : INotifyPropertyChanged
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
        private string _fileName;
        [JsonIgnore]
        public string FileName => _fileName ?? (_fileName = System.IO.Path.GetFileName(Path));

        [JsonIgnore]
        private string _displayPath;
        [JsonIgnore]
        public string DisplayPath
        {
            get
            {
                if (_displayPath == null)
                {
                    string dir = System.IO.Path.GetDirectoryName(Path);
                    _displayPath = string.IsNullOrEmpty(dir) ? "N/A" : dir;
                }
                return _displayPath;
            }
        }

        [JsonIgnore]
        private string _oldSizeString;
        [JsonIgnore]
        public string OldSizeString => _oldSizeString ??= OldUncompressedSize.HasValue ? FormatUtils.FormatSize((long)OldUncompressedSize.Value) : "N/A";
        
        [JsonIgnore]
        private string _newSizeString;
        [JsonIgnore]
        public string NewSizeString => _newSizeString ??= NewUncompressedSize.HasValue ? FormatUtils.FormatSize((long)NewUncompressedSize.Value) : "N/A";

        [JsonIgnore]
        private string _sizeDifferenceString;
        [JsonIgnore]
        public string SizeDifferenceString
        {
            get
            {
                if (_sizeDifferenceString != null) return _sizeDifferenceString;

                if (Type == ChunkDiffType.Dependency) { _sizeDifferenceString = "N/A"; return _sizeDifferenceString; }

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
                    _sizeDifferenceString = (Type == ChunkDiffType.Modified || Type == ChunkDiffType.Renamed) ? "±0 B" : "0 B";
                    return _sizeDifferenceString;
                }
                
                _sizeDifferenceString = (diff > 0 ? "+" : "-") + FormatUtils.FormatSize(Math.Abs(diff));
                return _sizeDifferenceString;
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
        public List<SerializableChunkDiff> Diffs { get; set; }
    }
}
