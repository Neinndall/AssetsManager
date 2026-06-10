using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Core;
using AssetsManager.Services.Downloads;
using AssetsManager.Services.Explorer;
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

        private string _oldPbePath;
        private string _newPbePath;
        private string _sourceJsonPath;

        private readonly WadComparisonResultModel _viewModel;

        public WadComparisonResultWindow(
            IServiceProvider serviceProvider,
            CustomMessageBoxService customMessageBoxService,
            AssetDownloader assetDownloaderService,
            LogService logService,
            ComparisonHistoryService comparisonHistoryService,
            DiffViewService diffViewService,
            HashResolverService hashResolverService,
            AppSettings appSettings,
            WadContentProvider wadContentProvider,
            VersionService versionService)
        {
            InitializeComponent();
            _viewModel = new WadComparisonResultModel();
            DataContext = _viewModel;

            _serviceProvider = serviceProvider;
            _customMessageBoxService = customMessageBoxService;
            _assetDownloaderService = assetDownloaderService;
            _logService = logService;
            _comparisonHistoryService = comparisonHistoryService;
            _diffViewService = diffViewService;
            _hashResolverService = hashResolverService;
            _appSettings = appSettings;
            _wadContentProvider = wadContentProvider;
            _versionService = versionService;

            // Peer Injection
            ResultsTree.ParentWindow = this;

            _viewModel.TreeModel.FilterChanged += OnTreeFilterChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            Loaded += WadComparisonResultWindow_Loaded;
            Closed += OnWindowClosed;
        }

        private void OnViewModelPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WadComparisonResultModel.ActiveView))
            {
                if (_viewModel.ActiveView == ComparisonViewMode.Discovery)
                {
                    _ = LoadGalleryThumbnailsAsync();
                }
            }
        }

        private async Task LoadGalleryThumbnailsAsync()
        {
            var itemsToLoad = _viewModel.DiscoveryItems.Where(i => i.ImagePreview == null).ToList();
            if (!itemsToLoad.Any()) return;

            foreach (var item in itemsToLoad)
            {
                try
                {
                    // Delegamos todo al servicio: extracción + procesado (TextureUtils)
                    // Mantenemos el límite de 256px para optimizar memoria
                    item.ImagePreview = await _wadContentProvider.GetDiffThumbnailAsync(item, _oldPbePath, _newPbePath, 256);
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Failed to load gallery thumbnail for {item.Path}");
                }
            }
        }

        private void OnTreeFilterChanged(object sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(() => ApplyFilters());
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

            // If we are in Gallery mode, trigger thumbnail loading for newly visible filtered items
            if (_viewModel.ActiveView == ComparisonViewMode.Discovery)
            {
                _ = LoadGalleryThumbnailsAsync();
            }
        }

        public void Initialize(List<ChunkDiff> diffs, string oldPbePath, string newPbePath)
        {
            _oldPbePath = oldPbePath;
            _newPbePath = newPbePath;
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

        public void Initialize(List<SerializableChunkDiff> serializableDiffs, string oldPbePath = null, string newPbePath = null, string sourceJsonPath = null)
        {
            _serializableDiffs = serializableDiffs;
            _oldPbePath = oldPbePath;
            _newPbePath = newPbePath;
            _sourceJsonPath = sourceJsonPath;
        }

        private void OnWindowClosed(object sender, System.EventArgs e)
        {
            Loaded -= WadComparisonResultWindow_Loaded;
            Closed -= OnWindowClosed;

            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                if (_viewModel.TreeModel != null)
                {
                    _viewModel.TreeModel.FilterChanged -= OnTreeFilterChanged;
                }
            }
            _serializableDiffs?.Clear();
            _viewModel.TreeModel.WadGroups?.Clear();
            ResultsTree.Cleanup();
        }

        private async void WadComparisonResultWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= WadComparisonResultWindow_Loaded;
            _viewModel.SetLoadingState(ComparisonLoadingState.ResolvingHashes);
            
            var wadGroups = await Task.Run(() =>
            {
                TryResolveHashes();
                return PrepareGroupedResults(_serializableDiffs);
            });

            _viewModel.SetResults(_serializableDiffs, wadGroups);
        }

        // --- Handle methods for direct peer communication ---

        private void GlobalSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _viewModel.FilterText = globalSearchBox.Text;
            ApplyFilters();
        }

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

        private void TryResolveHashes()
        {
            string backupRoot = !string.IsNullOrEmpty(_sourceJsonPath) ? Path.GetDirectoryName(_sourceJsonPath) : null;
            
            foreach (var diff in _serializableDiffs)
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
                string version = await _versionService.GetGameVersionAsync(_newPbePath);

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
    }
}
