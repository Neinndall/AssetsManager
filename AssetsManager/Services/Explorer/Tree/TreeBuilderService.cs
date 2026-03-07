using System;
using System.Windows;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;
using AssetsManager.Services.Audio;
using AssetsManager.Views.Models.Audio;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Services.Explorer.Tree
{
    public class TreeBuilderService
    {
        private readonly WadNodeLoaderService _wadNodeLoaderService;
        private readonly HashResolverService _hashResolverService;
        private readonly LogService _logService;
        private readonly AudioBankLinkerService _audioBankLinkerService;
        private readonly WadExtractionService _wadExtractionService;
        private readonly AudioBankService _audioBankService;

        public TreeBuilderService(
            WadNodeLoaderService wadNodeLoaderService, 
            HashResolverService hashResolverService, 
            LogService logService,
            AudioBankLinkerService audioBankLinkerService,
            WadExtractionService wadExtractionService,
            AudioBankService audioBankService)
        {
            _wadNodeLoaderService = wadNodeLoaderService;
            _hashResolverService = hashResolverService;
            _logService = logService;
            _audioBankLinkerService = audioBankLinkerService;
            _wadExtractionService = wadExtractionService;
            _audioBankService = audioBankService;
        }

        public async Task<ObservableRangeCollection<FileSystemNodeModel>> BuildWadTreeAsync(string rootPath, CancellationToken cancellationToken)
        {
            var rootNodes = new ObservableRangeCollection<FileSystemNodeModel>();

            string gamePath = Path.Combine(rootPath, "Game");
            if (Directory.Exists(gamePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var gameNode = new FileSystemNodeModel(gamePath);
                rootNodes.Add(gameNode);
                await _wadNodeLoaderService.EnsureAllChildrenLoadedAsync(gameNode, rootPath, cancellationToken);
            }

            string pluginsPath = Path.Combine(rootPath, "Plugins");
            if (Directory.Exists(pluginsPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pluginsNode = new FileSystemNodeModel(pluginsPath);
                rootNodes.Add(pluginsNode);
                await _wadNodeLoaderService.EnsureAllChildrenLoadedAsync(pluginsNode, rootPath, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            for (int i = rootNodes.Count - 1; i >= 0; i--)
            {
                if (!PruneEmptyDirectories(rootNodes[i]))
                {
                    rootNodes.RemoveAt(i);
                }
            }

            return rootNodes;
        }

        public async Task<ObservableRangeCollection<FileSystemNodeModel>> BuildDirectoryTreeAsync(string rootPath, CancellationToken cancellationToken)
        {
            var nodes = await _wadNodeLoaderService.LoadDirectoryAsync(rootPath, cancellationToken);
            return new ObservableRangeCollection<FileSystemNodeModel>(nodes);
        }

        public async Task<(ObservableRangeCollection<FileSystemNodeModel> Nodes, string NewLolPath, string OldLolPath)> BuildTreeFromBackupAsync(string jsonPath, bool isSortingEnabled, CancellationToken cancellationToken)
        {
            var (nodes, newLolPath, oldLolPath) = await _wadNodeLoaderService.LoadFromBackupAsync(jsonPath, isSortingEnabled, cancellationToken);
            return (new ObservableRangeCollection<FileSystemNodeModel>(nodes), newLolPath, oldLolPath);
        }

        public async Task ExpandAudioBankAsync(FileSystemNodeModel clickedNode, ObservableRangeCollection<FileSystemNodeModel> rootNodes, string currentRootPath, string newLolPath = null, string oldLolPath = null)
        {
            var linkedBank = await _audioBankLinkerService.LinkAudioBankAsync(clickedNode, rootNodes, currentRootPath, newLolPath, oldLolPath);
            if (linkedBank == null)
            {
                return; // Errors are logged by the service
            }

            // Read other file data from the WAD.
            var eventsData = linkedBank.EventsBnkNode != null ? await _wadExtractionService.GetVirtualFileBytesAsync(linkedBank.EventsBnkNode) : null;
            byte[] wpkData = linkedBank.WpkNode != null ? await _wadExtractionService.GetVirtualFileBytesAsync(linkedBank.WpkNode) : null;
            byte[] audioBnkFileData = linkedBank.AudioBnkNode != null ? await _wadExtractionService.GetVirtualFileBytesAsync(linkedBank.AudioBnkNode) : null;

            List<AudioEventNode> audioTree;
            if (linkedBank.BinData != null)
            {
                // BIN-based parsing (Champions, Maps)
                if (wpkData != null)
                {
                    audioTree = _audioBankService.ParseAudioBank(wpkData, audioBnkFileData, eventsData, linkedBank.BinData, linkedBank.BaseName, linkedBank.BinType);
                }
                else
                {
                    audioTree = _audioBankService.ParseSfxAudioBank(audioBnkFileData, eventsData, linkedBank.BinData, linkedBank.BaseName, linkedBank.BinType);
                }
            }
            else
            {
                // Generic parsing (no BIN file)
                audioTree = _audioBankService.ParseGenericAudioBank(wpkData, audioBnkFileData, eventsData);
            }

            // 5. Populate the tree view with the results.
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                clickedNode.Children.Clear();

                // Determine the absolute source WAD path for the child sound nodes.
                string absoluteSourceWadPath;
                if (clickedNode.ChunkDiff != null && (!string.IsNullOrEmpty(newLolPath) || !string.IsNullOrEmpty(oldLolPath)))
                {
                    // Backup mode: construct the absolute path from the base LoL directory and the relative WAD path.
                    string basePath = clickedNode.ChunkDiff.Type == ChunkDiffType.Removed ? oldLolPath : newLolPath;
                    absoluteSourceWadPath = Path.Combine(basePath, clickedNode.SourceWadPath);
                }
                else
                {
                    // Normal mode: the SourceWadPath should already be absolute.
                    absoluteSourceWadPath = clickedNode.SourceWadPath;
                }

                var eventNodesToAdd = new List<FileSystemNodeModel>();
                foreach (var eventNode in audioTree)
                {
                    var newEventNode = new FileSystemNodeModel(eventNode.Name, NodeType.AudioEvent);
                    var soundNodesToAdd = new List<FileSystemNodeModel>();
                    foreach (var soundNode in eventNode.Sounds)
                    {
                        // Determine the correct source file (WPK or BNK) for the sound.
                        AudioSourceType sourceType;
                        ulong sourceHash;
                        if (linkedBank.WpkNode != null)
                        {
                            sourceType = AudioSourceType.Wpk;
                            sourceHash = linkedBank.WpkNode.SourceChunkPathHash;
                        }
                        else
                        {
                            sourceType = AudioSourceType.Bnk;
                            sourceHash = linkedBank.AudioBnkNode.SourceChunkPathHash;
                        }

                        var newSoundNode = new FileSystemNodeModel(soundNode.Name, soundNode.Id, soundNode.Offset, soundNode.Size)
                        {
                            SourceWadPath = absoluteSourceWadPath,
                            SourceChunkPathHash = sourceHash,
                            AudioSource = sourceType
                        };
                        soundNodesToAdd.Add(newSoundNode);
                    }
                    newEventNode.Children.AddRange(soundNodesToAdd);
                    eventNodesToAdd.Add(newEventNode);
                }
                clickedNode.Children.AddRange(eventNodesToAdd);
                clickedNode.IsExpanded = true;
            });
        }

        public async Task EnsureAllChildrenLoadedAsync(FileSystemNodeModel node, string currentRootPath, CancellationToken cancellationToken = default)
        {
            await _wadNodeLoaderService.EnsureAllChildrenLoadedAsync(node, currentRootPath, cancellationToken);
        }

        private bool PruneEmptyDirectories(FileSystemNodeModel node)
        {
            if (node.Type != NodeType.RealDirectory)
            {
                return true; // Keep files
            }

            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                if (!PruneEmptyDirectories(node.Children[i]))
                {
                    node.Children.RemoveAt(i);
                }
            }

            return node.Children.Any();
        }
    }
}
