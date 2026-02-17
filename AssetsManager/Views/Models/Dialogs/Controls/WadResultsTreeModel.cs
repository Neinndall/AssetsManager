using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Views.Models.Dialogs.Controls
{
    /// <summary>
    /// Model for the results data, including the hierarchy and analytical insights.
    /// </summary>
    public class WadResultsTreeModel
    {
        // 1. Data Hierarchy (The Tree)
        public ObservableRangeCollection<WadGroupViewModel> WadGroups { get; set; } = new ObservableRangeCollection<WadGroupViewModel>();

        // 2. Analytical Insights (The Dashboard)
        public ObservableRangeCollection<AssetCategoryStats> CategoryDistribution { get; } = new ObservableRangeCollection<AssetCategoryStats>();
        public ObservableRangeCollection<TopImpactFile> TopImpactFiles { get; } = new ObservableRangeCollection<TopImpactFile>();
        public ObservableRangeCollection<AffectedArea> AffectedAreas { get; } = new ObservableRangeCollection<AffectedArea>();
    }

    public class WadGroupViewModel
    {
        public string WadName { get; set; }
        public int DiffCount { get; set; }
        public string WadNameWithCount => $"{WadName} ({DiffCount})";
        public ObservableRangeCollection<DiffTypeGroupViewModel> Types { get; set; } = new ObservableRangeCollection<DiffTypeGroupViewModel>();
    }

    public class DiffTypeGroupViewModel
    {
        public ChunkDiffType Type { get; set; }
        public int DiffCount { get; set; }
        public string TypeNameWithCount => $"{Type} ({DiffCount})";
        public ObservableRangeCollection<SerializableChunkDiff> Diffs { get; set; } = new ObservableRangeCollection<SerializableChunkDiff>();
    }

    public class AssetCategoryStats
    {
        public string Name { get; set; }
        public int Count { get; set; }
        public double Percentage { get; set; }
        public long TotalSizeChange { get; set; }
        public string SizeChangeText => (TotalSizeChange >= 0 ? "+" : "") + FormatSize((ulong)Math.Abs(TotalSizeChange));

        private string FormatSize(ulong sizeInBytes)
        {
            if (sizeInBytes < 1024) return $"{sizeInBytes} B";
            double sizeInKB = sizeInBytes / 1024.0;
            if (sizeInKB < 1024) return $"{sizeInKB:F1} KB";
            double sizeInMB = sizeInKB / 1024.0;
            return $"{sizeInMB:F1} MB";
        }
    }

    public class TopImpactFile
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public ChunkDiffType Type { get; set; }
        public ulong OldSize { get; set; }
        public ulong NewSize { get; set; }
        public long SizeDiff { get; set; }
        public string SizeDiffText => (SizeDiff >= 0 ? "+" : "") + FormatSize((ulong)Math.Abs(SizeDiff));

        private string FormatSize(ulong sizeInBytes)
        {
            if (sizeInBytes < 1024) return $"{sizeInBytes} B";
            double sizeInKB = sizeInBytes / 1024.0;
            if (sizeInKB < 1024) return $"{sizeInKB:F1} KB";
            double sizeInMB = sizeInKB / 1024.0;
            return $"{sizeInMB:F1} MB";
        }
    }

    public class AffectedArea
    {
        public string Name { get; set; }
        public int Count { get; set; }
    }
}