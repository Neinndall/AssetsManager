using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Views.Models.Dialogs.Controls;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Views.Models.Dialogs
{
    public enum ComparisonLoadingState
    {
        Idle,
        ResolvingHashes,
        ReloadingHashes,
        Ready
    }

    /// <summary>
    /// Master model for the Comparison Results window. Orchestrates sub-models and global state.
    /// </summary>
    public class WadComparisonResultModel : INotifyPropertyChanged
    {
        private bool _isLoading;
        private string _summaryText = "Analyzing differences...";
        private string _countNew = "0";
        private string _countModified = "0";
        private string _countRemoved = "0";
        private string _countRenamed = "0";

        // Sub-Models (Encapsulated responsibilities)
        public WadResultsTreeModel TreeModel { get; } = new WadResultsTreeModel();

        private WadDiffDetailsModel _detailsModel;
        public WadDiffDetailsModel DetailsModel
        {
            get => _detailsModel;
            set { _detailsModel = value; OnPropertyChanged(); }
        }

        private bool _isDashboardVisible;
        public bool IsDashboardVisible
        {
            get => _isDashboardVisible;
            set { _isDashboardVisible = value; OnPropertyChanged(); }
        }

        public WadComparisonResultModel()
        {
            TreeModel.PropertyChanged += (sender, e) =>
            {
                if (e.PropertyName == nameof(TreeModel.DashboardToggleChecked))
                {
                    IsDashboardVisible = TreeModel.DashboardToggleChecked;
                }
            };
            IsDashboardVisible = TreeModel.DashboardToggleChecked; // Set initial state
        }



        // Window Global State
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
                    IsLoading = false;
                    SummaryText = "Ready";
                    break;
                case ComparisonLoadingState.ResolvingHashes:
                    IsLoading = true;
                    SummaryText = "Resolving hashes and building result tree...";
                    break;
                case ComparisonLoadingState.ReloadingHashes:
                    IsLoading = true;
                    SummaryText = "Force reloading hash databases...";
                    break;
                case ComparisonLoadingState.Ready:
                    IsLoading = false;
                    break;
            }
        }

        public void SetResults(List<SerializableChunkDiff> diffs, List<WadGroupViewModel> groups)
        {
            SetLoadingState(ComparisonLoadingState.Ready);
            
            // 1. Update the Results Model (The Tree)
            TreeModel.WadGroups.ReplaceRange(groups);
            
            // 2. Update Global Stats
            SummaryText = $"Found {diffs.Count} differences across {groups.Count} WAD files.";
            CountNew = diffs.Count(d => d.Type == ChunkDiffType.New).ToString();
            CountModified = diffs.Count(d => d.Type == ChunkDiffType.Modified).ToString();
            CountRemoved = diffs.Count(d => d.Type == ChunkDiffType.Removed).ToString();
            CountRenamed = diffs.Count(d => d.Type == ChunkDiffType.Renamed).ToString();

            // 3. Perform Analysis (Populates Dashboard in TreeModel)
            CalculateInsights(diffs);
        }

        private void CalculateInsights(List<SerializableChunkDiff> diffs)
        {
            // Category Analysis
            TreeModel.CategoryDistribution.Clear();
            var categories = new Dictionary<string, (int Count, long Size)>
            {
                { "Audio", (0, 0) },
                { "Images", (0, 0) },
                { "Models/3D", (0, 0) },
                { "Data/Bin", (0, 0) },
                { "Other", (0, 0) }
            };

            foreach (var diff in diffs)
            {
                string ext = System.IO.Path.GetExtension(diff.Path).ToLower();
                string cat = "Other";
                
                if (ext == ".wem" || ext == ".bnk" || ext == ".wpk") cat = "Audio";
                else if (ext == ".dds" || ext == ".png" || ext == ".tex" || ext == ".tga") cat = "Images";
                else if (ext == ".skn" || ext == ".skl" || ext == ".anm" || ext == ".sco" || ext == ".scb") cat = "Models/3D";
                else if (ext == ".bin" || ext == ".json" || ext == ".txt" || ext == ".stringtable") cat = "Data/Bin";

                long sizeChange = (long)(diff.NewUncompressedSize ?? 0) - (long)(diff.OldUncompressedSize ?? 0);
                if (diff.Type == ChunkDiffType.New) sizeChange = (long)(diff.NewUncompressedSize ?? 0);
                if (diff.Type == ChunkDiffType.Removed) sizeChange = -(long)(diff.OldUncompressedSize ?? 0);

                var current = categories[cat];
                categories[cat] = (current.Count + 1, current.Size + sizeChange);
            }

            int total = diffs.Count;
            if (total > 0)
            {
                var statsToAdd = categories
                    .Where(c => c.Value.Count > 0)
                    .OrderByDescending(c => c.Value.Count)
                    .Select(cat => new AssetCategoryStats 
                    { 
                        Name = cat.Key, 
                        Count = cat.Value.Count,
                        Percentage = (double)cat.Value.Count / total * 100,
                        TotalSizeChange = cat.Value.Size
                    });
                
                TreeModel.CategoryDistribution.ReplaceRange(statsToAdd);
            }
            else
            {
                TreeModel.CategoryDistribution.Clear();
            }

            // Top Impact Analysis
            var topFiles = diffs
                .Where(d => d.OldUncompressedSize != null || d.NewUncompressedSize != null)
                .Select(d => new TopImpactFile
                {
                    Name = d.FileName,
                    Path = d.Path,
                    Type = d.Type,
                    OldSize = d.OldUncompressedSize ?? 0,
                    NewSize = d.NewUncompressedSize ?? 0,
                    SizeDiff = (long)(d.NewUncompressedSize ?? 0) - (long)(d.OldUncompressedSize ?? 0)
                })
                .OrderByDescending(f => Math.Abs(f.SizeDiff))
                .Take(5)
                .ToList();

            TreeModel.TopImpactFiles.ReplaceRange(topFiles);

            // Area Analysis
            var areas = diffs
                .Select(d => {
                    var parts = d.Path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                    return parts.Length > 0 ? parts[0].ToUpper() : "ROOT";
                })
                .GroupBy(a => a)
                .Select(g => new AffectedArea { Name = g.Key, Count = g.Count() })
                .OrderByDescending(a => a.Count)
                .Take(6)
                .ToList();

            TreeModel.AffectedAreas.ReplaceRange(areas);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}