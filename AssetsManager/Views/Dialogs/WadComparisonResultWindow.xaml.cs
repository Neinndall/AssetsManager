using System;
using AssetsManager.Services.Comparator;
using AssetsManager.Views.Models;
using AssetsManager.Services.Downloads;
using AssetsManager.Services;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
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
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Views.Dialogs
{
    #region ViewModel Classes
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
    #endregion

    public partial class WadComparisonResultWindow : Window
    {
        private readonly List<SerializableChunkDiff> _serializableDiffs;
        private readonly IServiceProvider _serviceProvider;
        private readonly CustomMessageBoxService _customMessageBoxService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly AssetDownloader _assetDownloaderService;
        private readonly LogService _logService;
        private readonly WadDifferenceService _wadDifferenceService;
        private readonly WadPackagingService _wadPackagingService;
        private readonly DiffViewService _diffViewService;
        private readonly HashResolverService _hashResolverService;

        private readonly AppSettings _appSettings;
        private readonly string _oldPbePath;
        private readonly string _newPbePath;
        private readonly string _sourceJsonPath; // Path to the loaded wadcomparison.json

        public WadComparisonResultWindow(List<ChunkDiff> diffs, IServiceProvider serviceProvider, CustomMessageBoxService customMessageBoxService, DirectoriesCreator directoriesCreator, AssetDownloader assetDownloaderService, LogService logService, WadDifferenceService wadDifferenceService, WadPackagingService wadPackagingService, DiffViewService diffViewService, HashResolverService hashResolverService, AppSettings appSettings, string oldPbePath, string newPbePath)
        {
            InitializeComponent();
            _serviceProvider = serviceProvider;
            _customMessageBoxService = customMessageBoxService;
            _directoriesCreator = directoriesCreator;
            _assetDownloaderService = assetDownloaderService;
            _logService = logService;
            _wadDifferenceService = wadDifferenceService;
            _wadPackagingService = wadPackagingService;
            _diffViewService = diffViewService;
            _hashResolverService = hashResolverService;
            _appSettings = appSettings;
            _oldPbePath = oldPbePath;
            _newPbePath = newPbePath;
            _sourceJsonPath = null; // Not loaded from a file
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
            Loaded += WadComparisonResultWindow_Loaded;
            Closed += OnWindowClosed;
        }

        public WadComparisonResultWindow(List<SerializableChunkDiff> serializableDiffs, IServiceProvider serviceProvider, CustomMessageBoxService customMessageBoxService, DirectoriesCreator directoriesCreator, AssetDownloader assetDownloaderService, LogService logService, WadDifferenceService wadDifferenceService, WadPackagingService wadPackagingService, DiffViewService diffViewService, HashResolverService hashResolverService, AppSettings appSettings, string oldPbePath = null, string newPbePath = null, string sourceJsonPath = null)
        {
            InitializeComponent();
            _serviceProvider = serviceProvider;
            _customMessageBoxService = customMessageBoxService;
            _directoriesCreator = directoriesCreator;
            _assetDownloaderService = assetDownloaderService;
            _logService = logService;
            _wadDifferenceService = wadDifferenceService;
            _wadPackagingService = wadPackagingService;
            _diffViewService = diffViewService;
            _hashResolverService = hashResolverService;
            _appSettings = appSettings;
            _serializableDiffs = serializableDiffs;
            _oldPbePath = oldPbePath;
            _newPbePath = newPbePath;
            _sourceJsonPath = sourceJsonPath; // Store the path of the loaded file
            Loaded += WadComparisonResultWindow_Loaded;
            Closed += OnWindowClosed;
        }

        private void OnWindowClosed(object sender, System.EventArgs e)
        {
            Loaded -= WadComparisonResultWindow_Loaded;
            ResultsTree.SelectedItemChanged -= ResultsTree_SelectedItemChanged;
            ResultsTree.ContextMenuOpening -= ResultsTree_ContextMenuOpening;
            _serializableDiffs?.Clear();
            ResultsTree.ItemsSource = null;
            ResultsTree.Cleanup();
        }

        private void WadComparisonResultWindow_Loaded(object sender, RoutedEventArgs e)
        {
            TryResolveHashes();
            PopulateResults(_serializableDiffs);
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

        private void PopulateResults(List<SerializableChunkDiff> diffs)
        {
            var groupedByWad = diffs.GroupBy(d => d.SourceWadFile)
                                    .OrderBy(g => g.Key);

            summaryTextBlock.Text = $"Found {diffs.Count} differences across {groupedByWad.Count()} WAD files.";

            var wadGroups = groupedByWad.Select(wadGroup => new WadGroupViewModel
            {
                WadName = wadGroup.Key,
                DiffCount = wadGroup.Count(),
                Types = wadGroup.GroupBy(d => d.Type)
                                .OrderBy(g => g.Key.ToString())
                                .Select(typeGroup => new DiffTypeGroupViewModel
                                {
                                    Type = typeGroup.Key,
                                    DiffCount = typeGroup.Count(),
                                    Diffs = typeGroup.OrderBy(d => d.NewPath ?? d.OldPath).ToList()
                                }).ToList()
            }).ToList();

            ResultsTree.ItemsSource = wadGroups;
        }

        private void ResultsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            DiffDetails.DisplayDetails(e.NewValue);
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_sourceJsonPath != null)
            {
                _customMessageBoxService.ShowInfo("Info", "This result is already saved.", this);
                return;
            }

            try
            {
                _directoriesCreator.GenerateNewWadComparisonPaths();

                string comparisonFullPath = _directoriesCreator.WadComparisonFullPath;
                string oldChunksPath = _directoriesCreator.OldChunksPath;
                string newChunksPath = _directoriesCreator.NewChunksPath;

                _logService.Log("Starting lean WAD packaging process...");
                await _wadPackagingService.CreateLeanWadPackageAsync(_serializableDiffs, _oldPbePath, _newPbePath, oldChunksPath, newChunksPath);
                _logService.LogSuccess("Finished lean WAD packaging process.");

                string jsonFilePath = Path.Combine(comparisonFullPath, "wadcomparison.json");
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new JsonStringEnumConverter() }
                };
                var comparisonResult = new WadComparisonData
                {
                    OldLolPath = _oldPbePath,
                    NewLolPath = _newPbePath,
                    Diffs = _serializableDiffs
                };
                var json = JsonSerializer.Serialize(comparisonResult, options);
                await File.WriteAllTextAsync(jsonFilePath, json);

                // _logService.LogInteractiveInfo($"Saved comparison WAD files in {comparisonFullPath}", comparisonFullPath);
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

                PopulateResults(_serializableDiffs);
                _customMessageBoxService.ShowSuccess("Success", "Hashes have been reloaded and the result tree has been refreshed.", this);
            }
            catch (Exception ex)
            {
                _customMessageBoxService.ShowError("Error", $"Failed to reload hashes: {ex.Message}", this);
                _logService.LogError(ex, "Failed to reload hashes.");
            }
        }

        private async void ViewDifferences_Click(object sender, RoutedEventArgs e)
        {
            if (ResultsTree.SelectedItem is not SerializableChunkDiff diff) return;

            var diffViewService = _serviceProvider.GetRequiredService<DiffViewService>();
            await diffViewService.ShowWadDiffAsync(diff, _oldPbePath, _newPbePath, this);
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
    }
}