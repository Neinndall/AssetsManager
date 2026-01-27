using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AssetsManager.Views.Models.Wad;

namespace AssetsManager.Views.Models.Wad
{
    public class WadComparisonResultModel : INotifyPropertyChanged
    {
        private bool _isLoading;
        private string _summaryText = "Analyzing differences...";
        private ObservableCollection<WadGroupViewModel> _wadGroups;
        
        // Stats counters
        private string _countNew = "0";
        private string _countModified = "0";
        private string _countRemoved = "0";
        private string _countRenamed = "0";

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        public string SummaryText
        {
            get => _summaryText;
            set { _summaryText = value; OnPropertyChanged(); }
        }

        public ObservableCollection<WadGroupViewModel> WadGroups
        {
            get => _wadGroups;
            set { _wadGroups = value; OnPropertyChanged(); }
        }

        public string CountNew
        {
            get => _countNew;
            set { _countNew = value; OnPropertyChanged(); }
        }

        public string CountModified
        {
            get => _countModified;
            set { _countModified = value; OnPropertyChanged(); }
        }

        public string CountRemoved
        {
            get => _countRemoved;
            set { _countRemoved = value; OnPropertyChanged(); }
        }

        public string CountRenamed
        {
            get => _countRenamed;
            set { _countRenamed = value; OnPropertyChanged(); }
        }

        public void SetResults(List<SerializableChunkDiff> diffs, List<WadGroupViewModel> groups)
        {
            WadGroups = new ObservableCollection<WadGroupViewModel>(groups);
            SummaryText = $"Found {diffs.Count} differences across {groups.Count} WAD files.";
            
            CountNew = diffs.Count(d => d.Type == ChunkDiffType.New).ToString();
            CountModified = diffs.Count(d => d.Type == ChunkDiffType.Modified).ToString();
            CountRemoved = diffs.Count(d => d.Type == ChunkDiffType.Removed).ToString();
            CountRenamed = diffs.Count(d => d.Type == ChunkDiffType.Renamed).ToString();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class WadGroupViewModel
    {
        public string WadName { get; set; }
        public int DiffCount { get; set; }
        public string WadNameWithCount => $"{WadName} ({DiffCount})";
        public List<DiffTypeGroupViewModel> Types { get; set; }
    }

    public class DiffTypeGroupViewModel
    {
        public ChunkDiffType Type { get; set; }
        public int DiffCount { get; set; }
        public string TypeNameWithCount => $"{Type} ({DiffCount})";
        public List<SerializableChunkDiff> Diffs { get; set; }
    }
}