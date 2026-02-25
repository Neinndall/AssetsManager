using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AssetsManager.Views.Helpers;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.WindowsAPICodePack.Dialogs;
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Comparator;
using AssetsManager.Views.Models.Dialogs;
using AssetsManager.Views.Models.Dialogs.Controls;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Services.Downloads;
using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using LeagueToolkit.Core.Wad;
using MahApps.Metro.Controls;

namespace AssetsManager.Views.Dialogs
{
    public partial class WadComparisonResultWindow : MetroWindow
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
        private string _sourceJsonPath; // Path to the loaded wadcomparison.json
        private string _assignedFolderName; // Stores the folder name if it was already auto-saved

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

            Loaded += WadComparisonResultWindow_Loaded;
            Closed += OnWindowClosed;
        }

        /// <summary>
        /// Initializes the window with data from a live comparison.
        /// </summary>
        public void Initialize(List<ChunkDiff> diffs, string oldPbePath, string newPbePath, string assignedFolderName = null)
        {
            _oldPbePath = oldPbePath;
            _newPbePath = newPbePath;
            _sourceJsonPath = null;
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

        /// <summary>
        /// Initializes the window with data loaded from a saved JSON file.
        /// </summary>
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
            Loaded -= WadComparisonResultWindow_Loaded;
            ResultsTree.SelectedItemChanged -= ResultsTree_SelectedItemChanged;
            ResultsTree.ContextMenuOpening -= ResultsTree_ContextMenuOpening;
            _serializableDiffs?.Clear();
            _viewModel.TreeModel.WadGroups?.Clear();
            ResultsTree.Cleanup();
        }

        private async void WadComparisonResultWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _viewModel.SetLoadingState(ComparisonLoadingState.ResolvingHashes);
            
            // Execute heavy logic in a background thread to keep UI responsive
            var wadGroups = await Task.Run(() =>
            {
                TryResolveHashes();
                return PrepareGroupedResults(_serializableDiffs);
            });

            // Update UI via Model
            _viewModel.SetResults(_serializableDiffs, wadGroups);
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var searchTextBox = (TextBox)e.Source;
            if (string.IsNullOrWhiteSpace(searchTextBox.Text))
            {
                PopulateResults(_serializableDiffs);
            }
            else
            {
                var filteredDiffs = _serializableDiffs
                    .Where(d => d.FileName.IndexOf(searchTextBox.Text, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                PopulateResults(filteredDiffs);
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
                        bool noExtension = !Path.HasExtension(resolvedPath);

                        if ((isUnresolved || noExtension) && diff.Type != ChunkDiffType.New)
                        {
                            string wadPath = Path.Combine(_oldPbePath, diff.SourceWadFile);
                            if (!wadFileCache.TryGetValue(wadPath, out var wadFile))
                            {
                                if (File.Exists(wadPath))
                                {
                                    wadFile = new WadFile(wadPath);
                                    wadFileCache[wadPath] = wadFile;
                                }
                            }

                            if (wadFile != null && wadFile.Chunks.TryGetValue(diff.OldPathHash, out var chunk))
                            {
                                using (var stream = wadFile.OpenChunk(chunk))
                                {
                                    var buffer = new byte[256];
                                    var bytesRead = stream.Read(buffer, 0, buffer.Length);
                                    var data = new Span<byte>(buffer, 0, bytesRead);
                                    string extension = FileTypeDetector.GuessExtension(data);
                                    if (!string.IsNullOrEmpty(extension))
                                    {
                                        resolvedPath += "." + extension;
                                    }
                                }
                            }
                        }
                        diff.OldPath = resolvedPath;
                    }

                    if (diff.NewPathHash != 0)
                    {
                        string resolvedPath = _hashResolverService.ResolveHash(diff.NewPathHash);
                        bool isUnresolved = resolvedPath == diff.NewPathHash.ToString("x16");
                        bool noExtension = !Path.HasExtension(resolvedPath);

                        if ((isUnresolved || noExtension) && diff.Type != ChunkDiffType.Removed)
                        {
                            string wadPath = Path.Combine(_newPbePath, diff.SourceWadFile);
                            if (!wadFileCache.TryGetValue(wadPath, out var wadFile))
                            {
                                if (File.Exists(wadPath))
                                {
                                    wadFile = new WadFile(wadPath);
                                    wadFileCache[wadPath] = wadFile;
                                }
                            }

                            if (wadFile != null && wadFile.Chunks.TryGetValue(diff.NewPathHash, out var chunk))
                            {
                                using (var stream = wadFile.OpenChunk(chunk))
                                {
                                    var buffer = new byte[256];
                                    var bytesRead = stream.Read(buffer, 0, buffer.Length);
                                    var data = new Span<byte>(buffer, 0, bytesRead);
                                    string extension = FileTypeDetector.GuessExtension(data);
                                    if (!string.IsNullOrEmpty(extension))
                                    {
                                        resolvedPath += "." + extension;
                                    }
                                }
                            }
                        }
                        diff.NewPath = resolvedPath;
                    }
                }
            }
            finally
            {
                foreach (var wadFile in wadFileCache.Values)
                {
                    wadFile.Dispose();
                }
            }
        }

        private List<WadGroupViewModel> PrepareGroupedResults(List<SerializableChunkDiff> diffs)
        {
            var groupedByWad = diffs.GroupBy(d => d.SourceWadFile)
                                    .OrderBy(g => g.Key);

            return groupedByWad.Select(wadGroup => new WadGroupViewModel
            {
                WadName = wadGroup.Key,
                DiffCount = wadGroup.Count(),
                Types = new ObservableRangeCollection<DiffTypeGroupViewModel>(wadGroup.GroupBy(d => d.Type)
                                  .OrderBy(g => g.Key.ToString())
                                  .Select(typeGroup => new DiffTypeGroupViewModel
                                  {
                                      Type = typeGroup.Key,
                                      DiffCount = typeGroup.Count(),
                                      Diffs = new ObservableRangeCollection<SerializableChunkDiff>(typeGroup.OrderBy(d => d.NewPath ?? d.OldPath))
                                  }))
            }).ToList();
        }

        private void PopulateResults(List<SerializableChunkDiff> diffs)
        {
            var wadGroups = PrepareGroupedResults(diffs);
            _viewModel.SetResults(diffs, wadGroups);
        }

        private void ResultsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is SerializableChunkDiff diff)
            {
                _viewModel.DetailsModel = new WadDiffDetailsModel { SelectedDiff = diff };
            }
            else
            {
                _viewModel.DetailsModel = null;
            }
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // If it was already auto-saved, just notify the user and don't do anything else.
                if (!string.IsNullOrEmpty(_assignedFolderName))
                {
                    _customMessageBoxService.ShowSuccess("Already Saved", $"This comparison is already stored in your history:\n{_assignedFolderName}", this);
                    return;
                }

                _logService.Log("Starting comparison backup and asset packaging...");
                
                string displayName = "Manual Backup";
                var uniqueWads = _serializableDiffs.Select(d => d.SourceWadFile).Distinct().ToList();

                if (uniqueWads.Count == 1)
                {
                    displayName = Path.GetFileName(uniqueWads[0]).Split('.')[0];
                }
                else if (!string.IsNullOrEmpty(_newPbePath))
                {
                    displayName = Path.GetFileName(_newPbePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (string.IsNullOrEmpty(displayName)) displayName = "Root";
                }

                // 1. Physical Save (WadPackagingService)
                var folderInfo = _directoriesCreator.GetNewWadComparisonFolderInfo();
                
                await _wadPackagingService.SaveBackupAsync(_serializableDiffs, _oldPbePath, _newPbePath, folderInfo.FullPath);

                // 2. Metadata Registration (ComparisonHistoryService)
                _comparisonHistoryService.RegisterComparisonInHistory(folderInfo.FolderName, $"Comparison from {displayName}", _oldPbePath, _newPbePath);

                // Assign the folder name so subsequent clicks also don't duplicate
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
                        if (diff.OldPathHash != 0)
                        {
                            diff.OldPath = _hashResolverService.ResolveHash(diff.OldPathHash);
                        }
                        if (diff.NewPathHash != 0)
                        {
                            diff.NewPath = _hashResolverService.ResolveHash(diff.NewPathHash);
                        }
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

        private async void ViewDifferences_Click(object sender, RoutedEventArgs e)
        {
            if (ResultsTree.SelectedItem is not SerializableChunkDiff diff) return;

            var diffViewService = _serviceProvider.GetRequiredService<DiffViewService>();
            await diffViewService.ShowWadDiffAsync(diff, _oldPbePath, _newPbePath, this, _sourceJsonPath);
        }

        private void ResultsTree_ContextMenuOpening(object sender, RoutedEventArgs e)
        {
            if (ResultsTree.ViewDifferencesMenuItem is MenuItem viewDiffMenuItem)
            {
                viewDiffMenuItem.IsEnabled = false;
                if (ResultsTree.SelectedItem is SerializableChunkDiff diff && diff.Type == ChunkDiffType.Modified)
                {
                    viewDiffMenuItem.IsEnabled = true;
                }
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) this.DragMove();
        }
    }
}
