using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Explorer.Tree;
using AssetsManager.Views.Models.Audio;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Services.Audio
{
    public class AudioBankLinkerService
    {
        private readonly WadContentProvider _wadContentProvider;
        private readonly WadSearchBoxService _wadSearchBoxService;
        private readonly LogService _logService;
        private readonly TreeUIManager _treeUIManager;
        private readonly HashResolverService _hashResolverService;
        private readonly WadNodeLoaderService _wadNodeLoaderService;

        public AudioBankLinkerService(
            WadContentProvider wadContentProvider,
            WadSearchBoxService wadSearchBoxService,
            LogService logService,
            TreeUIManager treeUIManager,
            HashResolverService hashResolverService,
            WadNodeLoaderService wadNodeLoaderService)
        {
            _wadContentProvider = wadContentProvider;
            _wadSearchBoxService = wadSearchBoxService;
            _logService = logService;
            _treeUIManager = treeUIManager;
            _hashResolverService = hashResolverService;
            _wadNodeLoaderService = wadNodeLoaderService;
        }

        public async Task<LinkedAudioBank> LinkAudioBankForDiffAsync(FileSystemNodeModel clickedNode, string basePath, bool preferOld = false, string backupRootDir = null)
        {
            _logService.LogDebug($"[LinkAudioBankForDiffAsync] Linking audio bank for diff view. Node: '{clickedNode.Name}', PreferOld: {preferOld}");

            if (string.IsNullOrEmpty(basePath) && clickedNode.ChunkDiff == null && string.IsNullOrEmpty(backupRootDir))
            {
                _logService.LogWarning($"[LinkAudioBankForDiffAsync] Base path is null, ChunkDiff is null, and backupRootDir is null. Cannot proceed. Node: {clickedNode.Name}");
                return null;
            }

            byte[] binData = null;
            FileSystemNodeModel binNode = null;
            string baseName = GetBaseName(clickedNode.Name);
            BinType binType = BinType.Unknown;

            FileSystemNodeModel wpkNode = null;
            FileSystemNodeModel audioBnkNode = null;
            FileSystemNodeModel eventsBnkNode = null;

            // 1. BACKUP MODE: If backupRootDir is provided AND we have Dependencies, use them (New Architecture)
            if (backupRootDir != null && clickedNode.ChunkDiff?.Dependencies != null)
            {
                _logService.LogDebug($"[LinkAudioBankForDiffAsync] Backup mode detected via backupRootDir. Found {clickedNode.ChunkDiff.Dependencies.Count} dependencies in ChunkDiff.");

                // Find the BIN dependency
                var binDep = clickedNode.ChunkDiff.Dependencies.FirstOrDefault(d => d.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
                if (binDep != null)
                {
                    _logService.LogDebug($"[LinkAudioBankForDiffAsync] Found .bin dependency: '{binDep.Path}' from WAD '{binDep.SourceWad}'");
                    ulong binHash = preferOld ? binDep.OldPathHash : binDep.NewPathHash;
                    string binChunkDir = preferOld ? "old" : "new";
                    string binBackupChunkPath = Path.Combine(backupRootDir, "wad_chunks", binChunkDir, binDep.SourceWad, $"{binHash:X16}.chunk");
                    _logService.LogDebug($"[LinkAudioBankForDiffAsync] Constructed .bin chunk path: '{binBackupChunkPath}'");

                    binNode = new FileSystemNodeModel(Path.GetFileName(binDep.Path), false, binDep.Path, binDep.SourceWad)
                    {
                        ChunkDiff = new SerializableChunkDiff 
                        { 
                            OldPath = binDep.Path, 
                            NewPath = binDep.Path, 
                            SourceWadFile = binDep.SourceWad,
                            OldPathHash = binDep.OldPathHash, 
                            NewPathHash = binDep.NewPathHash, 
                            OldCompressionType = binDep.CompressionType, 
                            NewCompressionType = binDep.CompressionType 
                        },
                        BackupChunkPath = binBackupChunkPath,
                        Type = NodeType.SoundBank
                    };
                    binData = await _wadContentProvider.GetVirtualFileBytesAsync(binNode);
                    _logService.LogDebug($"[LinkAudioBankForDiffAsync] Loaded .bin data. Size: {binData?.Length ?? 0} bytes.");

                    if (clickedNode.FullPath.Contains("/characters/")) binType = BinType.Champion;
                    else if (clickedNode.FullPath.Contains("/maps/")) binType = BinType.Map;
                    _logService.LogDebug($"[LinkAudioBankForDiffAsync] Determined BinType: {binType}");
                }

                // Find the Sibling audio dependency (_audio.bnk or _audio.wpk)
                var audioDep = clickedNode.ChunkDiff.Dependencies.FirstOrDefault(d => d.Path.Contains("_audio.bnk") || d.Path.Contains("_audio.wpk"));
                if (audioDep != null)
                {
                    _logService.LogDebug($"[LinkAudioBankForDiffAsync] Found sibling audio dependency: '{audioDep.Path}' from WAD '{audioDep.SourceWad}'");
                    ulong audioHash = preferOld ? audioDep.OldPathHash : audioDep.NewPathHash;
                    string audioChunkDir = preferOld ? "old" : "new";
                    string audioBackupChunkPath = Path.Combine(backupRootDir, "wad_chunks", audioChunkDir, audioDep.SourceWad, $"{audioHash:X16}.chunk");
                    _logService.LogDebug($"[LinkAudioBankForDiffAsync] Constructed sibling chunk path: '{audioBackupChunkPath}'");

                    var siblingNode = new FileSystemNodeModel(Path.GetFileName(audioDep.Path), false, audioDep.Path, audioDep.SourceWad)
                    {
                        ChunkDiff = new SerializableChunkDiff 
                        { 
                            OldPath = audioDep.Path,
                            NewPath = audioDep.Path,
                            SourceWadFile = audioDep.SourceWad,
                            OldPathHash = audioDep.OldPathHash, 
                            NewPathHash = audioDep.NewPathHash, 
                            OldCompressionType = audioDep.CompressionType, 
                            NewCompressionType = audioDep.CompressionType 
                        },
                        BackupChunkPath = audioBackupChunkPath
                    };

                    if (audioDep.Path.EndsWith(".wpk")) wpkNode = siblingNode;
                    else if (audioDep.Path.EndsWith(".bnk")) audioBnkNode = siblingNode;
                }

                eventsBnkNode = clickedNode;
            }
            // 2. MIXED MODE: If we have backupRootDir but NO Dependencies (Old Architecture or incomplete JSON), use the JSON searching sub-methods
            else
            {
                _logService.LogDebug($"[LinkAudioBankForDiffAsync] Resolving via sub-methods. BasePath: '{basePath}', BackupRootDir: '{backupRootDir}'");
                var (binNodeFromWads, baseNameFromWads, binTypeFromWads) = await FindAssociatedBinFileFromWadsAsync(clickedNode, basePath, preferOld, backupRootDir);
                binNode = binNodeFromWads;
                baseName = baseNameFromWads;
                binType = binTypeFromWads;

                if (binNode != null)
                {
                    binData = await _wadContentProvider.GetVirtualFileBytesAsync(binNode);
                }

                var siblingsResult = await FindSiblingFilesFromWadsAsync(clickedNode, basePath, preferOld, backupRootDir);
                wpkNode = siblingsResult.WpkNode;
                audioBnkNode = siblingsResult.AudioBnkNode;
                eventsBnkNode = siblingsResult.EventsBnkNode;
            }

            return new LinkedAudioBank
            {
                WpkNode = wpkNode,
                AudioBnkNode = audioBnkNode,
                EventsBnkNode = eventsBnkNode,
                BinData = binData,
                BaseName = baseName,
                BinType = binType
            };
        }

        public async Task<LinkedAudioBank> LinkAudioBankAsync(FileSystemNodeModel clickedNode, ObservableRangeCollection<FileSystemNodeModel> rootNodes, string currentRootPath, string newLolPath = null, string oldLolPath = null)
        {
            // Explorer Backup Mode or Diff View Mode
            if (clickedNode.ChunkDiff != null)
            {
                var (binNode, baseName, binType) = await FindAssociatedBinFileAsync(clickedNode, rootNodes, null);
                byte[] binData = null;
                if (binNode != null)
                {
                    binData = await _wadContentProvider.GetVirtualFileBytesAsync(binNode);
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
            else
            {
                // Normal Mode
                var (binNode, baseName, binType) = await FindAssociatedBinFileAsync(clickedNode, rootNodes, currentRootPath);
                byte[] binData = null;
                if (binNode != null)
                {
                    binData = await _wadContentProvider.GetVirtualFileBytesAsync(binNode);
                }

                var siblingsResult = await FindSiblingFilesFromWadsInternalAsync(clickedNode, clickedNode.SourceWadPath, baseName);

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

        private async Task<(FileSystemNodeModel WpkNode, FileSystemNodeModel AudioBnkNode, FileSystemNodeModel EventsBnkNode)> FindSiblingFilesFromWadsAsync(FileSystemNodeModel clickedNode, string basePath, bool preferOld = false, string backupRootDir = null)
        {
            string baseName = GetBaseName(clickedNode.Name);

            // BACKUP MODE: If backupRootDir is provided or basePath is null, use the JSON index
            if (backupRootDir != null || basePath == null)
            {
                if (clickedNode.ChunkDiff != null)
                {
                    // If backupRootDir is null, infer it from the node (legacy)
                    if (backupRootDir == null && !string.IsNullOrEmpty(clickedNode.BackupChunkPath))
                    {
                        string chunkPath = clickedNode.BackupChunkPath;
                        int wadChunksIndex = chunkPath.IndexOf("wad_chunks", StringComparison.OrdinalIgnoreCase);
                        if (wadChunksIndex != -1) backupRootDir = chunkPath.Substring(0, wadChunksIndex);
                        else backupRootDir = Path.GetDirectoryName(Path.GetDirectoryName(chunkPath));
                    }

                    if (backupRootDir != null)
                    {
                        string backupJsonPath = Path.Combine(backupRootDir, "wadcomparison.json");
                        if (File.Exists(backupJsonPath))
                        {
                            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
                            string jsonContent = await File.ReadAllTextAsync(backupJsonPath);
                            var comparisonData = JsonSerializer.Deserialize<WadComparisonData>(jsonContent, options);

                            string directoryPath = Path.GetDirectoryName(clickedNode.FullPath).Replace('\\', '/');
                            var siblings = comparisonData?.Diffs.Where(d =>
                                (d.NewPath != null && Path.GetDirectoryName(d.NewPath).Replace('\\', '/') == directoryPath) ||
                                (d.OldPath != null && Path.GetDirectoryName(d.OldPath).Replace('\\', '/') == directoryPath))
                                .ToList();

                            if (siblings != null)
                            {
                                var wpkDiff = siblings.FirstOrDefault(d => (d.NewPath ?? d.OldPath).EndsWith(baseName + "_audio.wpk", StringComparison.OrdinalIgnoreCase));
                                var audioBnkDiff = siblings.FirstOrDefault(d => (d.NewPath ?? d.OldPath).EndsWith(baseName + "_audio.bnk", StringComparison.OrdinalIgnoreCase));
                                var eventsBnkDiff = siblings.FirstOrDefault(d => (d.NewPath ?? d.OldPath).EndsWith(baseName + "_events.bnk", StringComparison.OrdinalIgnoreCase));

                                Func<SerializableChunkDiff, FileSystemNodeModel> createNode = (diff) =>
                                {
                                    if (diff == null) return null;
                                    bool useOld = preferOld || diff.Type == ChunkDiffType.Removed;
                                    ulong hashForPath = useOld ? diff.OldPathHash : diff.NewPathHash;
                                    string chunkDir = useOld ? "old" : "new";
                                    string fullPath = useOld ? diff.OldPath : diff.NewPath;

                                    return new FileSystemNodeModel
                                    {
                                        ChunkDiff = diff,
                                        FullPath = fullPath,
                                        Name = Path.GetFileName(fullPath),
                                        BackupChunkPath = Path.Combine(backupRootDir, "wad_chunks", chunkDir, diff.SourceWadFile, $"{hashForPath:X16}.chunk"),
                                        Type = NodeType.SoundBank
                                    };
                                };

                                return (createNode(wpkDiff), createNode(audioBnkDiff), createNode(eventsBnkDiff));
                            }
                        }
                    }
                }
                return (null, null, null);
            }
            // LIVE MODE
            else
            {
                string wadPath = Path.Combine(basePath, clickedNode.ChunkDiff.SourceWadFile);
                return await FindSiblingFilesFromWadsInternalAsync(clickedNode, wadPath, baseName);
            }
        }

        private async Task<(FileSystemNodeModel WpkNode, FileSystemNodeModel AudioBnkNode, FileSystemNodeModel EventsBnkNode)> FindSiblingFilesFromWadsInternalAsync(FileSystemNodeModel clickedNode, string wadPath, string baseName)
        {
            if (!File.Exists(wadPath))
            {
                _logService.LogWarning($"Source WAD file not found: {wadPath}");
                return (null, null, null);
            }

            var wadContent = await _wadNodeLoaderService.LoadWadContentAsync(wadPath);

            FileSystemNodeModel wpkNode = FindNodeByName(wadContent, baseName + "_audio.wpk");
            FileSystemNodeModel audioBnkNode = FindNodeByName(wadContent, baseName + "_audio.bnk");
            FileSystemNodeModel eventsBnkNode = FindNodeByName(wadContent, baseName + "_events.bnk");

            return (wpkNode, audioBnkNode, eventsBnkNode);
        }

        private record BinFileStrategy(string BinPath, string TargetWadName, BinType Type);

        private BinFileStrategy GetBinFileSearchStrategy(FileSystemNodeModel clickedNode)
        {
            _logService.LogDebug($"[GetBinFileSearchStrategy] Searching for BIN strategy for node: '{clickedNode.FullPath}' in WAD: '{clickedNode.SourceWadPath}'");
            string sourceWadName = Path.GetFileName(clickedNode.SourceWadPath);

            // Strategy 0: Infer from companion path structure
            if (clickedNode.FullPath.Contains("/companions/pets/"))
            {
                var pathParts = clickedNode.FullPath.Split('/');
                int petsIndex = Array.IndexOf(pathParts, "pets");

                if (petsIndex != -1 && pathParts.Length > petsIndex + 2)
                {
                    string petName = pathParts[petsIndex + 1];
                    string themeName = pathParts[petsIndex + 2];

                    if (!string.IsNullOrEmpty(petName) && !string.IsNullOrEmpty(themeName))
                    {
                        string binPath = $"data/characters/{petName}/themes/{themeName}/root.bin";
                        string targetWadName = "companions.wad.client";
                        return new BinFileStrategy(binPath, targetWadName, BinType.Companion);
                    }
                }
            }

            // Strategy 1: Infer from full path structure
            if (clickedNode.FullPath.Contains("/characters/") && clickedNode.FullPath.Contains("/skins/"))
            {
                var pathParts = clickedNode.FullPath.Split('/');
                int charactersIndex = Array.IndexOf(pathParts, "characters");
                int skinsIndex = Array.IndexOf(pathParts, "skins");

                if (skinsIndex > charactersIndex + 1)
                {
                    string championName = pathParts[charactersIndex + 1];
                    string skinFolder = pathParts[skinsIndex + 1];

                    if (!string.IsNullOrEmpty(championName) && !string.IsNullOrEmpty(skinFolder))
                    {
                        string skinName = (skinFolder == "base") ? "skin0" : $"skin{int.Parse(skinFolder.Replace("skin", ""))}";
                        string binPath = $"data/characters/{championName}/skins/{skinName}.bin";
                        string targetWadName = $"{championName.ToLower()}.wad.client";
                        return new BinFileStrategy(binPath, targetWadName, BinType.Champion);
                    }
                }
            }

            // Strategy 2: Infer from WAD file name (for champions)
            var wadNameParts = sourceWadName.Split('.');
            if (wadNameParts.Length > 0)
            {
                string championName = wadNameParts[0];
                if (!championName.StartsWith("map", StringComparison.OrdinalIgnoreCase) && 
                    !championName.Equals("common", StringComparison.OrdinalIgnoreCase) &&
                    !championName.Equals("companions", StringComparison.OrdinalIgnoreCase))
                {
                    string skinName = "skin0";
                    string binPath = $"data/characters/{championName.ToLower()}/skins/{skinName}.bin";
                    string targetWadName = $"{championName.ToLower()}.wad.client";
                    return new BinFileStrategy(binPath, targetWadName, BinType.Champion);
                }
            }

            // Strategy 3 (New): Infer for locale VO files (e.g., misc_emotes_vo_audio.wpk in Common.es_ES.wad.client)
            if (clickedNode.Name.Contains("_vo_", StringComparison.OrdinalIgnoreCase) &&
                sourceWadName.StartsWith("Common.", StringComparison.OrdinalIgnoreCase) &&
                sourceWadName.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase) &&
                !sourceWadName.Equals("Common.wad.client", StringComparison.OrdinalIgnoreCase)) // Ensure it's a locale specific common WAD
            {
                string binPath = "data/maps/shipping/common/common.bin";
                string targetWadName = "Common.wad.client"; // Always in the general Common.wad.client
                return new BinFileStrategy(binPath, targetWadName, BinType.Map);
            }

            // Strategy 4 (Unified Map/Common): Infer from WAD file name for maps.
            if (wadNameParts.Length > 0 && (sourceWadName.StartsWith("Map", StringComparison.OrdinalIgnoreCase) || sourceWadName.StartsWith("Common", StringComparison.OrdinalIgnoreCase)))
            {
                string mapName = wadNameParts[0]; // For "Common.es_ES", this is "Common".
                if (!string.IsNullOrEmpty(mapName))
                {
                    string binPath = $"data/maps/shipping/{mapName.ToLower()}/{mapName.ToLower()}.bin";
                    string targetWadName = $"{mapName.ToLower()}.wad.client";
                    return new BinFileStrategy(mapName.Equals("Common", StringComparison.OrdinalIgnoreCase) ? "data/maps/shipping/common/common.bin" : binPath, targetWadName, BinType.Map);
                }
            }

            return null;
        }

        private async Task<(FileSystemNodeModel BinNode, string BaseName, BinType Type)> FindAssociatedBinFileFromWadsAsync(FileSystemNodeModel clickedNode, string basePath, bool preferOld = false, string backupRootDir = null)
        {
            string baseName = GetBaseName(clickedNode.Name);
            var strategy = GetBinFileSearchStrategy(clickedNode);

            if (strategy == null)
            {
                return (null, baseName, BinType.Unknown);
            }

            // BACKUP MODE: If backupRootDir is provided or basePath is null, use the JSON index
            if (backupRootDir != null || basePath == null)
            {
                if (clickedNode.ChunkDiff != null)
                {
                    // If backupRootDir is null, infer it (legacy)
                    if (backupRootDir == null && !string.IsNullOrEmpty(clickedNode.BackupChunkPath))
                    {
                        string chunkPath = clickedNode.BackupChunkPath;
                        int wadChunksIndex = chunkPath.IndexOf("wad_chunks", StringComparison.OrdinalIgnoreCase);
                        if (wadChunksIndex != -1) backupRootDir = chunkPath.Substring(0, wadChunksIndex);
                        else backupRootDir = Path.GetDirectoryName(Path.GetDirectoryName(chunkPath));
                    }

                    if (backupRootDir != null)
                    {
                        ulong binHash = XxHash64Ext.Hash(strategy.BinPath.ToLower());
                        string backupJsonPath = Path.Combine(backupRootDir, "wadcomparison.json");
                        if (File.Exists(backupJsonPath))
                        {
                            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
                            string jsonContent = await File.ReadAllTextAsync(backupJsonPath);
                            var comparisonData = JsonSerializer.Deserialize<WadComparisonData>(jsonContent, options);

                            var binDiff = comparisonData?.Diffs.FirstOrDefault(d => d.NewPathHash == binHash || d.OldPathHash == binHash);
                            if (binDiff != null)
                            {
                                bool useOld = preferOld || binDiff.Type == ChunkDiffType.Removed;
                                ulong hashForPath = useOld ? binDiff.OldPathHash : binDiff.NewPathHash;
                                string chunkDir = useOld ? "old" : "new";

                                var binNode = new FileSystemNodeModel(Path.GetFileName(strategy.BinPath), false, strategy.BinPath, strategy.TargetWadName)
                                {
                                    ChunkDiff = binDiff,
                                    SourceChunkPathHash = hashForPath,
                                    BackupChunkPath = Path.Combine(backupRootDir, "wad_chunks", chunkDir, binDiff.SourceWadFile, $"{hashForPath:X16}.chunk")
                                };
                                return (binNode, baseName, strategy.Type);
                            }
                        }
                    }
                }
                return (null, baseName, strategy.Type);
            }
            // LIVE MODE
            else
            {
                string targetWadFullPath = null;
                string sourceWadRelativePath = clickedNode.ChunkDiff.SourceWadFile;
                string sourceWadFileName = Path.GetFileName(sourceWadRelativePath);

                // Case 1: The BIN is in the same WAD as the audio file
                if (string.Equals(sourceWadFileName, strategy.TargetWadName, StringComparison.OrdinalIgnoreCase))
                {
                    targetWadFullPath = Path.Combine(basePath, sourceWadRelativePath);
                }
                // Case 2: The BIN is in a different WAD. Try to find it in the same directory first.
                else
                {
                    string sourceWadDirectory = Path.GetDirectoryName(sourceWadRelativePath);
                    string potentialPath = Path.Combine(basePath, sourceWadDirectory, strategy.TargetWadName);
                    if (File.Exists(potentialPath)) targetWadFullPath = potentialPath;
                    else
                    {
                        string wadDirectory = (strategy.Type == BinType.Champion || strategy.Type == BinType.Map) ? basePath : Path.GetDirectoryName(Path.Combine(basePath, sourceWadRelativePath));
                        targetWadFullPath = Path.Combine(wadDirectory, strategy.TargetWadName);
                    }
                }

                if (File.Exists(targetWadFullPath))
                {
                    var wadContent = await _wadNodeLoaderService.LoadWadContentAsync(targetWadFullPath);
                    var binNode = wadContent.FirstOrDefault(n => n.FullPath.Equals(strategy.BinPath, StringComparison.OrdinalIgnoreCase));
                    if (binNode != null) return (binNode, baseName, strategy.Type);
                }

                return (null, baseName, strategy.Type);
            }
        }

        private (FileSystemNodeModel WpkNode, FileSystemNodeModel AudioBnkNode, FileSystemNodeModel EventsBnkNode) FindSiblingFilesByName(FileSystemNodeModel clickedNode, ObservableRangeCollection<FileSystemNodeModel> rootNodes)
        {
            // A simple way to detect backup mode is to check if the node has ChunkDiff data.
            bool isBackupMode = clickedNode.ChunkDiff != null;

            string baseName = GetBaseName(clickedNode.Name);
            string expectedWpkName = baseName + "_audio.wpk";
            string expectedAudioBnkName = baseName + "_audio.bnk";
            string expectedEventsBnkName = baseName + "_events.bnk";

            FileSystemNodeModel wpkNode = null;
            FileSystemNodeModel audioBnkNode = null;
            FileSystemNodeModel eventsBnkNode = null;

            if (!isBackupMode)
            {
                // Keep the original, efficient logic for normal (live) mode
                var parentPath = _treeUIManager.FindNodePath(rootNodes, clickedNode);
                if (parentPath == null || parentPath.Count < 2)
                {
                    return (null, null, null);
                }
                var parentNode = parentPath[parentPath.Count - 2];

                wpkNode = parentNode.Children.FirstOrDefault(c => c.Name.Equals(expectedWpkName, StringComparison.OrdinalIgnoreCase));
                audioBnkNode = parentNode.Children.FirstOrDefault(c => c.Name.Equals(expectedAudioBnkName, StringComparison.OrdinalIgnoreCase));
                eventsBnkNode = parentNode.Children.FirstOrDefault(c => c.Name.Equals(expectedEventsBnkName, StringComparison.OrdinalIgnoreCase));
            }
            else // Backup Mode - search across the whole tree
            {
                _logService.LogDebug("[FindSiblingFilesByName] Backup mode detected. Searching entire tree for siblings.");
                var namesToFind = new List<string> { expectedWpkName, expectedAudioBnkName, expectedEventsBnkName };
                var allMatches = new List<FileSystemNodeModel>();
                
                FindAllNodesByNameRecursive(rootNodes, namesToFind, allMatches);

                wpkNode = allMatches.FirstOrDefault(n => n.Name.Equals(expectedWpkName, StringComparison.OrdinalIgnoreCase));
                audioBnkNode = allMatches.FirstOrDefault(n => n.Name.Equals(expectedAudioBnkName, StringComparison.OrdinalIgnoreCase));
                eventsBnkNode = allMatches.FirstOrDefault(n => n.Name.Equals(expectedEventsBnkName, StringComparison.OrdinalIgnoreCase));

                _logService.LogDebug($"[FindSiblingFilesByName] Sibling search results - WPK: {wpkNode != null}, AudioBNK: {audioBnkNode != null}, EventsBNK: {eventsBnkNode != null}");
            }

            return (wpkNode, audioBnkNode, eventsBnkNode);
        }

        private async Task<(FileSystemNodeModel BinNode, string BaseName, BinType Type)> FindAssociatedBinFileAsync(FileSystemNodeModel clickedNode, ObservableRangeCollection<FileSystemNodeModel> rootNodes, string currentRootPath)
        {
            string baseName = GetBaseName(clickedNode.Name);
            var strategy = GetBinFileSearchStrategy(clickedNode);

            if (strategy == null)
            {
                _logService.LogDebug($"[FindAssociatedBinFileAsync] No BIN strategy found for node: {clickedNode.FullPath}");
                return (null, baseName, BinType.Unknown);
            }

            _logService.LogDebug($"[FindAssociatedBinFileAsync] Using strategy for {clickedNode.FullPath}. BinPath: '{strategy.BinPath}', TargetWAD: '{strategy.TargetWadName}'");

            // Backup Mode
            if (currentRootPath == null)
            {
                _logService.LogDebug($"[FindAssociatedBinFileAsync] Backup Mode: Searching for BIN node with path: '{strategy.BinPath}'");
                
                // First, try to find it without any prefix (for the non-sorted/flat view)
                var binNode = FindNodeByPath(rootNodes, strategy.BinPath);

                // If not found, try with status prefixes (for the sorted/grouped by WAD view)
                if (binNode == null)
                {
                    _logService.LogDebug($"[FindAssociatedBinFileAsync] Node not found with direct path. Trying with status prefixes.");
                    string[] prefixes = { "Modified/", "New/", "Renamed/", "Dependency/" };
                    foreach (var prefix in prefixes)
                    {
                        string prefixedPath = prefix + strategy.BinPath;
                        _logService.LogDebug($"[FindAssociatedBinFileAsync] Trying prefixed path: '{prefixedPath}'");
                        binNode = FindNodeByPath(rootNodes, prefixedPath);
                        if (binNode != null)
                        {
                            break; // Found it
                        }
                    }
                }

                if (binNode != null)
                {
                    _logService.LogDebug($"[FindAssociatedBinFileAsync] Backup Mode: Found BIN node: '{binNode.FullPath}'");
                }
                else
                {
                    _logService.LogDebug($"[FindAssociatedBinFileAsync] Backup Mode: BIN node with path '{strategy.BinPath}' not found after all attempts.");
                }
                return (binNode, baseName, strategy.Type);
            }
            // Normal Mode
            else
            {
                Func<FileSystemNodeModel, Task> loader = async (node) => await _wadNodeLoaderService.EnsureAllChildrenLoadedAsync(node, currentRootPath);

                var targetWadNode = FindNodeByName(rootNodes, strategy.TargetWadName);
                if (targetWadNode != null)
                {
                    _logService.LogDebug($"[FindAssociatedBinFileAsync] Searching for BIN '{strategy.BinPath}' inside '{targetWadNode.Name}'...");
                    var binNode = await _wadSearchBoxService.PerformSearchAsync(strategy.BinPath, new ObservableRangeCollection<FileSystemNodeModel> { targetWadNode }, loader);
                    if (binNode != null)
                    {
                        _logService.LogDebug($"[FindAssociatedBinFileAsync] Found BIN node in primary target WAD.");
                        return (binNode, baseName, strategy.Type);
                    }
                    _logService.LogDebug($"[FindAssociatedBinFileAsync] BIN not found in primary target WAD '{targetWadNode.Name}'.");
                }
                else
                {
                    _logService.LogWarning($"[FindAssociatedBinFileAsync] Primary target WAD '{strategy.TargetWadName}' not found in tree.");
                }

                // Fallback: If the .bin file was not found in the target WAD, check Common.wad.client
                var commonWadNode = FindNodeByName(rootNodes, "Common.wad.client");
                if (commonWadNode != null && commonWadNode != targetWadNode)
                {
                    _logService.LogDebug($"[FindAssociatedBinFileAsync] Fallback: Searching for BIN '{strategy.BinPath}' inside '{commonWadNode.Name}'...");
                    var binNodeInCommon = await _wadSearchBoxService.PerformSearchAsync(strategy.BinPath, new ObservableRangeCollection<FileSystemNodeModel> { commonWadNode }, loader);
                    if (binNodeInCommon != null)
                    {
                        _logService.LogDebug($"[FindAssociatedBinFileAsync] Found BIN node in fallback WAD 'Common.wad.client'.");
                        return (binNodeInCommon, baseName, strategy.Type);
                    }
                    _logService.LogDebug($"[FindAssociatedBinFileAsync] BIN not found in fallback WAD 'Common.wad.client'.");
                }
            }

            return (null, baseName, strategy.Type);
        }

        private string GetBaseName(string name)
        {
            return name.Replace("_audio.wpk", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("_audio.bnk", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("_events.bnk", "", StringComparison.OrdinalIgnoreCase);
        }



        private void FindAllNodesByNameRecursive(IEnumerable<FileSystemNodeModel> nodes, List<string> namesToFind, List<FileSystemNodeModel> foundNodes)
        {
            foreach (var node in nodes)
            {
                if (namesToFind.Contains(node.Name))
                {
                    foundNodes.Add(node);
                }

                if (node.Children != null && node.Children.Any())
                {
                    FindAllNodesByNameRecursive(node.Children, namesToFind, foundNodes);
                }
            }
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

        private FileSystemNodeModel FindNodeByPath(IEnumerable<FileSystemNodeModel> nodes, string fullPath)
        {
            foreach (var node in nodes)
            {
                if (node.FullPath != null && node.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }

                if (node.Children != null && node.Children.Any())
                {
                    var found = FindNodeByPath(node.Children, fullPath);
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
