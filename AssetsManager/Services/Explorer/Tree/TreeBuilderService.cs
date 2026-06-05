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
using AssetsManager.Views.Models.Settings;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Services.Explorer.Tree
{
    public class TreeBuilderService
    {
        private readonly WadNodeLoaderService _wadNodeLoaderService;
        private readonly HashResolverService _hashResolverService;
        private readonly LogService _logService;
        private readonly AudioBankLinkerService _audioBankLinkerService;
        private readonly WadContentProvider _wadContentProvider;
        private readonly AudioBankService _audioBankService;

        public TreeBuilderService(
            WadNodeLoaderService wadNodeLoaderService, 
            HashResolverService hashResolverService, 
            LogService logService,
            AudioBankLinkerService audioBankLinkerService,
            WadContentProvider wadContentProvider,
            AudioBankService audioBankService)
        {
            _wadNodeLoaderService = wadNodeLoaderService;
            _hashResolverService = hashResolverService;
            _logService = logService;
            _audioBankLinkerService = audioBankLinkerService;
            _wadContentProvider = wadContentProvider;
            _audioBankService = audioBankService;
        }

        public async Task<ObservableRangeCollection<FileSystemNodeModel>> BuildWadTreeAsync(string rootPath, CancellationToken cancellationToken, PreferredDirectory preferredDirectory = PreferredDirectory.All, Action<string> onScanningProgress = null, Action<string> onMountingProgress = null)
        {
            var rootNodes = new ObservableRangeCollection<FileSystemNodeModel>();

            if (preferredDirectory == PreferredDirectory.All || preferredDirectory == PreferredDirectory.Game)
            {
                string gamePath = Path.Combine(rootPath, "Game");
                if (Directory.Exists(gamePath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var gameNode = new FileSystemNodeModel(gamePath);
                    rootNodes.Add(gameNode);
                    await _wadNodeLoaderService.EnsureAllChildrenLoadedAsync(gameNode, rootPath, cancellationToken, onScanningProgress, onMountingProgress);
                }
            }

            if (preferredDirectory == PreferredDirectory.All || preferredDirectory == PreferredDirectory.Plugins)
            {
                string pluginsPath = Path.Combine(rootPath, "Plugins");
                if (Directory.Exists(pluginsPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pluginsNode = new FileSystemNodeModel(pluginsPath);
                    rootNodes.Add(pluginsNode);
                    await _wadNodeLoaderService.EnsureAllChildrenLoadedAsync(pluginsNode, rootPath, cancellationToken, onScanningProgress, onMountingProgress);
                }
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

        public async Task<ObservableRangeCollection<FileSystemNodeModel>> BuildDirectoryTreeAsync(string rootPath, CancellationToken cancellationToken, Action<string> onScanningProgress = null, Action<string> onMountingProgress = null)
        {
            var nodes = await _wadNodeLoaderService.LoadDirectoryAsync(rootPath, cancellationToken, onScanningProgress, onMountingProgress);
            return new ObservableRangeCollection<FileSystemNodeModel>(nodes);
        }

        public async Task<(ObservableRangeCollection<FileSystemNodeModel> Nodes, string NewLolPath, string OldLolPath)> BuildTreeFromBackupAsync(string jsonPath, bool isSortingEnabled, CancellationToken cancellationToken)
        {
            var (nodes, newLolPath, oldLolPath) = await _wadNodeLoaderService.LoadFromBackupAsync(jsonPath, isSortingEnabled, cancellationToken);
            return (new ObservableRangeCollection<FileSystemNodeModel>(nodes), newLolPath, oldLolPath);
        }

        public async Task ExpandAudioBankAsync(FileSystemNodeModel clickedNode, ObservableRangeCollection<FileSystemNodeModel> rootNodes, string currentRootPath, string newLolPath = null, string oldLolPath = null)
        {
            var linkedBank = await _audioBankLinkerService.LinkAudioBankAsync(clickedNode, rootNodes, currentRootPath);
            if (linkedBank == null)
            {
                return; // Errors are logged by the service
            }

            // Read other file data from the WAD in parallel (3 independent I/O operations).
            var eventsTask = linkedBank.EventsBnkNode != null ? _wadContentProvider.GetVirtualFileBytesAsync(linkedBank.EventsBnkNode) : Task.FromResult<byte[]>(null);
            var wpkTask = linkedBank.WpkNode != null ? _wadContentProvider.GetVirtualFileBytesAsync(linkedBank.WpkNode) : Task.FromResult<byte[]>(null);
            var audioBnkTask = linkedBank.AudioBnkNode != null ? _wadContentProvider.GetVirtualFileBytesAsync(linkedBank.AudioBnkNode) : Task.FromResult<byte[]>(null);

            await Task.WhenAll(eventsTask, wpkTask, audioBnkTask);
            var eventsData = eventsTask.Result;
            byte[] wpkData = wpkTask.Result;
            byte[] audioBnkFileData = audioBnkTask.Result;

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
                string absoluteSourceWadPath = null;
                bool isBackup = clickedNode.ChunkDiff != null && (!string.IsNullOrEmpty(newLolPath) || !string.IsNullOrEmpty(oldLolPath));

                if (!isBackup)
                {
                    absoluteSourceWadPath = clickedNode.SourceWadPath;
                }

                var eventNodesToAdd = new List<FileSystemNodeModel>();
                foreach (var eventNode in audioTree)
                {
                    var newEventNode = new FileSystemNodeModel(eventNode.Name, NodeType.AudioEvent)
                    {
                        Parent = clickedNode
                    };

                    var soundNodesToAdd = new List<FileSystemNodeModel>();
                    foreach (var soundNode in eventNode.Sounds)
                    {
                        // Determine the correct source metadata.
                        ulong sourceHash;
                        string backupChunkPath = null;
                        SerializableChunkDiff soundDiff = null;

                        if (soundNode.Source == AudioSourceType.Wpk && linkedBank.WpkNode != null)
                        {
                            sourceHash = linkedBank.WpkNode.SourceChunkPathHash;
                            backupChunkPath = linkedBank.WpkNode.BackupChunkPath;
                            soundDiff = linkedBank.WpkNode.ChunkDiff;
                        }
                        else if (linkedBank.AudioBnkNode != null)
                        {
                            sourceHash = linkedBank.AudioBnkNode.SourceChunkPathHash;
                            backupChunkPath = linkedBank.AudioBnkNode.BackupChunkPath;
                            soundDiff = linkedBank.AudioBnkNode.ChunkDiff;
                        }
                        else
                        {
                            sourceHash = clickedNode.SourceChunkPathHash;
                            backupChunkPath = clickedNode.BackupChunkPath;
                            soundDiff = clickedNode.ChunkDiff;
                        }

                        var newSoundNode = new FileSystemNodeModel(soundNode.Name, soundNode.Id, soundNode.Offset, soundNode.Size)
                        {
                            SourceWadPath = absoluteSourceWadPath,
                            SourceChunkPathHash = sourceHash,
                            BackupChunkPath = backupChunkPath,
                            ChunkDiff = soundDiff,
                            AudioSource = soundNode.Source,
                            Parent = newEventNode
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

        public async Task EnsureAllChildrenLoadedAsync(FileSystemNodeModel node, string currentRootPath, CancellationToken cancellationToken = default, Action<string> onScanningProgress = null, Action<string> onMountingProgress = null)
        {
            await _wadNodeLoaderService.EnsureAllChildrenLoadedAsync(node, currentRootPath, cancellationToken, onScanningProgress, onMountingProgress);
        }

        private bool PruneEmptyDirectories(FileSystemNodeModel node)
        {
            if (node.Type != NodeType.RealDirectory)
            {
                return true; // Keep files
            }

            if (node.Children == null) return false;

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