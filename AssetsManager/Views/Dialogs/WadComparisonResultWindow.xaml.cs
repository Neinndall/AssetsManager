using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Core;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Formatting;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using AssetsManager.Views.Dialogs.Controls;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Dialogs;
using AssetsManager.Views.Models.Dialogs.Controls;
using AssetsManager.Views.Models.Wad;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Views.Dialogs
{
    public partial class WadComparisonResultWindow : HudWindow
    {
        private List<SerializableChunkDiff> _serializableDiffs;
        private readonly IServiceProvider _serviceProvider;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly AssetDownloader _assetDownloaderService;
        private readonly LogService _logService;
        private readonly ComparisonHistoryService _comparisonHistoryService;
        private readonly DiffViewService _diffViewService;
        private readonly HashResolverService _hashResolverService;
        private readonly AppSettings _appSettings;
        private readonly WadContentProvider _wadContentProvider;
        private readonly VersionService _versionService;
        private readonly BackupManager _backupManager;
        private readonly ExtractionService _extractionService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly SemaphoreSlim _thumbnailLoadLimiter = new(2, 2);
        private readonly Dictionary<SerializableChunkDiff, CancellationTokenSource> _thumbnailLoads = new();

        private string _oldPbePath;
        private string _newPbePath;
        private string _sourceJsonPath;
        private string _version;
        private List<ExtractResultItem> _extractionResults;

        private readonly WadComparisonResultModel _viewModel;

        public WadComparisonResultWindow(IServiceProvider serviceProvider)
        {
            InitializeComponent();
            _viewModel = new WadComparisonResultModel();
            DataContext = _viewModel;

            _serviceProvider = serviceProvider;
            _customMessageBoxService = serviceProvider.GetRequiredService<CustomMessageBoxService>();
            _assetDownloaderService = serviceProvider.GetRequiredService<AssetDownloader>();
            _logService = serviceProvider.GetRequiredService<LogService>();
            _comparisonHistoryService = serviceProvider.GetRequiredService<ComparisonHistoryService>();
            _diffViewService = serviceProvider.GetRequiredService<DiffViewService>();
            _hashResolverService = serviceProvider.GetRequiredService<HashResolverService>();
            _appSettings = serviceProvider.GetRequiredService<AppSettings>();
            _wadContentProvider = serviceProvider.GetRequiredService<WadContentProvider>();
            _versionService = serviceProvider.GetRequiredService<VersionService>();
            _backupManager = serviceProvider.GetRequiredService<BackupManager>();
            _extractionService = serviceProvider.GetRequiredService<ExtractionService>();
            _directoriesCreator = serviceProvider.GetRequiredService<DirectoriesCreator>();

            // Peer Injection
            ResultsTree.ParentWindow = this;
            ResultsControl.ParentWindow = this;

            _viewModel.TreeModel.FilterChanged += OnTreeFilterChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ResultsControl.ItemVisibilityChanged += OnResultsItemVisibilityChanged;
            ResultsControl.DiffTypeChanged += OnResultsDiffTypeChanged;
            ResultsControl.FilterApplied += OnResultsFilterApplied;

            Loaded += WadComparisonResultWindow_Loaded;
            Closed += OnWindowClosed;
        }

        private void OnViewModelPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WadComparisonResultModel.ActiveView))
            {
                UpdateFooterSummary();
                if (_viewModel.ActiveView == ComparisonViewMode.Results)
                {
                    QueueResultsThumbnailLoading();
                }
                else
                {
                    ResetThumbnailLoading();
                }
            }
        }

        private void OnResultsFilterApplied()
        {
            UpdateFooterSummary();
        }

        private void UpdateFooterSummary()
        {
            if (_viewModel == null || _viewModel.ActiveView != ComparisonViewMode.Results)
            {
                return;
            }

            int total = ResultsControl.TotalCount;
            int visible = ResultsControl.VisibleCount;
            if (total == 0)
            {
                _viewModel.ResultsSummaryText = "No results to show.";
            }
            else if (visible == total)
            {
                _viewModel.ResultsSummaryText = $"Showing {visible} {ResultsControl.SelectedDiffType} results.";
            }
            else
            {
                _viewModel.ResultsSummaryText = $"Showing {visible} of {total} {ResultsControl.SelectedDiffType} results.";
            }
        }

        private void OnResultsItemVisibilityChanged(SerializableChunkDiff item, bool isVisible)
        {
            if (!isVisible || _viewModel.ActiveView != ComparisonViewMode.Results)
            {
                CancelThumbnail(item);
                return;
            }

            LoadThumbnail(item);
        }

        private void QueueResultsThumbnailLoading()
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_viewModel.ActiveView == ComparisonViewMode.Results)
                {
                    ResultsControl.LoadRealizedItems();
                }
            }, DispatcherPriority.Loaded);
        }

        private void OnTreeFilterChanged(object sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() => ApplyFilters());
        }

        private void ResetThumbnailLoading()
        {
            foreach (var cancellation in _thumbnailLoads.Values) cancellation.Cancel();
            _thumbnailLoads.Clear();
            foreach (var item in _serializableDiffs ?? Enumerable.Empty<SerializableChunkDiff>())
            {
                item.ImagePreview = null;
            }
        }

        private async void LoadThumbnail(SerializableChunkDiff item)
        {
            if (item.ImagePreview != null || _thumbnailLoads.ContainsKey(item)) return;

            var cancellation = new CancellationTokenSource();
            _thumbnailLoads.Add(item, cancellation);
            bool acquiredSlot = false;
            try
            {
                await _thumbnailLoadLimiter.WaitAsync(cancellation.Token);
                acquiredSlot = true;
                var preview = await _wadContentProvider.GetDiffThumbnailAsync(
                    item, _oldPbePath, _newPbePath, 256, cancellation.Token);

                if (_thumbnailLoads.TryGetValue(item, out var active) && ReferenceEquals(active, cancellation))
                    item.ImagePreview = preview;
            }
            catch (OperationCanceledException)
            {
                // Recycling and filtering cancel thumbnails by design.
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to load thumbnail. WAD='{item.SourceWadFile}', Path='{item.Path}'");
            }
            finally
            {
                if (acquiredSlot) _thumbnailLoadLimiter.Release();
                if (_thumbnailLoads.TryGetValue(item, out var active) && ReferenceEquals(active, cancellation))
                    _thumbnailLoads.Remove(item);
                cancellation.Dispose();
            }
        }

        private void CancelThumbnail(SerializableChunkDiff item)
        {
            if (_thumbnailLoads.Remove(item, out var cancellation)) cancellation.Cancel();
        }

        public void ApplyFilters()
        {
            if (_serializableDiffs == null) return;

            var filtered = _serializableDiffs.Where(d => 
            {
                bool stateMatch = false;
                if (d.Type == ChunkDiffType.New && _viewModel.TreeModel.ShowNew) stateMatch = true;
                else if (d.Type == ChunkDiffType.Modified && _viewModel.TreeModel.ShowModified) stateMatch = true;
                else if (d.Type == ChunkDiffType.Removed && _viewModel.TreeModel.ShowRemoved) stateMatch = true;
                else if (d.Type == ChunkDiffType.Renamed && _viewModel.TreeModel.ShowRenamed) stateMatch = true;
                
                if (!stateMatch) return false;

                if (string.IsNullOrWhiteSpace(_viewModel.FilterText)) return true;
                return d.FileName.IndexOf(_viewModel.FilterText, StringComparison.OrdinalIgnoreCase) >= 0;
            }).ToList();

            var wadGroups = PrepareGroupedResults(filtered);
            _viewModel.SetResults(filtered, wadGroups);
        }

        public void Initialize(List<ChunkDiff> diffs, string oldPbePath, string newPbePath, string version = null)
        {
            _oldPbePath = oldPbePath;
            _newPbePath = newPbePath;
            _version = version;
            _serializableDiffs = diffs.Select(d => new SerializableChunkDiff
            {
                Type = d.Type,
                OldPath = d.OldPath,
                NewPath = d.NewPath,
                SourceWadFile = d.SourceWadFile,
                OldPathHash = (d.Type == ChunkDiffType.New) ? 0 : d.OldChunk.PathHash,
                NewPathHash = (d.Type == ChunkDiffType.Removed) ? 0 : d.NewChunk.PathHash,
                OldUncompressedSize = (d.Type == ChunkDiffType.New) ? (ulong?)null : (ulong)d.OldChunk.UncompressedSize,
                NewUncompressedSize = (d.Type == ChunkDiffType.Removed) ? (ulong?)null : (ulong)d.NewChunk.UncompressedSize,
                OldCompressionType = (d.Type == ChunkDiffType.New) ? null : (WadChunkCompression?)d.OldChunk.Compression,
                NewCompressionType = (d.Type == ChunkDiffType.Removed) ? null : (WadChunkCompression?)d.NewChunk.Compression
            }).ToList();
        }

        public void Initialize(List<SerializableChunkDiff> serializableDiffs, string oldPbePath = null, string newPbePath = null, string sourceJsonPath = null, string version = null, List<ExtractResultItem> extractionResults = null)
        {
            _serializableDiffs = serializableDiffs;
            _oldPbePath = oldPbePath;
            _newPbePath = newPbePath;
            _sourceJsonPath = sourceJsonPath;
            _version = version;
            _extractionResults = extractionResults;

            // Default to the Results view when a batch extraction just finished.
            if (extractionResults != null && extractionResults.Count > 0)
            {
                _viewModel.ActiveView = ComparisonViewMode.Results;
            }
        }

        private void OnWindowClosed(object sender, System.EventArgs e)
        {
            Loaded -= WadComparisonResultWindow_Loaded;
            Closed -= OnWindowClosed;
            ResultsControl.ItemVisibilityChanged -= OnResultsItemVisibilityChanged;
            ResultsControl.DiffTypeChanged -= OnResultsDiffTypeChanged;
            ResultsControl.FilterApplied -= OnResultsFilterApplied;
            ResetThumbnailLoading();

            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                if (_viewModel.TreeModel != null)
                {
                    _viewModel.TreeModel.FilterChanged -= OnTreeFilterChanged;
                }
            }
            _serializableDiffs = null;
            _viewModel.TreeModel.WadGroups?.Clear();
            ResultsTree.Cleanup();
            DataContext = null;
        }

        private async void WadComparisonResultWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= WadComparisonResultWindow_Loaded;
            _viewModel.SetLoadingState(ComparisonLoadingState.ResolvingHashes);

            var diffs = _serializableDiffs;
            var wadGroups = await Task.Run(() =>
            {
                TryResolveHashes(diffs);
                return PrepareGroupedResults(diffs);
            });

            if (_serializableDiffs != null)
            {
                _viewModel.SetResults(diffs, wadGroups);
                PopulateResults(ChunkDiffType.New);
                QueueResultsThumbnailLoading();
            }
        }

        private void OnResultsDiffTypeChanged(ChunkDiffType type)
        {
            if (_serializableDiffs == null) return;
            PopulateResults(type);
        }

        private void PopulateResults(ChunkDiffType type)
        {
            if (_serializableDiffs == null) return;

            var diffs = _serializableDiffs.Where(d => d.Type == type).ToList();
            var resultMap = (type == ChunkDiffType.New ? _extractionResults ?? new List<ExtractResultItem>() : new List<ExtractResultItem>())
                .GroupBy(r => r.Diff)
                .ToDictionary(g => g.Key, g => g.Last());

            var items = diffs.Select(diff =>
                resultMap.TryGetValue(diff, out var result)
                    ? new WadResultItemModel(result)
                    : new WadResultItemModel(diff, _extractionService != null ? _extractionService.GetModeFromSettings(diff) : WadExportMode.Original)).ToList();

            ResultsControl.SetItems(items);
        }

        // --- Handle methods for direct peer communication ---

        private void GlobalSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _viewModel.FilterText = globalSearchBox.Text;
            ApplyFilters();
            ResultsControl.SetSearchText(globalSearchBox.Text);
        }

        public void HandleSearchTextChanged(string text)
        {
            _viewModel.FilterText = text;
            ApplyFilters();
            ResultsControl.SetSearchText(text);
        }

        public void HandleTreeSelectionChanged(object selectedItem)
        {
            if (selectedItem is SerializableChunkDiff diff)
            {
                _viewModel.DetailsModel = new WadDiffDetailsModel { SelectedDiff = diff };
            }
            else
            {
                _viewModel.DetailsModel = null;
            }
        }

        public async void HandleViewDifferencesRequest()
        {
            if (ResultsTree.SelectedItem is not SerializableChunkDiff diff) return;
            await _diffViewService.ShowWadDiffAsync(diff, _oldPbePath, _newPbePath, this, _sourceJsonPath);
        }

        public async void HandleBatchViewDifferencesRequest(List<SerializableChunkDiff> diffs)
        {
            if (diffs == null || diffs.Count == 0) return;

            // Check if they are all images
            bool isImageBatch = diffs.All(d => SupportedFileTypes.IsImage(d.Path));
            
            var validDiffs = isImageBatch
                ? diffs.ToList()
                : diffs.Where(d => d.Type == ChunkDiffType.Modified && SupportedFileTypes.IsNonImageDiffable(d.Path)).ToList();
            
            if (validDiffs.Count > 1)
            {
                await _diffViewService.ShowBatchWadDiffAsync(validDiffs, 0, _oldPbePath, _newPbePath, this, _sourceJsonPath);
            }
            else if (validDiffs.Count == 1)
            {
                await _diffViewService.ShowWadDiffAsync(validDiffs[0], _oldPbePath, _newPbePath, this, _sourceJsonPath);
            }
        }

        public void HandleTreeContextMenuOpening()
        {
            // Sync selection to ViewModel to trigger dynamic Header/IsEnabled updates
            _viewModel.SelectedItem = ResultsTree.SelectedItem as SerializableChunkDiff;
            _viewModel.SelectedNodes = ResultsTree.SelectedDiffs;

            // Manually sync properties to the MenuItem (FileExplorer standard)
            if (ResultsTree.ViewDifferencesMenuItem is MenuItem viewDiffMenuItem)
            {
                viewDiffMenuItem.Header = _viewModel.ViewChangesHeader;
                viewDiffMenuItem.IsEnabled = _viewModel.CanViewChanges;
            }
        }

        private void TryResolveHashes(IEnumerable<SerializableChunkDiff> diffs)
        {
            string backupRoot = !string.IsNullOrEmpty(_sourceJsonPath) ? Path.GetDirectoryName(_sourceJsonPath) : null;
            
            foreach (var diff in diffs)
            {
                if (backupRoot != null)
                {
                    diff.BackupChunkPath = WadNodeLoaderService.GetBackupChunkPath(backupRoot, diff);
                }

                // Optimization: Skip if already has a readable name. Only attempt if empty or a raw hex hash.
                if (diff.OldPathHash != 0 && (string.IsNullOrEmpty(diff.OldPath) || IsHexHash(diff.OldPath)))
                {
                    string resolved = _hashResolverService.ResolveHash(diff.OldPathHash);
                    if (resolved != null) diff.OldPath = resolved;
                }

                if (diff.NewPathHash != 0 && (string.IsNullOrEmpty(diff.NewPath) || IsHexHash(diff.NewPath)))
                {
                    string resolved = _hashResolverService.ResolveHash(diff.NewPathHash);
                    if (resolved != null) diff.NewPath = resolved;
                }
            }
        }

        private bool IsHexHash(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.Length == 16 && System.Text.RegularExpressions.Regex.IsMatch(path, @"^[0-9a-fA-F]+$");
        }

        private List<WadGroupViewModel> PrepareGroupedResults(List<SerializableChunkDiff> diffs)
        {
            var groups = new List<WadGroupViewModel>();
            var groupedByWad = diffs.GroupBy(d => d.SourceWadFile).OrderBy(g => g.Key);

            foreach (var wadGroup in groupedByWad)
            {
                var wadVm = new WadGroupViewModel { WadName = wadGroup.Key, DiffCount = wadGroup.Count() };
                var groupedByType = wadGroup.GroupBy(d => d.Type).OrderBy(g => g.Key.ToString());
                foreach (var typeGroup in groupedByType)
                {
                    var typeVm = new DiffTypeGroupViewModel { Type = typeGroup.Key, DiffCount = typeGroup.Count() };
                    typeVm.Diffs.ReplaceRange(typeGroup.OrderBy(d => d.NewPath ?? d.OldPath));
                    if (typeVm.Diffs.Count > 0) wadVm.Types.Add(typeVm);
                }
                if (wadVm.Types.Count > 0) groups.Add(wadVm);
            }
            return groups;
        }

        private void PopulateResults(List<SerializableChunkDiff> diffs)
        {
            var wadGroups = PrepareGroupedResults(diffs);
            _viewModel.SetResults(diffs, wadGroups);
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _logService.Log("Starting comparison backup and asset packaging...");
                string displayName = ResolveComparisonDisplayName();
                
                // Use stored version if available, fallback to dynamic detection if missing
                string version = _version;
                if (string.IsNullOrEmpty(version))
                {
                    string root = _backupManager.GetGameRoot(_newPbePath);
                    version = await _versionService.GetGameVersionAsync(root ?? _newPbePath);
                }

                var result = await _comparisonHistoryService.EnsureArchivedAsync(
                    _serializableDiffs, _oldPbePath, _newPbePath, version, displayName);

                if (result.AlreadyArchived)
                {
                    _customMessageBoxService.ShowSuccess("Already Saved", $"This comparison is already stored in your history:\n{result.ReferenceId}", this);
                }
                else
                {
                    _customMessageBoxService.ShowSuccess("Success", "Results and associated WAD files saved successfully.", this);
                }
            }
            catch (Exception ex)
            {
                _customMessageBoxService.ShowError("Error", $"Failed to save results: {ex.Message}", this);
                _logService.LogError(ex, "Failed to save comparison results.");
            }
        }

        private string ResolveComparisonDisplayName()
        {
            var uniqueWads = _serializableDiffs.Select(d => d.SourceWadFile).Distinct().ToList();

            if (uniqueWads.Count == 1) return Path.GetFileName(uniqueWads[0]).Split('.')[0];

            if (!string.IsNullOrEmpty(_newPbePath))
            {
                return Path.GetFileName(_newPbePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "Root";
            }

            return "Unknown";
        }

        private async void ReloadHashesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _viewModel.SetLoadingState(ComparisonLoadingState.ReloadingHashes);
                await Task.Run(async () =>
                {
                    await _hashResolverService.ForceReloadHashesAsync();
                    foreach (var diff in _serializableDiffs)
                    {
                        if (diff.OldPathHash != 0) diff.OldPath = _hashResolverService.ResolveHash(diff.OldPathHash);
                        if (diff.NewPathHash != 0) diff.NewPath = _hashResolverService.ResolveHash(diff.NewPathHash);
                    }
                });
                PopulateResults(_serializableDiffs);
                _customMessageBoxService.ShowSuccess("Success", "Hashes have been reloaded and the result tree has been refreshed.", this);
            }
            catch (Exception ex)
            {
                _viewModel.SetLoadingState(ComparisonLoadingState.Ready);
                _customMessageBoxService.ShowError("Error", $"Failed to reload hashes: {ex.Message}", this);
                _logService.LogError(ex, "Failed to reload hashes.");
            }
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Results View Actions
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        public async void HandleResultsAction(string action, List<WadResultItemModel> items)
        {
            if (items == null || items.Count == 0) return;

            try
            {
                switch (action)
                {
                    case "Retry":
                        await ReExportAsync(items);
                        break;

                    case "Extract":
                        await ReExportAsync(items, WadExportMode.Original);
                        break;

                    case "Save":
                        await ReExportAsync(items, WadExportMode.Smart);
                        break;

                    case "CopyPaths":
                        Clipboard.SetText(string.Join(Environment.NewLine, items.Select(i => i.Diff.Path)));
                        break;

                    case "OpenFolder":
                        OpenOutputFolder(items.FirstOrDefault(i => !string.IsNullOrEmpty(i.OutputPath)) ?? items.FirstOrDefault());
                        break;
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to handle Results action.");
                _customMessageBoxService.ShowError("Error", $"Action failed: {ex.Message}", this);
            }
        }

        public void OpenOutputFolder(WadResultItemModel item)
        {
            string folder = item?.OutputPath;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                _customMessageBoxService.ShowInfo("Folder not found", "This asset has not been extracted yet.", this);
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }

        private async Task ReExportAsync(List<WadResultItemModel> items, WadExportMode? mode = null)
        {
            if (string.IsNullOrEmpty(_newPbePath)) return;

            var diffs = items.Select(i => i.Diff).Where(d => d.Type == ChunkDiffType.New).ToList();
            if (diffs.Count == 0) return;

            using var cancellation = new CancellationTokenSource();
            var results = new List<ExtractResultItem>();

            if (mode == WadExportMode.Smart)
            {
                results.AddRange(await _extractionService.ExtractSmartAsync(diffs, _newPbePath, cancellation.Token));
            }
            else if (mode == WadExportMode.Original)
            {
                results.AddRange(await _extractionService.ExtractRawAsync(diffs, _newPbePath, cancellation.Token));
            }
            else
            {
                // Retry: re-export each failed item with the mode it was attempted with.
                var rawDiffs = items.Where(i => i.Mode == WadExportMode.Original).Select(i => i.Diff).ToList();
                var smartDiffs = items.Where(i => i.Mode == WadExportMode.Smart).Select(i => i.Diff).ToList();
                results.AddRange(await _extractionService.ExtractRawAsync(rawDiffs, _newPbePath, cancellation.Token));
                results.AddRange(await _extractionService.ExtractSmartAsync(smartDiffs, _newPbePath, cancellation.Token));
            }

            if (results.Count == 0) return;

            var resultMap = results.ToDictionary(r => r.Diff, r => r);
            foreach (var item in ResultsControl.GetAllItems())
            {
                if (resultMap.TryGetValue(item.Diff, out var result))
                {
                    item.UpdateResult(result);
                }
            }

            int failedCount = results.Count(r => !r.Success);
            ResultsControl.UpdateRetryButton();
            ResultsControl.RefreshOpenFolderButton();
            if (failedCount == 0)
            {
                _customMessageBoxService.ShowSuccess("Extraction Complete", $"{results.Count} asset(s) exported successfully.", this);
            }
            else
            {
                _customMessageBoxService.ShowWarning("Extraction Finished", $"{results.Count - failedCount} exported, {failedCount} failed.", this);
            }
        }
    }
}
