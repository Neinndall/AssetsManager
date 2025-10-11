
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
            var binFindingResult = await FindAssociatedBinFileAsync(clickedNode, rootNodes, currentRootPath);
            if (binFindingResult.BinNode == null)
            {
                _logService.LogWarning($"[AUDIO] Could not find any associated .bin file for {clickedNode.Name}. Event names will be unavailable.");
            }

            var binData = await _wadExtractionService.GetVirtualFileBytesAsync(binFindingResult.BinNode);

            // Find sibling files using the new logic
            var siblingsResult = await FindSiblingFilesAsync(clickedNode, rootNodes, currentRootPath, binData);

            // Fallback to old logic if new logic fails
            if (siblingsResult.EventsBnkNode == null)
            {
                _logService.LogWarning($"[AUDIO] Could not find siblings via .bin for {clickedNode.Name}. Falling back to name-based search.");
                siblingsResult = FindSiblingFilesByName(clickedNode, rootNodes);
            }

            return new LinkedAudioBank
            {
                WpkNode = siblingsResult.WpkNode,
                AudioBnkNode = siblingsResult.AudioBnkNode,
                EventsBnkNode = siblingsResult.EventsBnkNode,
                BinData = binData,
                BaseName = binFindingResult.BaseName
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

            bool isVo = clickedNode.Name.Contains("_vo_audio");
            string baseName = clickedNode.Name.Replace("_vo_audio.wpk", "").Replace("_vo_audio.bnk", "").Replace("_vo_events.bnk", "").Replace("_sfx_audio.bnk", "").Replace("_sfx_events.bnk", "");

            FileSystemNodeModel wpkNode = isVo ? parentNode.Children.FirstOrDefault(c => c.Name == baseName + "_vo_audio.wpk") : null;
            FileSystemNodeModel audioBnkNode = isVo ? parentNode.Children.FirstOrDefault(c => c.Name == baseName + "_vo_audio.bnk") : parentNode.Children.FirstOrDefault(c => c.Name == baseName + "_sfx_audio.bnk");
            FileSystemNodeModel eventsBnkNode = isVo ? parentNode.Children.FirstOrDefault(c => c.Name == baseName + "_vo_events.bnk") : parentNode.Children.FirstOrDefault(c => c.Name == baseName + "_sfx_events.bnk");

            return (wpkNode, audioBnkNode, eventsBnkNode);
        }

        private async Task<(FileSystemNodeModel BinNode, string BaseName)> FindAssociatedBinFileAsync(FileSystemNodeModel clickedNode, ObservableCollection<FileSystemNodeModel> rootNodes, string currentRootPath)
        {
            var parentPath = _treeUIManager.FindNodePath(rootNodes, clickedNode);
            var wadRoot = parentPath?.FirstOrDefault(p => p.Type == NodeType.WadFile);
            string baseName = clickedNode.Name.Replace("_vo_audio.wpk", "").Replace("_vo_audio.bnk", "").Replace("_vo_events.bnk", "").Replace("_sfx_audio.bnk", "").Replace("_sfx_events.bnk", "");

            Func<FileSystemNodeModel, Task> loader = async (node) => await LoadAllChildrenForSearch(node, currentRootPath);

            // Strategy 1: Champion Audio
            if (clickedNode.FullPath.Contains("/characters/") && clickedNode.FullPath.Contains("/skins/"))
            {
                var pathParts = clickedNode.FullPath.Split('/');
                string championName = pathParts.FirstOrDefault(p => pathParts.ToList().IndexOf(p) > pathParts.ToList().IndexOf("characters") && pathParts.ToList().IndexOf(p) < pathParts.ToList().IndexOf("skins"));
                string skinFolder = pathParts.FirstOrDefault(p => pathParts.ToList().IndexOf(p) > pathParts.ToList().IndexOf("skins"));
                if (!string.IsNullOrEmpty(championName) && !string.IsNullOrEmpty(skinFolder))
                {
                    string skinName = (skinFolder == "base") ? "skin0" : $"skin{int.Parse(skinFolder.Replace("skin", ""))}";
                    string binPath = $"data/characters/{championName}/skins/{skinName}.bin";
                    string sourceWadName = Path.GetFileName(clickedNode.SourceWadPath);
                    string[] wadNameParts = sourceWadName.Split('.');
                    string targetWadName = wadNameParts.Length > 3 ? $"{wadNameParts[0]}.{wadNameParts[2]}.{wadNameParts[3]}" : sourceWadName;
                    var targetWadNode = FindNodeByName(rootNodes, targetWadName);
                    if (targetWadNode != null)
                    {
                        var binNode = await _wadSearchBoxService.PerformSearchAsync(binPath, new ObservableCollection<FileSystemNodeModel> { targetWadNode }, loader);
                        if (binNode != null) return (binNode, baseName);
                    }
                }
            }

            // Strategy 2: Map/Global Audio
            if (wadRoot != null)
            {
                string[] wadNameParts = wadRoot.Name.Split('.');
                string baseWadName = wadNameParts.First();
                string mainWadName = baseWadName + ".wad.client";
                var mainWadNode = FindNodeByName(rootNodes, mainWadName);

                if (mainWadNode != null)
                {
                    string binPath = $"data/maps/shipping/{baseWadName}/{baseWadName}.bin";
                    if (baseWadName.Equals("Common", StringComparison.OrdinalIgnoreCase)) binPath = $"data/maps/shipping/common/common.bin";

                    _logService.Log($"[AUDIO] Champion/Skin BIN not found, attempting map/global search for '{binPath}' in WAD '{mainWadNode.Name}'");
                    var binNode = await _wadSearchBoxService.PerformSearchAsync(binPath, new ObservableCollection<FileSystemNodeModel> { mainWadNode }, loader);
                    if (binNode != null) return (binNode, baseName);
                }
            }

            // Strategy 3: Fallback Audio
            if (wadRoot != null)
            {
                string binName = baseName + ".bin";
                _logService.Log($"[AUDIO] Specific BIN not found, attempting global search for '{binName}' in WAD '{wadRoot.Name}'");
                var binNode = await _wadSearchBoxService.PerformSearchAsync(binName, new ObservableCollection<FileSystemNodeModel> { wadRoot }, loader);
                if (binNode != null) return (binNode, baseName);
            }

            return (null, baseName);
        }

        private async Task<(FileSystemNodeModel WpkNode, FileSystemNodeModel AudioBnkNode, FileSystemNodeModel EventsBnkNode)> FindSiblingFilesAsync(FileSystemNodeModel clickedNode, ObservableCollection<FileSystemNodeModel> rootNodes, string currentRootPath, byte[] binData)
        {
            if (binData == null) return (null, null, null);

            try
            {
                using var binStream = new MemoryStream(binData);
                var binTree = new BinTree(binStream);

                var skinAudioProp = binTree.Objects.Values
                    .SelectMany(o => o.Properties)
                    .FirstOrDefault(p => _hashResolverService.ResolveBinHashGeneral(p.Key) == "skinAudioProperties");

                if (skinAudioProp.Value is BinTreeStruct skinAudioStruct)
                {
                    var bankUnitsProperty = skinAudioStruct.Properties
                        .FirstOrDefault(p => _hashResolverService.ResolveBinHashGeneral(p.Key) == "bankUnits");

                    if (bankUnitsProperty.Value is BinTreeContainer bankUnitsContainer)
                    {
                        var matchingBankUnits = new List<BinTreeStruct>();
                        foreach (BinTreeStruct bankUnitStruct in bankUnitsContainer.Elements)
                        {
                            var bankPathProperty = bankUnitStruct.Properties.FirstOrDefault(p => _hashResolverService.ResolveBinHashGeneral(p.Key) == "bankPath");
                            if (bankPathProperty.Value is BinTreeContainer bankPathContainer)
                            {
                                var paths = bankPathContainer.Elements.Select(p =>
                                {
                                    if (p is BinTreeWadChunkLink chunkLink)
                                        return _hashResolverService.ResolveHash(chunkLink.Value);
                                    if (p is BinTreeString stringValue)
                                        return stringValue.Value;
                                    return null;
                                }).Where(p => p != null).ToList();

                                if (paths.Any(p => string.Equals(p, clickedNode.FullPath, StringComparison.OrdinalIgnoreCase)))
                                {
                                    matchingBankUnits.Add(bankUnitStruct);
                                }
                            }
                        }

                        if (matchingBankUnits.Any())
                        {
                            BinTreeStruct bestMatch = matchingBankUnits.FirstOrDefault(bu => bu.Properties.Any(p => _hashResolverService.ResolveBinHashGeneral(p.Key) == "events")) ?? matchingBankUnits.First();

                            var finalBankPathProperty = bestMatch.Properties.FirstOrDefault(p => _hashResolverService.ResolveBinHashGeneral(p.Key) == "bankPath");
                            if (finalBankPathProperty.Value is BinTreeContainer finalBankPathContainer)
                            {
                                var finalPaths = finalBankPathContainer.Elements.Select(p =>
                                {
                                    if (p is BinTreeWadChunkLink chunkLink)
                                        return _hashResolverService.ResolveHash(chunkLink.Value);
                                    if (p is BinTreeString stringValue)
                                        return stringValue.Value;
                                    return null;
                                }).Where(p => p != null).ToList();
                                
                                FileSystemNodeModel wpkNode = null, audioBnkNode = null, eventsBnkNode = null;
                                Func<FileSystemNodeModel, Task> loader = async (node) => await LoadAllChildrenForSearch(node, currentRootPath);

                                foreach (var path in finalPaths)
                                {
                                    var node = await _wadSearchBoxService.PerformSearchAsync(path, rootNodes, loader);
                                    if (node != null)
                                    {
                                        if (node.Name.EndsWith("_vo_audio.wpk")) wpkNode = node;
                                        else if (node.Name.EndsWith("_vo_audio.bnk") || node.Name.EndsWith("_sfx_audio.bnk")) audioBnkNode = node;
                                        else if (node.Name.EndsWith("_vo_events.bnk") || node.Name.EndsWith("_sfx_events.bnk")) eventsBnkNode = node;
                                    }
                                }
                                return (wpkNode, audioBnkNode, eventsBnkNode);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to parse .bin file to find audio siblings.");
            }

            return (null, null, null);
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

    public class LinkedAudioBank
    {
        public FileSystemNodeModel WpkNode { get; set; }
        public FileSystemNodeModel AudioBnkNode { get; set; }
        public FileSystemNodeModel EventsBnkNode { get; set; }
        public byte[] BinData { get; set; }
        public string BaseName { get; set; }
    }
}
