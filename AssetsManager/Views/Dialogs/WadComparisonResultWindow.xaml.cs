using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Views.Helpers;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Comparator;
using AssetsManager.Views.Models.Dialogs;
using AssetsManager.Views.Models.Dialogs.Controls;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Views.Dialogs
{
    public partial class WadComparisonResultWindow : HudWindow
    {
        private List<SerializableChunkDiff> _serializableDiffs;
        private readonly IServiceProvider _serviceProvider;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly AssetDownloader _assetDownloaderService;
        private readonly LogService _logService;
        private readonly WadDifferenceService _wadDifferenceService;
        private readonly WadPackagingService _wadPackagingService;
        private readonly ComparisonHistoryService _comparisonHistoryService;
        private readonly DiffViewService _diffViewService;
        private readonly HashResolverService _hashResolverService;
        private readonly AppSettings _appSettings;

        private string _oldPbePath;
        private string _newPbePath;
        private string _sourceJsonPath;
        private string _assignedFolderName;

        private readonly WadComparisonResultModel _viewModel;

        public WadComparisonResultWindow(
            IServiceProvider serviceProvider, 
            CustomMessageBoxService customMessageBoxService, 
            DirectoriesCreator directoriesCreator, 
            AssetDownloader assetDownloaderService, 
            LogService logService, 
            WadDifferenceService wadDifferenceService, 
            WadPackagingService wadPackagingService, 
            ComparisonHistoryService comparisonHistoryService,
            DiffViewService diffViewService, 
            HashResolverService hashResolverService, 
            AppSettings appSettings)
        {
            InitializeComponent();
            _viewModel = new WadComparisonResultModel();
            DataContext = _viewModel;

            _serviceProvider = serviceProvider;
            _customMessageBoxService = customMessageBoxService;
            _directoriesCreator = directoriesCreator;
            _assetDownloaderService = assetDownloaderService;
            _logService = logService;
            _wadDifferenceService = wadDifferenceService;
            _wadPackagingService = wadPackagingService;
            _comparisonHistoryService = comparisonHistoryService;
            _diffViewService = diffViewService;
            _hashResolverService = hashResolverService;
            _appSettings = appSettings;

            // Peer Injection
            ResultsTree.ParentWindow = this;

            _viewModel.TreeModel.FilterChanged += OnTreeFilterChanged;

            Loaded += WadComparisonResultWindow_Loaded;
            Closed += OnWindowClosed;
        }

        private void OnTreeFilterChanged(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() => ApplyFilters());
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

        public void Initialize(List<ChunkDiff> diffs, string oldPbePath, string newPbePath, string assignedFolderName = null)
        {
            _oldPbePath = oldPbePath;
            _newPbePath = newPbePath;
            _assignedFolderName = assignedFolderName;
            _serializableDiffs = diffs.Select(d => new SerializableChunkDiff
            {
                Type = d.Type,
                OldPath = d.OldPath,
                NewPath = d.NewPath,
                SourceWadFile = d.SourceWadFile,
                OldPathHash = d.OldChunk.PathHash,
                NewPathHash = d.NewChunk.PathHash,
                OldUncompressedSize = (d.Type == ChunkDiffType.New) ? (ulong?)null : (ulong)d.OldChunk.UncompressedSize,
                NewUncompressedSize = (d.Type == ChunkDiffType.Removed) ? (ulong?)null : (ulong)d.NewChunk.UncompressedSize,
                OldCompressionType = (d.Type == ChunkDiffType.New) ? null : d.OldChunk.Compression,
                NewCompressionType = (d.Type == ChunkDiffType.Removed) ? null : d.NewChunk.Compression
            }).ToList();
        }

        public void Initialize(List<SerializableChunkDiff> serializableDiffs, string oldPbePath = null, string newPbePath = null, string sourceJsonPath = null, string assignedFolderName = null)
        {
            _serializableDiffs = serializableDiffs;
            _oldPbePath = oldPbePath;
            _newPbePath = newPbePath;
            _sourceJsonPath = sourceJsonPath;
            _assignedFolderName = assignedFolderName;
        }

        private void OnWindowClosed(object sender, System.EventArgs e)
        {
            if (_viewModel?.TreeModel != null)
            {
                _viewModel.TreeModel.FilterChanged -= OnTreeFilterChanged;
            }
            _serializableDiffs?.Clear();
            _viewModel.TreeModel.WadGroups?.Clear();
            ResultsTree.Cleanup();
        }

        private async void WadComparisonResultWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _viewModel.SetLoadingState(ComparisonLoadingState.ResolvingHashes);
            
            var wadGroups = await Task.Run(() =>
            {
                TryResolveHashes();
                return PrepareGroupedResults(_serializableDiffs);
            });

            _viewModel.SetResults(_serializableDiffs, wadGroups);
        }

        // --- Handle methods for direct peer communication ---

        public void HandleSearchTextChanged(string text)
        {
            _viewModel.FilterText = text;
            ApplyFilters();
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
            
            // Filter only modified items that are NOT audio data containers
            var validDiffs = diffs.Where(d => d.Type == ChunkDiffType.Modified && !SupportedFileTypes.IsAudioDataContainer(d.Path)).ToList();
            
            if (validDiffs.Count > 1)
            {
                await _diffViewService.ShowBatchWadDiffAsync(validDiffs, 0, _oldPbePath, _newPbePath, this);
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

        private void TryResolveHashes()
        {
            var wadFileCache = new Dictionary<string, WadFile>();
            try
            {
                foreach (var diff in _serializableDiffs)
                {
                    if (diff.OldPathHash != 0)
                    {
                        string resolvedPath = _hashResolverService.ResolveHash(diff.OldPathHash);
                        bool isUnresolved = resolvedPath == diff.OldPathHash.ToString("x16");
                        if ((isUnresolved || !Path.HasExtension(resolvedPath)) && diff.Type != ChunkDiffType.New)
                        {
                            string wadPath = Path.Combine(_oldPbePath, diff.SourceWadFile);
                            if (!wadFileCache.TryGetValue(wadPath, out var wadFile) && File.Exists(wadPath))
                                wadFileCache[wadPath] = wadFile = new WadFile(wadPath);

                            if (wadFile != null && wadFile.Chunks.TryGetValue(diff.OldPathHash, out var chunk))
                            {
                                using var stream = wadFile.OpenChunk(chunk);
                                var buffer = new byte[256];
                                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                                string extension = FileTypeDetector.GuessExtension(new Span<byte>(buffer, 0, bytesRead));
                                if (!string.IsNullOrEmpty(extension)) resolvedPath += "." + extension;
                            }
                        }
                        diff.OldPath = resolvedPath;
                    }

                    if (diff.NewPathHash != 0)
                    {
                        string resolvedPath = _hashResolverService.ResolveHash(diff.NewPathHash);
                        bool isUnresolved = resolvedPath == diff.NewPathHash.ToString("x16");
                        if ((isUnresolved || !Path.HasExtension(resolvedPath)) && diff.Type != ChunkDiffType.Removed)
                        {
                            string wadPath = Path.Combine(_newPbePath, diff.SourceWadFile);
                            if (!wadFileCache.TryGetValue(wadPath, out var wadFile) && File.Exists(wadPath))
                                wadFileCache[wadPath] = wadFile = new WadFile(wadPath);

                            if (wadFile != null && wadFile.Chunks.TryGetValue(diff.NewPathHash, out var chunk))
                            {
                                using var stream = wadFile.OpenChunk(chunk);
                                var buffer = new byte[256];
                                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                                string extension = FileTypeDetector.GuessExtension(new Span<byte>(buffer, 0, bytesRead));
                                if (!string.IsNullOrEmpty(extension)) resolvedPath += "." + extension;
                            }
                        }
                        diff.NewPath = resolvedPath;
                    }
                }
            }
            finally
            {
                foreach (var wadFile in wadFileCache.Values) wadFile.Dispose();
            }
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
                if (!string.IsNullOrEmpty(_assignedFolderName))
                {
                    _customMessageBoxService.ShowSuccess("Already Saved", $"This comparison is already stored in your history:\n{_assignedFolderName}", this);
                    return;
                }

                _logService.Log("Starting comparison backup and asset packaging...");
                string displayName = "Manual Backup";
                var uniqueWads = _serializableDiffs.Select(d => d.SourceWadFile).Distinct().ToList();

                if (uniqueWads.Count == 1) displayName = Path.GetFileName(uniqueWads[0]).Split('.')[0];
                else if (!string.IsNullOrEmpty(_newPbePath)) displayName = Path.GetFileName(_newPbePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "Root";

                var folderInfo = _directoriesCreator.GetNewWadComparisonFolderInfo();
                await _wadPackagingService.SaveBackupAsync(_serializableDiffs, _oldPbePath, _newPbePath, folderInfo.FullPath);
                _comparisonHistoryService.RegisterComparisonInHistory(folderInfo.FolderName, $"Comparison from {displayName}", _oldPbePath, _newPbePath);
                _assignedFolderName = folderInfo.FolderName;
                _customMessageBoxService.ShowSuccess("Success", "Results and associated WAD files saved successfully.", this);
            }
            catch (Exception ex)
            {
                _customMessageBoxService.ShowError("Error", $"Failed to save results: {ex.Message}", this);
                _logService.LogError(ex, "Failed to save comparison results.");
            }
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
    }
}
