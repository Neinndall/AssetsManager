using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel; // Added for INotifyPropertyChanged
using System.Runtime.CompilerServices; // Added for CallerMemberName
using AssetsManager.Views.Models.Wad;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;

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
        public ObservableRangeCollection<AffectedArea> AffectedAreas { get; } = new ObservableRangeCollection<AffectedArea>();
        
        // Dashboard Toggle State
        private bool _dashboardToggleChecked = false;
        public bool DashboardToggleChecked
        {
            get => _dashboardToggleChecked;
            set { _dashboardToggleChecked = value; OnPropertyChanged(); }
        }

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

    public class WadGroupViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private bool _isSelected;
        private bool _isMultiSelected;

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

        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public string WadName { get; set; }
        public int DiffCount { get; set; }
        private string _wadNameWithCount;
        public string WadNameWithCount => _wadNameWithCount ?? (_wadNameWithCount = $"{WadName} ({DiffCount})");
        public ObservableRangeCollection<DiffTypeGroupViewModel> Types { get; set; } = new ObservableRangeCollection<DiffTypeGroupViewModel>();
    }

    public class DiffTypeGroupViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private bool _isSelected;
        private bool _isMultiSelected;

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
        private string _sizeDiffText;
        public string SizeDiffText => _sizeDiffText ?? (_sizeDiffText = (SizeDiff >= 0 ? "+" : "") + FormatUtils.FormatSize(Math.Abs(SizeDiff)));
    }

    public class AffectedArea
    {
        public string Name { get; set; }
        public int Count { get; set; }
    }
}