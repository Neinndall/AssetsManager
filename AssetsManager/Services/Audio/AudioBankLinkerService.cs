using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Explorer.Tree;
using AssetsManager.Views.Models;

namespace AssetsManager.Services.Audio
{
    public class AudioBankLinkerService
    {
        private readonly WadExtractionService _wadExtractionService;
        private readonly WadSearchBoxService _wadSearchBoxService;
        private readonly LogService _logService;
        private readonly TreeUIManager _treeUIManager;
        private readonly TreeBuilderService _treeBuilderService;
        private readonly HashResolverService _hashResolverService;
        private readonly WadNodeLoaderService _wadNodeLoaderService;

        public AudioBankLinkerService(
            WadExtractionService wadExtractionService,
            WadSearchBoxService wadSearchBoxService,
            LogService logService,
            TreeUIManager treeUIManager,
            TreeBuilderService treeBuilderService,
            HashResolverService hashResolverService,
            WadNodeLoaderService wadNodeLoaderService)
        {
            _wadExtractionService = wadExtractionService;
            _wadSearchBoxService = wadSearchBoxService;
            _logService = logService;
            _treeUIManager = treeUIManager;
            _treeBuilderService = treeBuilderService;
            _hashResolverService = hashResolverService;
            _wadNodeLoaderService = wadNodeLoaderService;
        }

        public async Task<LinkedAudioBank> LinkAudioBankAsync(FileSystemNodeModel clickedNode, ObservableCollection<FileSystemNodeModel> rootNodes, string currentRootPath, string newLolPath = null, string oldLolPath = null)
        {
            if (clickedNode.ChunkDiff != null && (!string.IsNullOrEmpty(newLolPath) || !string.IsNullOrEmpty(oldLolPath)))
            {
                // Backup Mode: Paths are provided
                string basePath = clickedNode.ChunkDiff.Type == ChunkDiffType.New || clickedNode.ChunkDiff.Type == ChunkDiffType.Modified || clickedNode.ChunkDiff.Type == ChunkDiffType.Renamed
                    ? newLolPath
                    : oldLolPath;

                if (string.IsNullOrEmpty(basePath))
                {
                    _logService.LogWarning($"[AUDIO] Could not determine base path for backup mode audio linking. Node: {clickedNode.Name}");
                    return null;
                }
                _logService.Log($"[AUDIO_BACKUP] Determined base path: {basePath}");

                var (binNode, baseName, binType) = await FindAssociatedBinFileFromWadsAsync(clickedNode, basePath);
                byte[] binData = null;
                if (binNode != null)
                {
                    binData = await _wadExtractionService.GetVirtualFileBytesAsync(binNode);
                }
                else
                {
                    _logService.LogWarning($"[AUDIO] Could not find any associated .bin file for {clickedNode.Name} in backup mode. Event names will be unavailable.");
                }

                var siblingsResult = await FindSiblingFilesFromWadsAsync(clickedNode, basePath);

                return new LinkedAudioBank
                {
                    WpkNode = siblingsResult.WpkNode,
                    AudioBnkNode = siblingsResult.AudioBnkNode,
                    EventsBnkNode = siblingsResult.EventsBnkNode,
                    BinData = binData,
                    BaseName = baseName,
                    BinType = binType
                };
            }
            else
            {
                // Normal Mode
                var (binNode, baseName, binType) = await FindAssociatedBinFileAsync(clickedNode, rootNodes, currentRootPath);
                byte[] binData = null;
                if (binNode != null)
                {
                    binData = await _wadExtractionService.GetVirtualFileBytesAsync(binNode);
                }
                else
                {
                    _logService.LogWarning($"[AUDIO] Could not find any associated .bin file for {clickedNode.Name}. Event names will be unavailable.");
                }

                var siblingsResult = FindSiblingFilesByName(clickedNode, rootNodes);

                return new LinkedAudioBank
                {
                    WpkNode = siblingsResult.WpkNode,
                    AudioBnkNode = siblingsResult.AudioBnkNode,
                    EventsBnkNode = siblingsResult.EventsBnkNode,
                    BinData = binData,
                    BaseName = baseName,
                    BinType = binType
                };
            }
        }

