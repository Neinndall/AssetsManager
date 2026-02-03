using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Views.Models.Dialogs.Controls;

namespace AssetsManager.Views.Models.Dialogs
{
    public enum ComparisonLoadingState
    {
        Idle,
        ResolvingHashes,
        ReloadingHashes,
        Ready
    }

    public class WadComparisonResultModel : INotifyPropertyChanged
    {
        private bool _isLoading;
        private string _summaryText = "Analyzing differences...";
        private string _countNew = "0";
        private string _countModified = "0";
        private string _countRemoved = "0";
        private string _countRenamed = "0";

        // Nested Tree Model
        public WadResultsTreeModel TreeModel { get; } = new WadResultsTreeModel();

        private SerializableChunkDiff _selectedDiff;
        public SerializableChunkDiff SelectedDiff
        {
            get => _selectedDiff;
            set { _selectedDiff = value; OnPropertyChanged(); }
        }

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

        public void SetLoadingState(ComparisonLoadingState state)
        {
            switch (state)
            {
                case ComparisonLoadingState.Idle:
                    TreeModel.IsBusy = false;
                    SummaryText = "Ready";
                    break;
                case ComparisonLoadingState.ResolvingHashes:
                    TreeModel.IsBusy = true;
                    TreeModel.LoadingText = "BUILDING TREE & DATA";
                    SummaryText = "Resolving hashes and building result tree...";
                    break;
                case ComparisonLoadingState.ReloadingHashes:
                    TreeModel.IsBusy = true;
                    TreeModel.LoadingText = "RELOADING HASHES";
                    SummaryText = "Force reloading hash databases...";
                    break;
                case ComparisonLoadingState.Ready:
                    TreeModel.IsBusy = false;
                    break;
            }
        }

        public void SetResults(List<SerializableChunkDiff> diffs, List<WadGroupViewModel> groups)
        {
            SetLoadingState(ComparisonLoadingState.Ready);
            TreeModel.WadGroups = new ObservableCollection<WadGroupViewModel>(groups);
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