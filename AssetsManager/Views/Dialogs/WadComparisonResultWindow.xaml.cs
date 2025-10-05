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
using AssetsManager.Services.Monitor;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Explorer;

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
        private readonly WadExtractionService _wadExtractionService;
        private readonly AppSettings _appSettings;
        private readonly string _oldPbePath;
        private readonly string _newPbePath;
        private readonly string _sourceJsonPath; // Path to the loaded wadcomparison.json

        public WadComparisonResultWindow(List<ChunkDiff> diffs, IServiceProvider serviceProvider, CustomMessageBoxService customMessageBoxService, DirectoriesCreator directoriesCreator, AssetDownloader assetDownloaderService, LogService logService, WadDifferenceService wadDifferenceService, WadPackagingService wadPackagingService, DiffViewService diffViewService, HashResolverService hashResolverService, WadExtractionService wadExtractionService, AppSettings appSettings, string oldPbePath, string newPbePath)
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
            _wadExtractionService = wadExtractionService;
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
            PopulateResults(_serializableDiffs);
        }

        public WadComparisonResultWindow(List<SerializableChunkDiff> serializableDiffs, IServiceProvider serviceProvider, CustomMessageBoxService customMessageBoxService, DirectoriesCreator directoriesCreator, AssetDownloader assetDownloaderService, LogService logService, WadDifferenceService wadDifferenceService, WadPackagingService wadPackagingService, DiffViewService diffViewService, HashResolverService hashResolverService, WadExtractionService wadExtractionService, AppSettings appSettings, string oldPbePath = null, string newPbePath = null, string sourceJsonPath = null)
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
            _wadExtractionService = wadExtractionService;
            _appSettings = appSettings;
            _serializableDiffs = serializableDiffs;
            _oldPbePath = oldPbePath;
            _newPbePath = newPbePath;
            _sourceJsonPath = sourceJsonPath; // Store the path of the loaded file
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
                await _hashResolverService.LoadHashesAsync();
                await _hashResolverService.LoadBinHashesAsync();
                await _hashResolverService.LoadRstHashesAsync();

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

            if (ResultsTree.ExtractMenuItem is MenuItem extractMenuItem)
            {
                extractMenuItem.IsEnabled = GetExtractableDiffsFromSelection().Any();
            }
        }

        private async void ExtractMenuItem_Click(object sender, RoutedEventArgs e)
        {
            List<SerializableChunkDiff> diffsToExtract = GetExtractableDiffsFromSelection();
            if (!diffsToExtract.Any())
            {
                _customMessageBoxService.ShowInfo("Info", "No extractable files (New, Modified, Renamed, or Removed) in the current selection.", this);
                return;
            }

            string rootDestinationPath = null;
            if (!string.IsNullOrEmpty(_appSettings.DefaultExtractedSelectDirectory) && Directory.Exists(_appSettings.DefaultExtractedSelectDirectory))
            {
                rootDestinationPath = _appSettings.DefaultExtractedSelectDirectory;
            }
            else
            {
                var dialog = new Microsoft.WindowsAPICodePack.Dialogs.CommonOpenFileDialog
                {
                    IsFolderPicker = true,
                    Title = "Select Destination Folder for Extraction"
                };

                if (dialog.ShowDialog() == Microsoft.WindowsAPICodePack.Dialogs.CommonFileDialogResult.Ok)
                {
                    rootDestinationPath = dialog.FileName;
                }
            }

            if (rootDestinationPath != null)
            {
                if (ResultsTree.SelectedItem is WadGroupViewModel selectedWad)
                {
                    rootDestinationPath = Path.Combine(rootDestinationPath, selectedWad.WadName);
                    Directory.CreateDirectory(rootDestinationPath);
                }

                _logService.Log("Extracting selected files...");
                int successCount = 0;

                foreach (var diff in diffsToExtract)
                {
                    try
                    {
                        string typeFolder = diff.Type.ToString();
                        string finalDestinationPath = Path.Combine(rootDestinationPath, typeFolder);
                        Directory.CreateDirectory(finalDestinationPath);

                        string basePath = (diff.Type == ChunkDiffType.Removed) ? _oldPbePath : _newPbePath;
                        string sourceWadPath = Path.Combine(basePath, diff.SourceWadFile);

                        var node = new FileSystemNodeModel(diff.FileName, false, diff.Path, sourceWadPath)
                        {
                            SourceChunkPathHash = (diff.Type == ChunkDiffType.Removed) ? diff.OldPathHash : diff.NewPathHash,
                            ChunkDiff = diff,
                            Status = (DiffStatus)diff.Type
                        };

                        await _wadExtractionService.ExtractNodeAsync(node, finalDestinationPath);
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        _logService.LogError(ex, $"Failed to extract {diff.FileName}");
                    }
                }

                if (successCount == diffsToExtract.Count)
                {
                    _customMessageBoxService.ShowSuccess("Success", "Successfully extracted selected assets.");
                    _logService.LogInteractiveSuccess($"Successfully extracted to {rootDestinationPath}", rootDestinationPath);
                }
                else
                {
                    _customMessageBoxService.ShowWarning("Partial Success", $"Successfully extracted {successCount} out of {diffsToExtract.Count} asset(s). Check logs for details.");
                }
            }
        }

        private List<SerializableChunkDiff> GetExtractableDiffsFromSelection()
        {
            var selectedItem = ResultsTree.SelectedItem;
            var downloadableDiffs = new List<SerializableChunkDiff>();

            if (selectedItem is SerializableChunkDiff singleDiff && (singleDiff.Type == ChunkDiffType.New || singleDiff.Type == ChunkDiffType.Modified || singleDiff.Type == ChunkDiffType.Renamed || singleDiff.Type == ChunkDiffType.Removed))
            {
                downloadableDiffs.Add(singleDiff);
            }
            else if (selectedItem is DiffTypeGroupViewModel typeGroup && (typeGroup.Type == ChunkDiffType.New || typeGroup.Type == ChunkDiffType.Modified || typeGroup.Type == ChunkDiffType.Renamed || typeGroup.Type == ChunkDiffType.Removed))
            {
                downloadableDiffs.AddRange(typeGroup.Diffs);
            }
            else if (selectedItem is WadGroupViewModel wadGroup)
            {
                downloadableDiffs.AddRange(wadGroup.Types
                    .Where(t => t.Type == ChunkDiffType.New || t.Type == ChunkDiffType.Modified || t.Type == ChunkDiffType.Renamed || t.Type == ChunkDiffType.Removed)
                    .SelectMany(t => t.Diffs));
            }

            return downloadableDiffs;
        }
    }
}