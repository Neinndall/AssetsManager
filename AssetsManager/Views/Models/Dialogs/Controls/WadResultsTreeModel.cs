using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel; // Added for INotifyPropertyChanged
using System.Runtime.CompilerServices; // Added for CallerMemberName
using AssetsManager.Views.Models.Wad;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Helpers;
using Material.Icons;

namespace AssetsManager.Views.Models.Dialogs.Controls
{
    /// <summary>
    /// Model for the results data, including the hierarchy and analytical insights.
    /// </summary>
    public class WadResultsTreeModel : INotifyPropertyChanged
    {
        // 1. Data Hierarchy (The Tree)
        public ObservableRangeCollection<WadGroupViewModel> WadGroups { get; set; } = new ObservableRangeCollection<WadGroupViewModel>();

        // 2. Analytical Insights (The Dashboard)
        public ObservableRangeCollection<AssetCategoryStats> CategoryDistribution { get; } = new ObservableRangeCollection<AssetCategoryStats>();
        public ObservableRangeCollection<TopImpactFile> TopImpactFiles { get; } = new ObservableRangeCollection<TopImpactFile>();
        public ObservableRangeCollection<TopWadImpact> TopWadPackages { get; } = new ObservableRangeCollection<TopWadImpact>();
        public ObservableRangeCollection<PatchAreaStats> FeatureAreas { get; } = new ObservableRangeCollection<PatchAreaStats>();

        // Metrics & KPI
        private string _addedPayloadText = "+0 B";
        public string AddedPayloadText
        {
            get => _addedPayloadText;
            set { _addedPayloadText = value; OnPropertyChanged(); }
        }

        private string _removedPayloadText = "-0 B";
        public string RemovedPayloadText
        {
            get => _removedPayloadText;
            set { _removedPayloadText = value; OnPropertyChanged(); }
        }

        private string _netSizeChangeText = "0 B";
        public string NetSizeChangeText
        {
            get => _netSizeChangeText;
            set { _netSizeChangeText = value; OnPropertyChanged(); }
        }

        private int _unknownHashesCount;
        public int UnknownHashesCount
        {
            get => _unknownHashesCount;
            set { _unknownHashesCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasUnknownHashes)); }
        }

        public bool HasUnknownHashes => _unknownHashesCount > 0;
        
        // --- Surgical Filtering States ---
        private bool _showNew = true;
        private bool _showModified = true;
        private bool _showRemoved = true;
        private bool _showRenamed = true;

        public bool ShowNew
        {
            get => _showNew;
            set { if (_showNew != value) { _showNew = value; OnPropertyChanged(); OnFilterChanged(); } }
        }

        public bool ShowModified
        {
            get => _showModified;
            set { if (_showModified != value) { _showModified = value; OnPropertyChanged(); OnFilterChanged(); } }
        }

        public bool ShowRemoved
        {
            get => _showRemoved;
            set { if (_showRemoved != value) { _showRemoved = value; OnPropertyChanged(); OnFilterChanged(); } }
        }

        public bool ShowRenamed
        {
            get => _showRenamed;
            set { if (_showRenamed != value) { _showRenamed = value; OnPropertyChanged(); OnFilterChanged(); } }
        }

        public event EventHandler FilterChanged;
        private void OnFilterChanged() => FilterChanged?.Invoke(this, EventArgs.Empty);

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    }

    public class WadGroupViewModel : INotifyPropertyChanged, ISelectableTreeNode
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private bool _isSelected;
        private bool _isMultiSelected;
        private bool _isExpanded;

        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
        }

        public bool IsMultiSelected
        {
            get => _isMultiSelected;
            set { if (_isMultiSelected != value) { _isMultiSelected = value; OnPropertyChanged(); } }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
        }

        System.Collections.IEnumerable ISelectableTreeNode.SelectionChildren => Types;
        bool ISelectableTreeNode.IsSelectionVisible => true;

        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public string WadName { get; set; }
        public int DiffCount { get; set; }
        private string _wadNameWithCount;
        public string WadNameWithCount => _wadNameWithCount ?? (_wadNameWithCount = $"{WadName} ({DiffCount})");
        public ObservableRangeCollection<DiffTypeGroupViewModel> Types { get; set; } = new ObservableRangeCollection<DiffTypeGroupViewModel>();
    }

    public class DiffTypeGroupViewModel : INotifyPropertyChanged, ISelectableTreeNode
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private bool _isSelected;
        private bool _isMultiSelected;
        private bool _isExpanded;

        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
        }

        public bool IsMultiSelected
        {
            get => _isMultiSelected;
            set { if (_isMultiSelected != value) { _isMultiSelected = value; OnPropertyChanged(); } }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
        }

        System.Collections.IEnumerable ISelectableTreeNode.SelectionChildren => Diffs;
        bool ISelectableTreeNode.IsSelectionVisible => true;

        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public ChunkDiffType Type { get; set; }
        public int DiffCount { get; set; }
        private string _typeNameWithCount;
        public string TypeNameWithCount => _typeNameWithCount ?? (_typeNameWithCount = $"{Type} ({DiffCount})");
        public ObservableRangeCollection<SerializableChunkDiff> Diffs { get; set; } = new ObservableRangeCollection<SerializableChunkDiff>();
    }

    public class AssetCategoryStats
    {
        public string Name { get; set; }
        public int Count { get; set; }
        public double Percentage { get; set; }
        public long TotalSizeChange { get; set; }
        public string ExtensionFilter { get; set; }
        public MaterialIconKind IconKind { get; set; }
        private string _sizeChangeText;
        public string SizeChangeText => _sizeChangeText ?? (_sizeChangeText = (TotalSizeChange >= 0 ? "+" : "") + FormatUtils.FormatSize(Math.Abs(TotalSizeChange)));
    }

    public class TopImpactFile
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public ChunkDiffType Type { get; set; }
        public ulong OldSize { get; set; }
        public ulong NewSize { get; set; }
        public long SizeDiff { get; set; }
        public SerializableChunkDiff Diff { get; set; }
        public MaterialIconKind IconKind { get; set; }
        private string _sizeDiffText;
        public string SizeDiffText => _sizeDiffText ?? (_sizeDiffText = (SizeDiff >= 0 ? "+" : "") + FormatUtils.FormatSize(Math.Abs(SizeDiff)));
    }

    public class TopWadImpact
    {
        public string WadName { get; set; }
        public int Count { get; set; }
        public double Percentage { get; set; }
        public long TotalSizeChange { get; set; }
        private string _sizeChangeText;
        public string SizeChangeText => _sizeChangeText ?? (_sizeChangeText = (TotalSizeChange >= 0 ? "+" : "") + FormatUtils.FormatSize(Math.Abs(TotalSizeChange)));
    }

    public class PatchAreaStats
    {
        public string Name { get; set; }
        public MaterialIconKind IconKind { get; set; }
        public string ColorBrushKey { get; set; }
        public int Count { get; set; }
        public long TotalSizeChange { get; set; }
        public string FilterQuery { get; set; }
        private string _sizeChangeText;
        public string SizeChangeText => _sizeChangeText ?? (_sizeChangeText = (TotalSizeChange >= 0 ? "+" : "") + FormatUtils.FormatSize(Math.Abs(TotalSizeChange)));
    }
}
