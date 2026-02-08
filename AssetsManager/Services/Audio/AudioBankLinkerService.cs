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
using AssetsManager.Utils;

namespace AssetsManager.Services.Audio
{
    public class AudioBankLinkerService
    {
        private readonly WadExtractionService _wadExtractionService;
        private readonly WadSearchBoxService _wadSearchBoxService;
        private readonly LogService _logService;
        private readonly TreeUIManager _treeUIManager;
        private readonly HashResolverService _hashResolverService;
        private readonly WadNodeLoaderService _wadNodeLoaderService;

        public AudioBankLinkerService(
            WadExtractionService wadExtractionService,
            WadSearchBoxService wadSearchBoxService,
            LogService logService,
            TreeUIManager treeUIManager,
            HashResolverService hashResolverService,
            WadNodeLoaderService wadNodeLoaderService)
        {
            _wadExtractionService = wadExtractionService;
            _wadSearchBoxService = wadSearchBoxService;
            _logService = logService;
            _treeUIManager = treeUIManager;
            _hashResolverService = hashResolverService;
            _wadNodeLoaderService = wadNodeLoaderService;
        }

        public async Task<LinkedAudioBank> LinkAudioBankForDiffAsync(FileSystemNodeModel clickedNode, string basePath, bool preferOld = false, string backupRootDir = null)
        {
            _logService.LogDebug($"[LinkAudioBankForDiffAsync] Linking audio bank for diff view. Node: '{clickedNode.Name}', PreferOld: {preferOld}");

            if (string.IsNullOrEmpty(basePath) && clickedNode.ChunkDiff == null)
            {
                _logService.LogWarning($"[LinkAudioBankForDiffAsync] Base path is null and ChunkDiff is null. Cannot proceed. Node: {clickedNode.Name}");
                return null;
            }

            byte[] binData = null;
            FileSystemNodeModel binNode = null;
            string baseName = Path.GetFileNameWithoutExtension(clickedNode.Name).Replace("_events", "").Replace("_audio", "");
            BinType binType = BinType.Unknown;

            FileSystemNodeModel wpkNode = null;
            FileSystemNodeModel audioBnkNode = null;
            FileSystemNodeModel eventsBnkNode = null;

            // If in backup mode (basePath is null), we get dependencies from the clickedNode's ChunkDiff.Dependencies
            if (basePath == null && clickedNode.ChunkDiff?.Dependencies != null)
            {
                _logService.LogDebug($"[LinkAudioBankForDiffAsync] Backup mode detected. Found {clickedNode.ChunkDiff.Dependencies.Count} dependencies in ChunkDiff.");

                if (string.IsNullOrEmpty(backupRootDir))
                {
                    _logService.LogError("[LinkAudioBankForDiffAsync] Critical error: backupRootDir is null in backup mode.");
                    return null;
                }
                _logService.LogDebug($"[LinkAudioBankForDiffAsync] Backup root path: '{backupRootDir}'");

                // Find the BIN dependency
                var binDep = clickedNode.ChunkDiff.Dependencies.FirstOrDefault(d => d.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
                if (binDep != null)
                {
                    _logService.LogDebug($"[LinkAudioBankForDiffAsync] Found .bin dependency: '{binDep.Path}' from WAD '{binDep.SourceWad}'");
                    ulong binHash = preferOld ? binDep.OldPathHash : binDep.NewPathHash;
                    string binChunkDir = preferOld ? "old" : "new";
                    string binBackupChunkPath = Path.Combine(backupRootDir, "wad_chunks", binChunkDir, $"{binHash:X16}.chunk");
                    _logService.LogDebug($"[LinkAudioBankForDiffAsync] Constructed .bin chunk path: '{binBackupChunkPath}'");

                    binNode = new FileSystemNodeModel(Path.GetFileName(binDep.Path), false, binDep.Path, binDep.SourceWad)
                    {
                        ChunkDiff = new SerializableChunkDiff { OldPathHash = binDep.OldPathHash, NewPathHash = binDep.NewPathHash, OldCompressionType = binDep.CompressionType, NewCompressionType = binDep.CompressionType },
                        BackupChunkPath = binBackupChunkPath,
                        Type = NodeType.SoundBank
                    };
                    binData = await _wadExtractionService.GetVirtualFileBytesAsync(binNode);
                    _logService.LogDebug($"[LinkAudioBankForDiffAsync] Loaded .bin data. Size: {binData?.Length ?? 0} bytes.");

                    if (clickedNode.FullPath.Contains("/characters/")) binType = BinType.Champion;
                    else if (clickedNode.FullPath.Contains("/maps/")) binType = BinType.Map;
                    _logService.LogDebug($"[LinkAudioBankForDiffAsync] Determined BinType: {binType}");
                }
                else
                {
                    _logService.LogWarning($"[LinkAudioBankForDiffAsync] No .bin dependency found in ChunkDiff.Dependencies for {clickedNode.Name}.");
                }

                // Find the Sibling audio dependency (_audio.bnk or _audio.wpk)
                var audioDep = clickedNode.ChunkDiff.Dependencies.FirstOrDefault(d => d.Path.Contains("_audio.bnk") || d.Path.Contains("_audio.wpk"));
                if (audioDep != null)
                {
                    _logService.LogDebug($"[LinkAudioBankForDiffAsync] Found sibling audio dependency: '{audioDep.Path}' from WAD '{audioDep.SourceWad}'");
                    ulong audioHash = preferOld ? audioDep.OldPathHash : audioDep.NewPathHash;
                    string audioChunkDir = preferOld ? "old" : "new";
                    string audioBackupChunkPath = Path.Combine(backupRootDir, "wad_chunks", audioChunkDir, $"{audioHash:X16}.chunk");
                    _logService.LogDebug($"[LinkAudioBankForDiffAsync] Constructed sibling chunk path: '{audioBackupChunkPath}'");

                    var siblingNode = new FileSystemNodeModel(Path.GetFileName(audioDep.Path), false, audioDep.Path, audioDep.SourceWad)
                    {
                        ChunkDiff = new SerializableChunkDiff { OldPathHash = audioDep.OldPathHash, NewPathHash = audioDep.NewPathHash, OldCompressionType = audioDep.CompressionType, NewCompressionType = audioDep.CompressionType },
                        BackupChunkPath = audioBackupChunkPath
                    };

                    if (audioDep.Path.EndsWith(".wpk"))
                    {
                        _logService.LogDebug($"[LinkAudioBankForDiffAsync] Sibling is a .wpk file. Assigning to WpkNode.");
                        wpkNode = siblingNode;
                        wpkNode.Type = NodeType.SoundBank;
                    }
                    else if (audioDep.Path.EndsWith(".bnk"))
                    {
                        _logService.LogDebug($"[LinkAudioBankForDiffAsync] Sibling is a .bnk file. Assigning to AudioBnkNode.");
                        audioBnkNode = siblingNode;
                        audioBnkNode.Type = NodeType.SoundBank;
                    }
                }
                else
                {
                    _logService.LogWarning($"[LinkAudioBankForDiffAsync] No sibling audio dependency found in ChunkDiff.Dependencies for {clickedNode.Name}.");
                }

                // The clickedNode itself is the _events.bnk
                eventsBnkNode = clickedNode;
                _logService.LogDebug($"[LinkAudioBankForDiffAsync] Assigned clicked node '{clickedNode.Name}' as the EventsBnkNode.");
            }
            // Live Mode or Backup Mode where dependencies are top-level diffs (old architecture)
            else
            {
                _logService.LogDebug($"[LinkAudioBankForDiffAsync] Live mode or legacy backup mode detected. BasePath: '{basePath}'");
                var (binNodeFromWads, baseNameFromWads, binTypeFromWads) = await FindAssociatedBinFileFromWadsAsync(clickedNode, basePath, preferOld);
                binNode = binNodeFromWads;
                baseName = baseNameFromWads;
                binType = binTypeFromWads;

                if (binNode != null)
                {
                    binData = await _wadExtractionService.GetVirtualFileBytesAsync(binNode);
                    _logService.LogDebug($"[LinkAudioBankForDiffAsync] Live Mode: Loaded .bin data. Size: {binData?.Length ?? 0} bytes.");
                }
                else
                {
                    _logService.LogWarning($"Could not find any associated .bin file for {clickedNode.Name} in diff mode. Event names will be unavailable.");
                }

                var siblingsResult = await FindSiblingFilesFromWadsAsync(clickedNode, basePath, preferOld);
                wpkNode = siblingsResult.WpkNode;
                audioBnkNode = siblingsResult.AudioBnkNode;
                eventsBnkNode = siblingsResult.EventsBnkNode;
            }

            var linkedBank = new LinkedAudioBank
            {
                WpkNode = wpkNode,
                AudioBnkNode = audioBnkNode,
                EventsBnkNode = eventsBnkNode,
                BinData = binData,
                BaseName = baseName,
                BinType = binType
            };
            _logService.LogDebug($"[LinkAudioBankForDiffAsync] Finished linking. Wpk: {wpkNode != null}, AudioBnk: {audioBnkNode != null}, EventsBnk: {eventsBnkNode != null}, BinData: {binData != null}.");
            return linkedBank;
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
                    binData = await _wadExtractionService.GetVirtualFileBytesAsync(binNode);
                }
                else
                {
                    _logService.LogWarning($"Could not find any associated .bin file for {clickedNode.Name} in backup mode. Event names will be unavailable.");
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
                    binData = await _wadExtractionService.GetVirtualFileBytesAsync(binNode);
                }
                else
                {
                    _logService.LogWarning($"Could not find any associated .bin file for {clickedNode.Name}. Event names will be unavailable.");
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

        private async Task<(FileSystemNodeModel WpkNode, FileSystemNodeModel AudioBnkNode, FileSystemNodeModel EventsBnkNode)> FindSiblingFilesFromWadsAsync(FileSystemNodeModel clickedNode, string basePath, bool preferOld = false)
        {
            string baseName = clickedNode.Name.Replace("_audio.wpk", "").Replace("_audio.bnk", "").Replace("_events.bnk", "");

            // Backup Mode
            if (basePath == null)
            {
                if (clickedNode.ChunkDiff != null && !string.IsNullOrEmpty(clickedNode.BackupChunkPath))
                {
                    string backupChunkDir = Path.GetDirectoryName(clickedNode.BackupChunkPath);
                    string backupWadChunksDir = Path.GetDirectoryName(backupChunkDir);
                    string backupRootDir = Path.GetDirectoryName(backupWadChunksDir);
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
                            var wpkDiff = siblings.FirstOrDefault(d => (d.NewPath ?? d.OldPath).EndsWith(baseName + "_audio.wpk"));
                            var audioBnkDiff = siblings.FirstOrDefault(d => (d.NewPath ?? d.OldPath).EndsWith(baseName + "_audio.bnk"));
                            var eventsBnkDiff = siblings.FirstOrDefault(d => (d.NewPath ?? d.OldPath).EndsWith(baseName + "_events.bnk"));

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
                                    BackupChunkPath = Path.Combine(backupRootDir, "wad_chunks", chunkDir, $"{hashForPath:X16}.chunk"),
                                    Type = NodeType.SoundBank
                                };
                            };

                            return (createNode(wpkDiff), createNode(audioBnkDiff), createNode(eventsBnkDiff));
                        }
                    }
                }
                return (null, null, null);
            }
            // Live Mode
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

