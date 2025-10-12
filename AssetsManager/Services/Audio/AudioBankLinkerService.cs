
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Explorer.Tree;
using AssetsManager.Views.Models;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

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

        public AudioBankLinkerService(
            WadExtractionService wadExtractionService,
            WadSearchBoxService wadSearchBoxService,
            LogService logService,
            TreeUIManager treeUIManager,
            TreeBuilderService treeBuilderService,
            HashResolverService hashResolverService)
        {
            _wadExtractionService = wadExtractionService;
            _wadSearchBoxService = wadSearchBoxService;
            _logService = logService;
            _treeUIManager = treeUIManager;
            _treeBuilderService = treeBuilderService;
            _hashResolverService = hashResolverService;
        }

        public async Task<LinkedAudioBank> LinkAudioBankAsync(FileSystemNodeModel clickedNode, ObservableCollection<FileSystemNodeModel> rootNodes, string currentRootPath)
        {
            // Find .bin file and data
            var (binNode, baseName, binType) = await FindAssociatedBinFileAsync(clickedNode, rootNodes, currentRootPath);
            if (binNode == null)
            {
                _logService.LogWarning($"[AUDIO] Could not find any associated .bin file for {clickedNode.Name}. Event names will be unavailable.");
            }

            var binData = await _wadExtractionService.GetVirtualFileBytesAsync(binNode);

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

        private (FileSystemNodeModel WpkNode, FileSystemNodeModel AudioBnkNode, FileSystemNodeModel EventsBnkNode) FindSiblingFilesByName(FileSystemNodeModel clickedNode, ObservableCollection<FileSystemNodeModel> rootNodes)
        {
            var parentPath = _treeUIManager.FindNodePath(rootNodes, clickedNode);
            if (parentPath == null || parentPath.Count < 2)
            {
                return (null, null, null);
            }
            var parentNode = parentPath[parentPath.Count - 2];

            bool isVo = clickedNode.Name.Contains("_vo_");
            string baseName = clickedNode.Name.Replace("_audio.wpk", "").Replace("_audio.bnk", "").Replace("_events.bnk", "");

            FileSystemNodeModel wpkNode = isVo ? parentNode.Children.FirstOrDefault(c => c.Name == baseName + "_audio.wpk") : null;
            FileSystemNodeModel audioBnkNode = parentNode.Children.FirstOrDefault(c => c.Name == baseName + "_audio.bnk");
            FileSystemNodeModel eventsBnkNode = parentNode.Children.FirstOrDefault(c => c.Name == baseName + "_events.bnk");

            return (wpkNode, audioBnkNode, eventsBnkNode);
        }

        private async Task<(FileSystemNodeModel BinNode, string BaseName, BinType Type)> FindAssociatedBinFileAsync(FileSystemNodeModel clickedNode, ObservableCollection<FileSystemNodeModel> rootNodes, string currentRootPath)
        {
            var parentPath = _treeUIManager.FindNodePath(rootNodes, clickedNode);
            var wadRoot = parentPath?.FirstOrDefault(p => p.Type == NodeType.WadFile);
            string baseName = clickedNode.Name.Replace("_audio.wpk", "").Replace("_audio.bnk", "").Replace("_events.bnk", "");
            string sourceWadName = Path.GetFileName(clickedNode.SourceWadPath);
            Func<FileSystemNodeModel, Task> loader = async (node) => await LoadAllChildrenForSearch(node, currentRootPath);

            BinType detectedType;
            if (clickedNode.FullPath.Contains("/characters/") && clickedNode.FullPath.Contains("/skins/"))
            {
                detectedType = BinType.Champion;
            }
            else if (sourceWadName.StartsWith("Map") || sourceWadName.StartsWith("Common"))
            {
                detectedType = BinType.Map;
            }
            else
            {
                detectedType = BinType.Unknown;
            }

            switch (detectedType)
            {
                case BinType.Champion:
                    var pathParts = clickedNode.FullPath.Split('/');
                    string championName = pathParts.FirstOrDefault(p => pathParts.ToList().IndexOf(p) > pathParts.ToList().IndexOf("characters") && pathParts.ToList().IndexOf(p) < pathParts.ToList().IndexOf("skins"));
                    string skinFolder = pathParts.FirstOrDefault(p => pathParts.ToList().IndexOf(p) > pathParts.ToList().IndexOf("skins"));
                    if (!string.IsNullOrEmpty(championName) && !string.IsNullOrEmpty(skinFolder))
                    {
                        string skinName = (skinFolder == "base") ? "skin0" : $"skin{int.Parse(skinFolder.Replace("skin", ""))}";
                        string binPath = $"data/characters/{championName}/skins/{skinName}.bin";
                        string[] wadNameParts = sourceWadName.Split('.');
                        string targetWadName = wadNameParts.Length > 3 ? $"{wadNameParts[0]}.{wadNameParts[2]}.{wadNameParts[3]}" : sourceWadName;
                        var targetWadNode = FindNodeByName(rootNodes, targetWadName);
                        if (targetWadNode != null)
                        {
                            var binNode = await _wadSearchBoxService.PerformSearchAsync(binPath, new ObservableCollection<FileSystemNodeModel> { targetWadNode }, loader);
                            if (binNode != null) return (binNode, baseName, BinType.Champion);
                        }
                    }
                    break;

                case BinType.Map:
                    _logService.Log($"[AUDIO_STRATEGY] Detected Map WAD: {sourceWadName}");
                    string[] mapWadNameParts = sourceWadName.Split('.');
                    string mapName = mapWadNameParts[0];
                    _logService.Log($"[AUDIO_STRATEGY] Extracted Map Name: {mapName}");

                    if (!string.IsNullOrEmpty(mapName))
                    {
                        string binPath = $"data/maps/shipping/{mapName.ToLower()}/{mapName.ToLower()}.bin";
                        _logService.Log($"[AUDIO_STRATEGY] Constructed .bin Path: {binPath}");

                        string targetWadName = mapWadNameParts.Length > 3 ? $"{mapWadNameParts[0]}.{mapWadNameParts[2]}.{mapWadNameParts[3]}" : sourceWadName;
                        _logService.Log($"[AUDIO_STRATEGY] Target WAD for search: {targetWadName}");

                        var targetWadNode = FindNodeByName(rootNodes, targetWadName);
                        if (targetWadNode != null)
                        {
                            var binNode = await _wadSearchBoxService.PerformSearchAsync(binPath, new ObservableCollection<FileSystemNodeModel> { targetWadNode }, loader);
                            if (binNode != null) return (binNode, baseName, BinType.Map);
                        }
                    }
                    break;
            }

            // Fallback Audio
            if (wadRoot != null)
            {
                string binName = baseName + ".bin";
                _logService.Log($"[AUDIO] Specific BIN not found, attempting global search for '{binName}' in WAD '{wadRoot.Name}'");
                var binNode = await _wadSearchBoxService.PerformSearchAsync(binName, new ObservableCollection<FileSystemNodeModel> { wadRoot }, loader);
                if (binNode != null) return (binNode, baseName, BinType.Unknown);
            }

            return (null, baseName, BinType.Unknown);
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

    public enum BinType { Champion, Map, Unknown }

    public class LinkedAudioBank
    {
        public FileSystemNodeModel WpkNode { get; set; }
        public FileSystemNodeModel AudioBnkNode { get; set; }
        public FileSystemNodeModel EventsBnkNode { get; set; }
        public byte[] BinData { get; set; }
        public string BaseName { get; set; }
        public BinType BinType { get; set; }
    }
}