        private async Task<(FileSystemNodeModel WpkNode, FileSystemNodeModel AudioBnkNode, FileSystemNodeModel EventsBnkNode)> FindSiblingFilesFromWadsAsync(FileSystemNodeModel clickedNode, string basePath)
        {
            string baseName = clickedNode.Name.Replace("_audio.wpk", "").Replace("_audio.bnk", "").Replace("_events.bnk", "");
            string wadPath = Path.Combine(basePath, clickedNode.SourceWadPath);
            _logService.Log($"[AUDIO_BACKUP] Attempting to load siblings from WAD: {wadPath}");

            if (!File.Exists(wadPath))
            {
                _logService.LogWarning($"[AUDIO] Source WAD file not found in backup mode: {wadPath}");
                return (null, null, null);
            }

            var wadContent = await _wadNodeLoaderService.LoadWadContentAsync(wadPath);

            FileSystemNodeModel wpkNode = wadContent.FirstOrDefault(c => c.Name == baseName + "_audio.wpk");
            FileSystemNodeModel audioBnkNode = wadContent.FirstOrDefault(c => c.Name == baseName + "_audio.bnk");
            FileSystemNodeModel eventsBnkNode = wadContent.FirstOrDefault(c => c.Name == baseName + "_events.bnk");

            return (wpkNode, audioBnkNode, eventsBnkNode);
        }

        private record BinFileStrategy(string BinPath, string TargetWadName, BinType Type);

        private BinFileStrategy GetBinFileSearchStrategy(FileSystemNodeModel clickedNode)
        {
            string sourceWadName = Path.GetFileName(clickedNode.SourceWadPath);

            if (clickedNode.FullPath.Contains("/characters/") && clickedNode.FullPath.Contains("/skins/"))
            {
                var pathParts = clickedNode.FullPath.Split('/');
                string championName = pathParts.FirstOrDefault(p => pathParts.ToList().IndexOf(p) > pathParts.ToList().IndexOf("characters") && pathParts.ToList().IndexOf(p) < pathParts.ToList().IndexOf("skins"));
                string skinFolder = pathParts.FirstOrDefault(p => pathParts.ToList().IndexOf(p) > pathParts.ToList().IndexOf("skins"));

                if (!string.IsNullOrEmpty(championName) && !string.IsNullOrEmpty(skinFolder))
                {
                    string skinName = (skinFolder == "base") ? "skin0" : $"skin{int.Parse(skinFolder.Replace("skin", ""))}";
                    string binPath = $"data/characters/{championName}/skins/{skinName}.bin";
                    string targetWadName = $"{championName.ToLower()}.wad.client";
                    return new BinFileStrategy(binPath, targetWadName, BinType.Champion);
                }
            }
            else if (sourceWadName.StartsWith("Map") || sourceWadName.StartsWith("Common"))
            {
                string[] mapWadNameParts = sourceWadName.Split('.');
                string mapName = mapWadNameParts[0];
                if (!string.IsNullOrEmpty(mapName))
                {
                    string binPath = $"data/maps/shipping/{mapName.ToLower()}/{mapName.ToLower()}.bin";
                    string targetWadName = $"{mapName.ToLower()}.wad.client";
                    return new BinFileStrategy(binPath, targetWadName, BinType.Map);
                }
            }

            // For any other case, we don't have a reliable way to find the .bin file.
            return null;
        }