            FileSystemNodeModel wpkNode = wadContent.FirstOrDefault(c => c.Name == baseName + "_audio.wpk");
            FileSystemNodeModel audioBnkNode = wadContent.FirstOrDefault(c => c.Name == baseName + "_audio.bnk");
            FileSystemNodeModel eventsBnkNode = wadContent.FirstOrDefault(c => c.Name == baseName + "_events.bnk");

            return (wpkNode, audioBnkNode, eventsBnkNode);
        }

        private record BinFileStrategy(string BinPath, string TargetWadName, BinType Type);

        private BinFileStrategy GetBinFileSearchStrategy(FileSystemNodeModel clickedNode)
        {
            _logService.LogDebug($"[GetBinFileSearchStrategy] Searching for BIN strategy for node: '{clickedNode.FullPath}' in WAD: '{clickedNode.SourceWadPath}'");
            string sourceWadName = Path.GetFileName(clickedNode.SourceWadPath);

            // Strategy 0: Infer from companion path structure
            _logService.LogDebug("[GetBinFileSearchStrategy] Attempting Strategy 0: Infer from companion path structure.");
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
                        var strategy = new BinFileStrategy(binPath, targetWadName, BinType.Companion);
                        _logService.LogDebug($"[GetBinFileSearchStrategy] Strategy 0 successful. Found: {strategy}");
                        return strategy;
                    }
                }
            }
            _logService.LogDebug("[GetBinFileSearchStrategy] Strategy 0 failed or was not applicable.");

            // Strategy 1: Infer from full path structure
            _logService.LogDebug("[GetBinFileSearchStrategy] Attempting Strategy 1: Infer from full path structure.");
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
                        var strategy = new BinFileStrategy(binPath, targetWadName, BinType.Champion);
                        _logService.LogDebug($"[GetBinFileSearchStrategy] Strategy 1 successful. Found: {strategy}");
                        return strategy;
                    }
                }
            }
            _logService.LogDebug("[GetBinFileSearchStrategy] Strategy 1 failed or was not applicable.");

            // Strategy 2: Infer from WAD file name (for champions)
            _logService.LogDebug("[GetBinFileSearchStrategy] Attempting Strategy 2: Infer from WAD file name (for champions).");
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
                    var strategy = new BinFileStrategy(binPath, targetWadName, BinType.Champion);
                    _logService.LogDebug($"[GetBinFileSearchStrategy] Strategy 2 successful. Found: {strategy}");
                    return strategy;
                }
            }
            _logService.LogDebug("[GetBinFileSearchStrategy] Strategy 2 failed or was not applicable.");

            // Strategy 3 (New): Infer for locale VO files (e.g., misc_emotes_vo_audio.wpk in Common.es_ES.wad.client)
            _logService.LogDebug("[GetBinFileSearchStrategy] Attempting Strategy 3 (New): Infer for locale VO files.");
            if (clickedNode.Name.Contains("_vo_", StringComparison.OrdinalIgnoreCase) &&
                sourceWadName.StartsWith("Common.", StringComparison.OrdinalIgnoreCase) &&
                sourceWadName.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase) &&
                !sourceWadName.Equals("Common.wad.client", StringComparison.OrdinalIgnoreCase)) // Ensure it's a locale specific common WAD
            {
                string binPath = "data/maps/shipping/common/common.bin";
                string targetWadName = "Common.wad.client"; // Always in the general Common.wad.client
                var strategy = new BinFileStrategy(binPath, targetWadName, BinType.Map);
                _logService.LogDebug($"[GetBinFileSearchStrategy] Strategy 3 (New) successful. Found: {strategy}");
                return strategy;
            }
            _logService.LogDebug("[GetBinFileSearchStrategy] Strategy 3 (New) failed or was not applicable.");

            // Strategy 4 (Unified Map/Common): Infer from WAD file name for maps.
            _logService.LogDebug("[GetBinFileSearchStrategy] Attempting Strategy 4: Infer from WAD file name for Maps/Common.");
            if (wadNameParts.Length > 0 && (sourceWadName.StartsWith("Map", StringComparison.OrdinalIgnoreCase) || sourceWadName.StartsWith("Common", StringComparison.OrdinalIgnoreCase)))
            {
                // This will handle MapXX.wad.client, Common.wad.client, and also non-VO files in Common.es_ES.wad.client,
                // as VO files are caught by the more specific Strategy 3.
                string mapName = wadNameParts[0]; // For "Common.es_ES", this is "Common".
                if (!string.IsNullOrEmpty(mapName))
                {
                    string binPath = $"data/maps/shipping/{mapName.ToLower()}/{mapName.ToLower()}.bin";
                    string targetWadName = $"{mapName.ToLower()}.wad.client";
                    var strategy = new BinFileStrategy(binPath, targetWadName, BinType.Map);
                    _logService.LogDebug($"[GetBinFileSearchStrategy] Strategy 4 (Unified) successful. Found: {strategy}");
                    return strategy;
                }
            }
            _logService.LogDebug("[GetBinFileSearchStrategy] Strategy 4 failed or was not applicable.");

            _logService.LogDebug("[GetBinFileSearchStrategy] No BIN file strategy found for '{clickedNode.FullPath}'.");
            return null;
        }

        private async Task<(FileSystemNodeModel BinNode, string BaseName, BinType Type)> FindAssociatedBinFileFromWadsAsync(FileSystemNodeModel clickedNode, string basePath, bool preferOld = false)
        {
            string baseName = clickedNode.Name.Replace("_audio.wpk", "").Replace("_audio.bnk", "").Replace("_events.bnk", "");
            var strategy = GetBinFileSearchStrategy(clickedNode);

            if (strategy == null)
            {
                return (null, baseName, BinType.Unknown);
            }

            // Backup Mode
            if (basePath == null)
            {
                if (clickedNode.ChunkDiff != null && !string.IsNullOrEmpty(clickedNode.BackupChunkPath))
                {
                    ulong binHash = XxHash64Ext.Hash(strategy.BinPath.ToLower());

                    string backupChunkDir = Path.GetDirectoryName(clickedNode.BackupChunkPath);
                    string backupWadChunksDir = Path.GetDirectoryName(backupChunkDir);
                    string backupRootDir = Path.GetDirectoryName(backupWadChunksDir);
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
                                BackupChunkPath = Path.Combine(backupRootDir, "wad_chunks", chunkDir, $"{hashForPath:X16}.chunk")
                            };
                            return (binNode, baseName, strategy.Type);
                        }
                    }
                }
                return (null, baseName, strategy.Type);
            }
            // Live Mode
            else
            {
                string targetWadFullPath = null;
                string sourceWadRelativePath = clickedNode.ChunkDiff.SourceWadFile;
                string sourceWadFileName = Path.GetFileName(sourceWadRelativePath);

                // Case 1: The BIN is in the same WAD as the audio file (Most common case: Champions, Maps)
                if (string.Equals(sourceWadFileName, strategy.TargetWadName, StringComparison.OrdinalIgnoreCase))
                {
                    targetWadFullPath = Path.Combine(basePath, sourceWadRelativePath);
                }
                // Case 2: The BIN is in a different WAD. Try to find it in the same directory as the source WAD first.
                else
                {
                    string sourceWadDirectory = Path.GetDirectoryName(sourceWadRelativePath);
                    string potentialPath = Path.Combine(basePath, sourceWadDirectory, strategy.TargetWadName);

                    if (File.Exists(potentialPath))
                    {
                        targetWadFullPath = potentialPath;
                    }
                    else
                    {
                        // Fallback: Use the original logic as a last resort
                        string wadDirectory;
                        if (strategy.Type == BinType.Champion || strategy.Type == BinType.Map)
                        {
                            wadDirectory = basePath;
                        }
                        else
                        {
                            wadDirectory = Path.GetDirectoryName(Path.Combine(basePath, sourceWadRelativePath));
                        }
                        targetWadFullPath = Path.Combine(wadDirectory, strategy.TargetWadName);
                    }
                }

                _logService.LogDebug($"[FindAssociatedBinFileFromWadsAsync] Live Mode: Resolved Target WAD path: '{targetWadFullPath}'");

                if (File.Exists(targetWadFullPath))
                {
                    var wadContent = await _wadNodeLoaderService.LoadWadContentAsync(targetWadFullPath);
                    var binNode = wadContent.FirstOrDefault(n => n.FullPath.Equals(strategy.BinPath, StringComparison.OrdinalIgnoreCase));
                    if (binNode != null)
                    {
                        _logService.LogDebug($"[FindAssociatedBinFileFromWadsAsync] Live Mode: BIN node '{strategy.BinPath}' found in WAD '{targetWadFullPath}'.");
                        return (binNode, baseName, strategy.Type);
                    }
                    else
                    {
                        _logService.LogWarning($"[FindAssociatedBinFileFromWadsAsync] Live Mode: BIN node '{strategy.BinPath}' NOT found in WAD '{targetWadFullPath}'.");
                    }
                }
                else
                {
                    _logService.LogWarning($"[FindAssociatedBinFileFromWadsAsync] Live Mode: Target WAD for BIN search NOT found: '{targetWadFullPath}'");
                }

                return (null, baseName, strategy.Type);
            }
        }

        private (FileSystemNodeModel WpkNode, FileSystemNodeModel AudioBnkNode, FileSystemNodeModel EventsBnkNode) FindSiblingFilesByName(FileSystemNodeModel clickedNode, ObservableRangeCollection<FileSystemNodeModel> rootNodes)
        {
            // A simple way to detect backup mode is to check if the node has ChunkDiff data.
            bool isBackupMode = clickedNode.ChunkDiff != null;

            string baseName = clickedNode.Name.Replace("_audio.wpk", "").Replace("_audio.bnk", "").Replace("_events.bnk", "");
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

                wpkNode = parentNode.Children.FirstOrDefault(c => c.Name == expectedWpkName);
                audioBnkNode = parentNode.Children.FirstOrDefault(c => c.Name == expectedAudioBnkName);
                eventsBnkNode = parentNode.Children.FirstOrDefault(c => c.Name == expectedEventsBnkName);
            }
            else // Backup Mode - search across the whole tree
            {
                _logService.LogDebug("[FindSiblingFilesByName] Backup mode detected. Searching entire tree for siblings.");
                var namesToFind = new List<string> { expectedWpkName, expectedAudioBnkName, expectedEventsBnkName };
                var allMatches = new List<FileSystemNodeModel>();
                
                FindAllNodesByNameRecursive(rootNodes, namesToFind, allMatches);

                wpkNode = allMatches.FirstOrDefault(n => n.Name == expectedWpkName);
                audioBnkNode = allMatches.FirstOrDefault(n => n.Name == expectedAudioBnkName);
                eventsBnkNode = allMatches.FirstOrDefault(n => n.Name == expectedEventsBnkName);

                _logService.LogDebug($"[FindSiblingFilesByName] Sibling search results - WPK: {wpkNode != null}, AudioBNK: {audioBnkNode != null}, EventsBNK: {eventsBnkNode != null}");
            }

            return (wpkNode, audioBnkNode, eventsBnkNode);
        }

        private async Task<(FileSystemNodeModel BinNode, string BaseName, BinType Type)> FindAssociatedBinFileAsync(FileSystemNodeModel clickedNode, ObservableRangeCollection<FileSystemNodeModel> rootNodes, string currentRootPath)
        {
            string baseName = clickedNode.Name.Replace("_audio.wpk", "").Replace("_audio.bnk", "").Replace("_events.bnk", "");
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
                    string[] prefixes = { "[~] Modified/", "[+] New/", "[»] Renamed/", "[=] Dependency/" };
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
