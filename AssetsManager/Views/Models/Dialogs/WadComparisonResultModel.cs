using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Views.Models.Dialogs.Controls;
using AssetsManager.Utils;
using Material.Icons;

namespace AssetsManager.Views.Models.Dialogs
{
    public enum ComparisonViewMode
    {
        Overview,
        Hierarchy,
        Results
    }

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
        private string _resultsSummaryText = string.Empty;
        private string _countNew = "0";
        private string _countModified = "0";
        private string _countRemoved = "0";
        private string _countRenamed = "0";
        private string _filterText = string.Empty;
        private int _totalDiffsCount = -1;
        private ComparisonViewMode _activeView = ComparisonViewMode.Hierarchy;

        private SerializableChunkDiff _selectedItem;
        private List<SerializableChunkDiff> _selectedNodes = new();

        public ComparisonViewMode ActiveView
        {
            get => _activeView;
            set { if (_activeView != value) { _activeView = value; OnPropertyChanged(); OnPropertyChanged(nameof(FooterSummaryText)); } }
        }

        // Sub-Models (Encapsulated responsibilities)
        public WadResultsTreeModel TreeModel { get; } = new WadResultsTreeModel();

        public SerializableChunkDiff SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (_selectedItem != value)
                {
                    _selectedItem = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ViewChangesHeader));
                    OnPropertyChanged(nameof(CanViewChanges));
                }
            }
        }

        public List<SerializableChunkDiff> SelectedNodes
        {
            get => _selectedNodes;
            set
            {
                _selectedNodes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ViewChangesHeader));
                OnPropertyChanged(nameof(CanViewChanges));
            }
        }

        public string ViewChangesHeader => SelectedNodes.Count > 1 
            ? "View Selected Differences" 
            : "View Differences";

        public bool CanViewChanges
        {
            get
            {
                if (SelectedNodes.Count > 1)
                {
                    return SelectedNodes.All(d => SupportedFileTypes.IsImage(d.Path)) || 
                           SelectedNodes.Any(d => (d.Type == ChunkDiffType.Modified || d.Type == ChunkDiffType.New) && SupportedFileTypes.IsNonImageDiffable(d.Path));
                }

                return SelectedItem != null && 
                       (SupportedFileTypes.IsImage(SelectedItem.Path) || 
                        ((SelectedItem.Type == ChunkDiffType.Modified || SelectedItem.Type == ChunkDiffType.New) && SupportedFileTypes.IsNonImageDiffable(SelectedItem.Path)));
            }
        }

        private WadDiffDetailsModel _detailsModel;
        public WadDiffDetailsModel DetailsModel
        {
            get => _detailsModel;
            set { _detailsModel = value; OnPropertyChanged(); }
        }

        public string FilterText
        {
            get => _filterText;
            set { if (_filterText != value) { _filterText = value; OnPropertyChanged(); } }
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
            set { if (_summaryText != value) { _summaryText = value; OnPropertyChanged(); OnPropertyChanged(nameof(FooterSummaryText)); } }
        }

        public string ResultsSummaryText
        {
            get => _resultsSummaryText;
            set { if (_resultsSummaryText != value) { _resultsSummaryText = value; OnPropertyChanged(); OnPropertyChanged(nameof(FooterSummaryText)); } }
        }

        public string FooterSummaryText => ActiveView == ComparisonViewMode.Results ? ResultsSummaryText : SummaryText;

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
            
            // Store total count on the first real results load
            if (_totalDiffsCount == -1) _totalDiffsCount = diffs.Count;

            // 1. Update Technical Tree
            TreeModel.WadGroups.ReplaceRange(groups);
            
            // 2. Update Summary & Stats
            if (diffs.Count == _totalDiffsCount)
            {
                SummaryText = $"Found {diffs.Count} differences across {groups.Count} WAD files.";
            }
            else
            {
                SummaryText = $"Showing {diffs.Count} of {_totalDiffsCount} differences across {groups.Count} WAD files.";
            }

            if (diffs.Count == _totalDiffsCount)
            {
                CountNew = diffs.Count(d => d.Type == ChunkDiffType.New).ToString();
                CountModified = diffs.Count(d => d.Type == ChunkDiffType.Modified).ToString();
                CountRemoved = diffs.Count(d => d.Type == ChunkDiffType.Removed).ToString();
                CountRenamed = diffs.Count(d => d.Type == ChunkDiffType.Renamed).ToString();
            }

            // 4. Perform Analysis
            CalculateInsights(diffs, groups);
        }

        private void CalculateInsights(List<SerializableChunkDiff> diffs, List<WadGroupViewModel> groups = null)
        {
            if (diffs == null || diffs.Count == 0)
            {
                TreeModel.CategoryDistribution.Clear();
                TreeModel.TopImpactFiles.Clear();
                TreeModel.TopWadPackages.Clear();
                TreeModel.FeatureAreas.Clear();
                TreeModel.AddedPayloadText = "+0 B";
                TreeModel.RemovedPayloadText = "-0 B";
                TreeModel.NetSizeChangeText = "0 B";
                TreeModel.UnknownHashesCount = 0;
                return;
            }

            long addedBytes = 0, removedBytes = 0;
            int unknownHashes = 0;

            var areas = new (string Key, string Name, MaterialIconKind Icon, string Brush, int Count, long Size, string Filter)[]
            {
                ("characters/", "Champions & Entities", MaterialIconKind.AccountCircleOutline, "AccentBrush", 0, 0, "characters"),
                ("ux/", "Loot, Store & UI", MaterialIconKind.StorefrontOutline, "AccentTeal", 0, 0, "ux"),
                ("data/", "Game Data & Balance", MaterialIconKind.CodeJson, "AccentBlue", 0, 0, "data"),
                ("tft", "Teamfight Tactics", MaterialIconKind.ChessKnight, "AccentOrange", 0, 0, "tft"),
                ("audio/", "Audio & Voicebanks", MaterialIconKind.VolumeHigh, "AccentPurple", 0, 0, "audio"),
                ("maps/", "Maps & Environments", MaterialIconKind.MapOutline, "AccentGreen", 0, 0, "maps")
            };
            var categories = new (Func<string, bool> Match, string Name, MaterialIconKind Icon, string Ext, int Count, long Size)[]
            {
                (SupportedFileTypes.IsImage, "Image", MaterialIconKind.ImageOutline, ".dds", 0, 0),
                (SupportedFileTypes.IsAudio, "Audio", MaterialIconKind.MusicNoteOutline, ".bnk", 0, 0),
                (SupportedFileTypes.Is3D, "3D Models", MaterialIconKind.CubeOutline, ".skn", 0, 0),
                (p => SupportedFileTypes.IsText(p) || p.EndsWith(".bin", StringComparison.OrdinalIgnoreCase), "Data", MaterialIconKind.FileDocumentOutline, ".bin", 0, 0),
                (p => p.EndsWith(".hlsl", StringComparison.OrdinalIgnoreCase), "Shaders", MaterialIconKind.CodeBraces, ".hlsl", 0, 0),
                (_ => true, "Miscellaneous", MaterialIconKind.FileOutline, "", 0, 0)
            };
            var topFiles = new List<TopImpactFile>(Math.Min(diffs.Count, 32));

            foreach (var diff in diffs)
            {
                string path = diff.Path ?? string.Empty;
                string lower = path.Replace('\\', '/').ToLowerInvariant();

                // 1. Unknown Hashes & Payload
                if (IsHexHash(path) || path.StartsWith("[unknown_", StringComparison.OrdinalIgnoreCase)) unknownHashes++;

                long oldSz = (long)(diff.OldUncompressedSize ?? 0);
                long newSz = (long)(diff.NewUncompressedSize ?? 0);
                long delta = diff.Type switch
                {
                    ChunkDiffType.New => newSz,
                    ChunkDiffType.Removed => -oldSz,
                    _ => newSz - oldSz
                };

                if (delta > 0) addedBytes += delta;
                else if (delta < 0) removedBytes += Math.Abs(delta);

                // 2. Feature Areas
                for (int i = 0; i < areas.Length; i++)
                {
                    bool match = i switch
                    {
                        0 => lower.Contains("characters/"),
                        1 => lower.Contains("ux/") || lower.Contains("loot") || lower.Contains("emotes") || lower.Contains("lol-game-data"),
                        2 => lower.EndsWith(".bin") || lower.EndsWith(".inibin") || lower.Contains("data/"),
                        3 => lower.Contains("tft") || lower.Contains("sets/set"),
                        4 => SupportedFileTypes.IsAudio(path) || lower.Contains("audio/") || lower.Contains("sound/"),
                        5 => lower.Contains("maps/") || lower.Contains("map11") || lower.Contains("map12") || lower.Contains("map22"),
                        _ => false
                    };

                    if (match)
                    {
                        areas[i].Count++;
                        areas[i].Size += delta;
                        break;
                    }
                }

                // 3. Asset Categories
                for (int i = 0; i < categories.Length; i++)
                {
                    if (categories[i].Match(lower))
                    {
                        categories[i].Count++;
                        categories[i].Size += delta;
                        break;
                    }
                }

                // 4. Impact Files
                if (oldSz > 0 || newSz > 0)
                {
                    var icon = SupportedFileTypes.IsImage(path) ? Material.Icons.MaterialIconKind.ImageOutline
                        : SupportedFileTypes.IsAudio(path) ? Material.Icons.MaterialIconKind.VolumeHigh
                        : SupportedFileTypes.Is3D(path) ? Material.Icons.MaterialIconKind.CubeOutline
                        : Material.Icons.MaterialIconKind.FileDocumentOutline;

                    topFiles.Add(new TopImpactFile
                    {
                        Name = diff.FileName,
                        Path = diff.Path,
                        Type = diff.Type,
                        OldSize = (ulong)oldSz,
                        NewSize = (ulong)newSz,
                        SizeDiff = delta,
                        Diff = diff,
                        IconKind = icon
                    });
                }
            }

            // Update ViewModel
            long net = addedBytes - removedBytes;
            TreeModel.AddedPayloadText = "+" + FormatUtils.FormatSize(addedBytes);
            TreeModel.RemovedPayloadText = "-" + FormatUtils.FormatSize(removedBytes);
            TreeModel.NetSizeChangeText = (net >= 0 ? "+" : "-") + FormatUtils.FormatSize(Math.Abs(net));
            TreeModel.UnknownHashesCount = unknownHashes;

            if (groups != null && groups.Count > 0)
            {
                var topWads = groups
                    .Select(g =>
                    {
                        long wadSize = 0;
                        foreach (var typeGroup in g.Types)
                        {
                            foreach (var d in typeGroup.Diffs)
                            {
                                long oldSz = (long)(d.OldUncompressedSize ?? 0);
                                long newSz = (long)(d.NewUncompressedSize ?? 0);
                                wadSize += d.Type switch
                                {
                                    ChunkDiffType.New => newSz,
                                    ChunkDiffType.Removed => -oldSz,
                                    _ => newSz - oldSz
                                };
                            }
                        }

                        return new TopWadImpact
                        {
                            WadName = g.WadName,
                            Count = g.DiffCount,
                            Percentage = diffs.Count > 0 ? (double)g.DiffCount / diffs.Count * 100 : 0,
                            TotalSizeChange = wadSize
                        };
                    })
                    .OrderByDescending(w => w.Count)
                    .Take(5)
                    .ToList();

                TreeModel.TopWadPackages.ReplaceRange(topWads);
            }

            TreeModel.FeatureAreas.ReplaceRange(areas.Where(a => a.Count > 0).OrderByDescending(a => a.Count).Select(a => new PatchAreaStats
            {
                Name = a.Name,
                IconKind = a.Icon,
                ColorBrushKey = a.Brush,
                Count = a.Count,
                TotalSizeChange = a.Size,
                FilterQuery = a.Filter
            }));

            TreeModel.CategoryDistribution.ReplaceRange(categories.Where(c => c.Count > 0).OrderByDescending(c => c.Count).Select(c => new AssetCategoryStats
            {
                Name = c.Name,
                IconKind = c.Icon,
                ExtensionFilter = c.Ext,
                Count = c.Count,
                Percentage = (double)c.Count / diffs.Count * 100,
                TotalSizeChange = c.Size
            }));

            TreeModel.TopImpactFiles.ReplaceRange(topFiles.OrderByDescending(f => Math.Abs(f.SizeDiff)).Take(5));
        }

        private static bool IsHexHash(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            return name.Length == 16 && name.All(Uri.IsHexDigit);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