        private async Task<(FileSystemNodeModel BinNode, string BaseName, BinType Type)> FindAssociatedBinFileFromWadsAsync(FileSystemNodeModel clickedNode, string basePath)
        {
            string baseName = clickedNode.Name.Replace("_audio.wpk", "").Replace("_audio.bnk", "").Replace("_events.bnk", "");
            var strategy = GetBinFileSearchStrategy(clickedNode);

            if (strategy == null)
            {
                return (null, baseName, BinType.Unknown);
            }

            string wadDirectory;
            if (strategy.Type == BinType.Champion)
            {
                wadDirectory = basePath;
            }
            else if (strategy.Type == BinType.Map)
            {
                wadDirectory = basePath;
            }
            else
            {
                // For unknown types, the WAD path is the source WAD path of the clicked node itself.
                wadDirectory = Path.GetDirectoryName(Path.Combine(basePath, clickedNode.SourceWadPath));
            }

            string targetWadFullPath = Path.Combine(wadDirectory, strategy.TargetWadName);
            _logService.Log($"[AUDIO_BACKUP] Attempting to load BIN from WAD: {targetWadFullPath}");

            if (File.Exists(targetWadFullPath))
            {
                var wadContent = await _wadNodeLoaderService.LoadWadContentAsync(targetWadFullPath);
                var binNode = wadContent.FirstOrDefault(n => n.FullPath.Equals(strategy.BinPath, StringComparison.OrdinalIgnoreCase));
                if (binNode != null) return (binNode, baseName, strategy.Type);
            }
            else
            {
                _logService.LogWarning($"[AUDIO] Could not find target WAD for BIN search in backup mode: {targetWadFullPath}");
            }

            return (null, baseName, strategy.Type);
        }

        private (FileSystemNodeModel WpkNode, FileSystemNodeModel AudioBnkNode, FileSystemNodeModel EventsBnkNode) FindSiblingFilesByName(FileSystemNodeModel clickedNode, ObservableCollection<FileSystemNodeModel> rootNodes)
        {
            var parentPath = _treeUIManager.FindNodePath(rootNodes, clickedNode);
            if (parentPath == null || parentPath.Count < 2)
            {
                return (null, null, null);
            }
            var parentNode = parentPath[parentPath.Count - 2];

            string baseName = clickedNode.Name.Replace("_audio.wpk", "").Replace("_audio.bnk", "").Replace("_events.bnk", "");

            FileSystemNodeModel wpkNode = parentNode.Children.FirstOrDefault(c => c.Name == baseName + "_audio.wpk");
            FileSystemNodeModel audioBnkNode = parentNode.Children.FirstOrDefault(c => c.Name == baseName + "_audio.bnk");
            FileSystemNodeModel eventsBnkNode = parentNode.Children.FirstOrDefault(c => c.Name == baseName + "_events.bnk");

            return (wpkNode, audioBnkNode, eventsBnkNode);
        }

        private async Task<(FileSystemNodeModel BinNode, string BaseName, BinType Type)> FindAssociatedBinFileAsync(FileSystemNodeModel clickedNode, ObservableCollection<FileSystemNodeModel> rootNodes, string currentRootPath)
        {
            string baseName = clickedNode.Name.Replace("_audio.wpk", "").Replace("_audio.bnk", "").Replace("_events.bnk", "");
            var strategy = GetBinFileSearchStrategy(clickedNode);

            if (strategy == null)
            {
                return (null, baseName, BinType.Unknown);
            }

            Func<FileSystemNodeModel, Task> loader = async (node) => await LoadAllChildrenForSearch(node, currentRootPath);

            var targetWadNode = FindNodeByName(rootNodes, strategy.TargetWadName);
            if (targetWadNode != null)
            {
                var binNode = await _wadSearchBoxService.PerformSearchAsync(strategy.BinPath, new ObservableCollection<FileSystemNodeModel> { targetWadNode }, loader);
                if (binNode != null) return (binNode, baseName, strategy.Type);
            }

            return (null, baseName, strategy.Type);        
        }

        private async Task LoadAllChildrenForSearch(FileSystemNodeModel node, string rootPath)
        {
            await _treeBuilderService.LoadAllChildren(node, rootPath);
        }

        private FileSystemNodeModel FindNodeByName(IEnumerable<FileSystemNodeModel> nodes, string name)
        {
            foreach (var node in nodes)
            {
                if (node.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }

                if (node.Children != null && node.Children.Any())
                {
                    var found = FindNodeByName(node.Children, name);
                    if (found != null)
                    {
                        return found;
                    }
                }
            }
            return null;
        }
    }
}
