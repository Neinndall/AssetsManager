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
            _logService.LogDebug($"[LinkAudioBankForDiffAsync] Linking audio bank for diff view. Node: '{clickedNode.Name}', Path: '{clickedNode.FullPath}', PreferOld: {preferOld}, BackupRootDir: '{backupRootDir}'");

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

            // 1. BACKUP MODE: STRICTLY use backup data (JSON and chunks)
            if (backupRootDir != null)
            {
                _logService.LogDebug($"[LinkAudioBankForDiffAsync] [BACKUP MODE] STRICT resolution via JSON index.");
                eventsBnkNode = clickedNode; // CRITICAL: Always assign events node first
                
                if (clickedNode.ChunkDiff != null)
                {
                    string backupJsonPath = Path.Combine(backupRootDir, "wadcomparison.json");
                    if (File.Exists(backupJsonPath))
                    {
                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
                        string jsonContent = await File.ReadAllTextAsync(backupJsonPath);
                        var comparisonData = JsonSerializer.Deserialize<WadComparisonData>(jsonContent, options);

                        // Resolve BIN metadata
                        var strategy = GetBinFileSearchStrategy(clickedNode);
                        if (strategy != null)
                        {
                            binType = strategy.Type;
                            _logService.LogDebug($"[LinkAudioBankForDiffAsync] [BACKUP] Strategy identified: {strategy.Type}, Path: '{strategy.BinPath}'");

                            // DEEP SEARCH for the BIN in the whole JSON structure
                            SerializableChunkDiff binDiff = null;

                            // A. Check top-level diffs first (Modified, New, etc.)
                            binDiff = comparisonData?.Diffs.FirstOrDefault(d => 
                                string.Equals(d.NewPath, strategy.BinPath, StringComparison.OrdinalIgnoreCase) || 
                                string.Equals(d.OldPath, strategy.BinPath, StringComparison.OrdinalIgnoreCase));

                            // B. Check the clicked node's DIRECT dependencies
                            if (binDiff == null && clickedNode.ChunkDiff.Dependencies != null)
                            {
                                var depMatch = clickedNode.ChunkDiff.Dependencies.FirstOrDefault(d => string.Equals(d.Path, strategy.BinPath, StringComparison.OrdinalIgnoreCase));
                                if (depMatch != null)
                                {
                                    binDiff = new SerializableChunkDiff
                                    {
                                        Type = depMatch.Type ?? ChunkDiffType.Dependency,
                                        OldPath = depMatch.Path, NewPath = depMatch.Path,
                                        SourceWadFile = depMatch.SourceWad,
                                        OldPathHash = depMatch.OldPathHash, NewPathHash = depMatch.NewPathHash,
                                        OldCompressionType = depMatch.CompressionType, NewCompressionType = depMatch.CompressionType
                                    };
                                    _logService.LogDebug($"[LinkAudioBankForDiffAsync] [BACKUP] Found BIN in direct Dependencies.");
                                }
                            }

                            // C. Check ALL dependencies of ALL files in the JSON (Last resort)
                            if (binDiff == null && comparisonData?.Diffs != null)
                            {
                                foreach (var d in comparisonData.Diffs)
                                {
                                    if (d.Dependencies != null)
                                    {
                                        var depMatch = d.Dependencies.FirstOrDefault(dep => string.Equals(dep.Path, strategy.BinPath, StringComparison.OrdinalIgnoreCase));
                                        if (depMatch != null)
                                        {
                                            binDiff = new SerializableChunkDiff
                                            {
                                                Type = depMatch.Type ?? ChunkDiffType.Dependency,
                                                OldPath = depMatch.Path, NewPath = depMatch.Path,
                                                SourceWadFile = depMatch.SourceWad,
                                                OldPathHash = depMatch.OldPathHash, NewPathHash = depMatch.NewPathHash,
                                                OldCompressionType = depMatch.CompressionType, NewCompressionType = depMatch.CompressionType
                                            };
                                            _logService.LogDebug($"[LinkAudioBankForDiffAsync] [BACKUP] Found BIN in indirect dependencies.");
                                            break;
                                        }
                                    }
                                }
                            }

                            if (binDiff != null)
                            {
                                bool useOld = preferOld || binDiff.Type == ChunkDiffType.Removed;
                                ulong hashForPath = useOld ? binDiff.OldPathHash : binDiff.NewPathHash;
                                string chunkDir = useOld ? "old" : "new";

                                binNode = new FileSystemNodeModel(Path.GetFileName(strategy.BinPath), false, strategy.BinPath, binDiff.SourceWadFile)
                                {
                                    ChunkDiff = binDiff,
                                    BackupChunkPath = Path.Combine(backupRootDir, "wad_chunks", chunkDir, binDiff.SourceWadFile, $"{hashForPath:X16}.chunk"),
                                    Type = NodeType.SoundBank
                                };
                                binData = await _wadContentProvider.GetVirtualFileBytesAsync(binNode);
                                _logService.LogDebug($"[LinkAudioBankForDiffAsync] [BACKUP] BinData loaded: {binData?.Length ?? 0} bytes.");
                            }
                        }

                        // Resolve SIBLINGS (same directory)
                        string directoryPath = Path.GetDirectoryName(clickedNode.FullPath).Replace('\\', '/');
                        var siblings = comparisonData?.Diffs.Where(d =>
                            (d.NewPath != null && Path.GetDirectoryName(d.NewPath).Replace('\\', '/') == directoryPath) ||
                            (d.OldPath != null && Path.GetDirectoryName(d.OldPath).Replace('\\', '/') == directoryPath))
                            .ToList() ?? new List<SerializableChunkDiff>();

                        // FIX: Also check in Dependencies of the clicked node
                        if (clickedNode.ChunkDiff.Dependencies != null)
                        {
                            foreach (var dep in clickedNode.ChunkDiff.Dependencies)
                            {
                                if (Path.GetDirectoryName(dep.Path).Replace('\\', '/') == directoryPath)
                                {
                                    var depDiff = new SerializableChunkDiff
                                    {
                                        Type = dep.Type ?? ChunkDiffType.Dependency,
                                        OldPath = dep.Path, NewPath = dep.Path,
                                        SourceWadFile = dep.SourceWad,
                                        OldPathHash = dep.OldPathHash, NewPathHash = dep.NewPathHash,
                                        OldCompressionType = dep.CompressionType, NewCompressionType = dep.CompressionType
                                    };
                                    siblings.Add(depDiff);
                                }
                            }
                        }

                        _logService.LogDebug($"[LinkAudioBankForDiffAsync] [BACKUP] Found {siblings.Count} siblings. Searching containers...");
                        
                        var wpkDiff = siblings.FirstOrDefault(d => (d.NewPath ?? d.OldPath).EndsWith(baseName + "_audio.wpk", StringComparison.OrdinalIgnoreCase))
                                   ?? siblings.FirstOrDefault(d => (d.NewPath ?? d.OldPath).EndsWith(baseName + ".wpk", StringComparison.OrdinalIgnoreCase));
                        
                        var audioBnkDiff = siblings.FirstOrDefault(d => (d.NewPath ?? d.OldPath).EndsWith(baseName + "_audio.bnk", StringComparison.OrdinalIgnoreCase))
                                        ?? siblings.FirstOrDefault(d => (d.NewPath ?? d.OldPath).EndsWith(baseName + ".bnk", StringComparison.OrdinalIgnoreCase) 
                                                                     && !(d.NewPath ?? d.OldPath).EndsWith("_events.bnk", StringComparison.OrdinalIgnoreCase));

                        Func<SerializableChunkDiff, FileSystemNodeModel> createNode = (diff) =>
                        {
                            if (diff == null) return null;
                            bool useOld = preferOld || diff.Type == ChunkDiffType.Removed;
                            ulong hashForPath = useOld ? diff.OldPathHash : diff.NewPathHash;
                            string chunkDir = useOld ? "old" : "new";
                            string fPath = useOld ? diff.OldPath : diff.NewPath;

                            return new FileSystemNodeModel(Path.GetFileName(fPath), false, fPath, diff.SourceWadFile)
                            {
                                ChunkDiff = diff,
                                BackupChunkPath = Path.Combine(backupRootDir, "wad_chunks", chunkDir, diff.SourceWadFile, $"{hashForPath:X16}.chunk"),
                                Type = NodeType.SoundBank
                            };
                        };

                        wpkNode = createNode(wpkDiff);
                        audioBnkNode = createNode(audioBnkDiff);
                        eventsBnkNode = clickedNode;

                        // NEW: Master Events Bank Fallback for Regional Locales
                        // Detect regionality: Champion.Locale.wad.client (length >= 4) and not en_US
                        string sourceWad = clickedNode.ChunkDiff.SourceWadFile.ToLower();
                        var wadParts = Path.GetFileName(sourceWad).Split('.');
                        bool isRegionalWad = wadParts.Length >= 4 && !sourceWad.Contains(".en_us.");

                        if (isRegionalWad)
                        {
                            _logService.LogDebug($"[LinkAudioBankForDiffAsync] [BACKUP] Regional WAD detected: '{sourceWad}'. Searching for Master Events Bank in en_US counterpart using direct path...");
                            
                            // Use the EXACT same path (Riot usually shares en_us paths across all locales)
                            string targetPath = clickedNode.FullPath;
                            
                            var masterDiff = comparisonData?.Diffs.FirstOrDefault(d => 
                                (string.Equals(d.NewPath, targetPath, StringComparison.OrdinalIgnoreCase) || 
                                 string.Equals(d.OldPath, targetPath, StringComparison.OrdinalIgnoreCase)) &&
                                d.SourceWadFile.EndsWith(".en_US.wad.client", StringComparison.OrdinalIgnoreCase));

                            if (masterDiff == null && comparisonData?.Diffs != null)
                            {
                                foreach (var d in comparisonData.Diffs)
                                {
                                    if (d.Dependencies != null)
                                    {
                                        var depMatch = d.Dependencies.FirstOrDefault(dep => 
                                            string.Equals(dep.Path, targetPath, StringComparison.OrdinalIgnoreCase) && 
                                            dep.SourceWad.EndsWith(".en_US.wad.client", StringComparison.OrdinalIgnoreCase));

                                        if (depMatch != null)
                                        {
                                            masterDiff = new SerializableChunkDiff
                                            {
                                                Type = depMatch.Type ?? ChunkDiffType.Dependency,
                                                OldPath = depMatch.Path, NewPath = depMatch.Path,
                                                SourceWadFile = depMatch.SourceWad,
                                                OldPathHash = depMatch.OldPathHash, NewPathHash = depMatch.NewPathHash,
                                                OldCompressionType = depMatch.CompressionType, NewCompressionType = depMatch.CompressionType
                                            };
                                            break;
                                        }
                                    }
                                }
                            }

                            if (masterDiff != null)
                            {
                                var masterNode = createNode(masterDiff);
                                if (masterNode != null)
                                {
                                    _logService.LogDebug($"[LinkAudioBankForDiffAsync] [BACKUP] Successfully linked Master Events Bank (Exact Path) from en_US WAD.");
                                    eventsBnkNode = masterNode; 
                                }
                            }
                        }

                        _logService.LogDebug($"[LinkAudioBankForDiffAsync] [BACKUP] Siblings resolved: WPK={(wpkNode != null)}, AudioBNK={(audioBnkNode != null)}");
                    }
                }
            }
            // 2. LIVE MODE
            else
            {
                _logService.LogDebug($"[LinkAudioBankForDiffAsync] [LIVE MODE] Resolving via strategies and physical WADs.");
                var strategy = GetBinFileSearchStrategy(clickedNode);
                if (strategy != null)
                {
                    binType = strategy.Type;
                    string targetWadFullPath = ResolveTargetWadPath(clickedNode, basePath, strategy.TargetWadName);
                    
                    if (File.Exists(targetWadFullPath))
                    {
                        var wadContent = await _wadNodeLoaderService.LoadWadContentAsync(targetWadFullPath);
                        binNode = FindNodeByPath(wadContent, strategy.BinPath);
                        if (binNode != null)
                        {
                            binData = await _wadContentProvider.GetVirtualFileBytesAsync(binNode);
                            _logService.LogDebug($"[LinkAudioBankForDiffAsync] [LIVE] Loaded .bin data from live WAD. Size: {binData?.Length ?? 0} bytes.");
                        }

                        // Siblings
                        string sourceWadFullPath = Path.Combine(basePath, clickedNode.ChunkDiff.SourceWadFile);
                        if (string.Equals(targetWadFullPath, sourceWadFullPath, StringComparison.OrdinalIgnoreCase))
                        {
                            wpkNode = FindNodeByName(wadContent, baseName + "_audio.wpk");
                            audioBnkNode = FindNodeByName(wadContent, baseName + "_audio.bnk");
                            eventsBnkNode = FindNodeByName(wadContent, baseName + "_events.bnk");
                        }
                    }
                }

                if (eventsBnkNode == null)
                {
                    var siblingsResult = await FindSiblingFilesFromWadsAsync(clickedNode, basePath, preferOld, null);
                    wpkNode = wpkNode ?? siblingsResult.WpkNode;
                    audioBnkNode = audioBnkNode ?? siblingsResult.AudioBnkNode;
                    eventsBnkNode = siblingsResult.EventsBnkNode;
                }
            }

            _logService.LogDebug($"[LinkAudioBankForDiffAsync] Final results - BinData: {binData != null}, WpkNode: {wpkNode != null}, AudioBnkNode: {audioBnkNode != null}, EventsBnkNode: {eventsBnkNode != null}");

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

        private string ResolveTargetWadPath(FileSystemNodeModel clickedNode, string basePath, string targetWadName)
        {
            if (clickedNode.ChunkDiff == null || string.IsNullOrEmpty(basePath)) return null;

            string sourceWadRelativePath = clickedNode.ChunkDiff.SourceWadFile;
            string sourceWadFileName = Path.GetFileName(sourceWadRelativePath);

            if (string.Equals(sourceWadFileName, targetWadName, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(basePath, sourceWadRelativePath);
            
            string sourceWadDirectory = Path.GetDirectoryName(sourceWadRelativePath);
            string potentialPath = Path.Combine(basePath, sourceWadDirectory, targetWadName);
            if (File.Exists(potentialPath)) return potentialPath;

            return Path.Combine(basePath, targetWadName);
        }

        public async Task<LinkedAudioBank> LinkAudioBankAsync(FileSystemNodeModel clickedNode, ObservableRangeCollection<FileSystemNodeModel> rootNodes, string currentRootPath, string newLolPath = null, string oldLolPath = null)
        {
            if (clickedNode.ChunkDiff != null)
            {
                var (binNode, baseName, binType) = await FindAssociatedBinFileAsync(clickedNode, rootNodes, null);
                byte[] binData = null;
                if (binNode != null) binData = await _wadContentProvider.GetVirtualFileBytesAsync(binNode);

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
                var (binNode, baseName, binType) = await FindAssociatedBinFileAsync(clickedNode, rootNodes, currentRootPath);
                byte[] binData = null;
                if (binNode != null) binData = await _wadContentProvider.GetVirtualFileBytesAsync(binNode);

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

            if (backupRootDir != null || basePath == null)
            {
                if (clickedNode.ChunkDiff != null)
                {
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

                            // FIX: Also check in Dependencies of the clicked node
                            if (clickedNode.ChunkDiff.Dependencies != null)
                            {
                                foreach (var dep in clickedNode.ChunkDiff.Dependencies)
                                {
                                    if (Path.GetDirectoryName(dep.Path).Replace('\\', '/') == directoryPath)
                                    {
                                        var depDiff = new SerializableChunkDiff
                                        {
                                            Type = dep.Type ?? ChunkDiffType.Dependency,
                                            OldPath = dep.Path, NewPath = dep.Path,
                                            SourceWadFile = dep.SourceWad,
                                            OldPathHash = dep.OldPathHash, NewPathHash = dep.NewPathHash,
                                            OldCompressionType = dep.CompressionType, NewCompressionType = dep.CompressionType
                                        };
                                        siblings.Add(depDiff);
                                    }
                                }
                            }

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

                                    return new FileSystemNodeModel(Path.GetFileName(fullPath), false, fullPath, diff.SourceWadFile)
                                    {
                                        ChunkDiff = diff,
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
            else
            {
                string wadPath = Path.Combine(basePath, clickedNode.ChunkDiff.SourceWadFile);
                return await FindSiblingFilesFromWadsInternalAsync(clickedNode, wadPath, baseName);
            }
        }

        private async Task<(FileSystemNodeModel WpkNode, FileSystemNodeModel AudioBnkNode, FileSystemNodeModel EventsBnkNode)> FindSiblingFilesFromWadsInternalAsync(FileSystemNodeModel clickedNode, string wadPath, string baseName)
        {
            if (!File.Exists(wadPath)) return (null, null, null);

            var wadContent = await _wadNodeLoaderService.LoadWadContentAsync(wadPath);

            FileSystemNodeModel wpkNode = FindNodeByName(wadContent, baseName + "_audio.wpk");
            FileSystemNodeModel audioBnkNode = FindNodeByName(wadContent, baseName + "_audio.bnk");
            FileSystemNodeModel eventsBnkNode = FindNodeByName(wadContent, baseName + "_events.bnk");

            return (wpkNode, audioBnkNode, eventsBnkNode);
        }

        private record BinFileStrategy(string BinPath, string TargetWadName, BinType Type);

        private BinFileStrategy GetBinFileSearchStrategy(FileSystemNodeModel clickedNode)
        {
            string sourceWadName = Path.GetFileName(clickedNode.SourceWadPath);

            // Strategy 0: Companions
            if (clickedNode.FullPath.Contains("/companions/pets/"))
            {
                var pathParts = clickedNode.FullPath.Split('/');
                int petsIndex = Array.IndexOf(pathParts, "pets");
                if (petsIndex != -1 && pathParts.Length > petsIndex + 2)
                {
                    string petName = pathParts[petsIndex + 1];
                    string themeName = pathParts[petsIndex + 2];
                    if (!string.IsNullOrEmpty(petName) && !string.IsNullOrEmpty(themeName))
                        return new BinFileStrategy($"data/characters/{petName}/themes/{themeName}/root.bin", "companions.wad.client", BinType.Companion);
                }
            }

            // Strategy 1: Champion Skins Structure
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
                        return new BinFileStrategy($"data/characters/{championName}/skins/{skinName}.bin", $"{championName.ToLower()}.wad.client", BinType.Champion);
                    }
                }
            }

            // Strategy 2: Robust champion VO inference (CRITICAL: Move before generic inference)
            if (clickedNode.Name.Contains("_vo_", StringComparison.OrdinalIgnoreCase))
            {
                string baseName = GetBaseName(clickedNode.Name);
                string[] baseParts = baseName.Split('_');
                if (baseParts.Length > 0)
                {
                    string championName = baseParts[0].ToLower();
                    if (championName != "map" && championName != "common" && championName != "misc")
                    {
                        string skinName = "skin0";
                        var skinPart = baseParts.FirstOrDefault(p => p.StartsWith("skin", StringComparison.OrdinalIgnoreCase));
                        if (skinPart != null && int.TryParse(skinPart.Replace("skin", ""), out int skinId)) skinName = $"skin{skinId}";
                        return new BinFileStrategy($"data/characters/{championName}/skins/{skinName}.bin", $"{championName}.wad.client", BinType.Champion);
                    }
                }
            }

            // Strategy 3: Locale VO
            if (clickedNode.Name.Contains("_vo_", StringComparison.OrdinalIgnoreCase) &&
                sourceWadName.StartsWith("Common.", StringComparison.OrdinalIgnoreCase))
                return new BinFileStrategy("data/maps/shipping/common/common.bin", "Common.wad.client", BinType.Map);

            // Strategy 4: Maps/Common
            var wadNameParts = sourceWadName.Split('.');
            if (wadNameParts.Length > 0 && (sourceWadName.StartsWith("Map", StringComparison.OrdinalIgnoreCase) || sourceWadName.StartsWith("Common", StringComparison.OrdinalIgnoreCase)))
            {
                string mapName = wadNameParts[0];
                if (!string.IsNullOrEmpty(mapName))
                {
                    string binPath = mapName.Equals("Common", StringComparison.OrdinalIgnoreCase) ? "data/maps/shipping/common/common.bin" : $"data/maps/shipping/{mapName.ToLower()}/{mapName.ToLower()}.bin";
                    return new BinFileStrategy(binPath, $"{mapName.ToLower()}.wad.client", BinType.Map);
                }
            }

            // Strategy 5: Generic Infer from WAD name (Pattern: Champion.Locale.wad.client)
            if (wadNameParts.Length > 0)
            {
                string championName = wadNameParts[0];
                if (!championName.StartsWith("map", StringComparison.OrdinalIgnoreCase) && 
                    !championName.Equals("common", StringComparison.OrdinalIgnoreCase) &&
                    !championName.Equals("companions", StringComparison.OrdinalIgnoreCase))
                    return new BinFileStrategy($"data/characters/{championName.ToLower()}/skins/skin0.bin", $"{championName.ToLower()}.wad.client", BinType.Champion);
            }

            return null;
        }

        private async Task<(FileSystemNodeModel BinNode, string BaseName, BinType Type)> FindAssociatedBinFileFromWadsAsync(FileSystemNodeModel clickedNode, string basePath, bool preferOld = false, string backupRootDir = null)
        {
            string baseName = GetBaseName(clickedNode.Name);
            var strategy = GetBinFileSearchStrategy(clickedNode);
            if (strategy == null) return (null, baseName, BinType.Unknown);

            if (backupRootDir != null || basePath == null)
            {
                if (clickedNode.ChunkDiff != null)
                {
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

                            // SEARCH DEEP in JSON
                            SerializableChunkDiff binDiff = comparisonData?.Diffs.FirstOrDefault(d => 
                                string.Equals(d.NewPath, strategy.BinPath, StringComparison.OrdinalIgnoreCase) || 
                                string.Equals(d.OldPath, strategy.BinPath, StringComparison.OrdinalIgnoreCase));

                            if (binDiff == null && comparisonData?.Diffs != null)
                            {
                                foreach (var d in comparisonData.Diffs)
                                {
                                    if (d.Dependencies != null)
                                    {
                                        var depMatch = d.Dependencies.FirstOrDefault(dep => string.Equals(dep.Path, strategy.BinPath, StringComparison.OrdinalIgnoreCase));
                                        if (depMatch != null)
                                        {
                                            binDiff = new SerializableChunkDiff
                                            {
                                                Type = depMatch.Type ?? ChunkDiffType.Dependency,
                                                OldPath = depMatch.Path, NewPath = depMatch.Path,
                                                SourceWadFile = depMatch.SourceWad,
                                                OldPathHash = depMatch.OldPathHash, NewPathHash = depMatch.NewPathHash,
                                                OldCompressionType = depMatch.CompressionType, NewCompressionType = depMatch.CompressionType
                                            };
                                            break;
                                        }
                                    }
                                }
                            }

                            if (binDiff != null)
                            {
                                bool useOld = preferOld || binDiff.Type == ChunkDiffType.Removed;
                                ulong hashForPath = useOld ? binDiff.OldPathHash : binDiff.NewPathHash;
                                string chunkDir = useOld ? "old" : "new";
                                var binNode = new FileSystemNodeModel(Path.GetFileName(strategy.BinPath), false, strategy.BinPath, binDiff.SourceWadFile)
                                {
                                    ChunkDiff = binDiff,
                                    BackupChunkPath = Path.Combine(backupRootDir, "wad_chunks", chunkDir, binDiff.SourceWadFile, $"{hashForPath:X16}.chunk")
                                };
                                return (binNode, baseName, strategy.Type);
                            }
                        }
                    }
                }
                return (null, baseName, strategy.Type);
            }
            else
            {
                string targetWadFullPath = ResolveTargetWadPath(clickedNode, basePath, strategy.TargetWadName);
                if (File.Exists(targetWadFullPath))
                {
                    var wadContent = await _wadNodeLoaderService.LoadWadContentAsync(targetWadFullPath);
                    var binNode = FindNodeByPath(wadContent, strategy.BinPath);
                    if (binNode != null) return (binNode, baseName, strategy.Type);
                }
                return (null, baseName, strategy.Type);
            }
        }


        private (FileSystemNodeModel WpkNode, FileSystemNodeModel AudioBnkNode, FileSystemNodeModel EventsBnkNode) FindSiblingFilesByName(FileSystemNodeModel clickedNode, ObservableRangeCollection<FileSystemNodeModel> rootNodes)
        {
            bool isBackupMode = clickedNode.ChunkDiff != null;
            string baseName = GetBaseName(clickedNode.Name);
            string expectedWpkName = baseName + "_audio.wpk";
            string expectedAudioBnkName = baseName + "_audio.bnk";
            string expectedEventsBnkName = baseName + "_events.bnk";

            if (!isBackupMode)
            {
                var parentPath = _treeUIManager.FindNodePath(rootNodes, clickedNode);
                if (parentPath == null || parentPath.Count < 2) return (null, null, null);
                var parentNode = parentPath[parentPath.Count - 2];
                return (parentNode.Children.FirstOrDefault(c => c.Name.Equals(expectedWpkName, StringComparison.OrdinalIgnoreCase)),
                        parentNode.Children.FirstOrDefault(c => c.Name.Equals(expectedAudioBnkName, StringComparison.OrdinalIgnoreCase)),
                        parentNode.Children.FirstOrDefault(c => c.Name.Equals(expectedEventsBnkName, StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                var namesToFind = new List<string> { expectedWpkName, expectedAudioBnkName, expectedEventsBnkName };
                var allMatches = new List<FileSystemNodeModel>();
                FindAllNodesByNameRecursive(rootNodes, namesToFind, allMatches);
                return (allMatches.FirstOrDefault(n => n.Name.Equals(expectedWpkName, StringComparison.OrdinalIgnoreCase)),
                        allMatches.FirstOrDefault(n => n.Name.Equals(expectedAudioBnkName, StringComparison.OrdinalIgnoreCase)),
                        allMatches.FirstOrDefault(n => n.Name.Equals(expectedEventsBnkName, StringComparison.OrdinalIgnoreCase)));
            }
        }

        private async Task<(FileSystemNodeModel BinNode, string BaseName, BinType Type)> FindAssociatedBinFileAsync(FileSystemNodeModel clickedNode, ObservableRangeCollection<FileSystemNodeModel> rootNodes, string currentRootPath)
        {
            string baseName = GetBaseName(clickedNode.Name);
            var strategy = GetBinFileSearchStrategy(clickedNode);
            if (strategy == null) return (null, baseName, BinType.Unknown);

            if (currentRootPath == null)
            {
                var binNode = FindNodeByPath(rootNodes, strategy.BinPath);
                if (binNode == null)
                {
                    string[] prefixes = { "Modified/", "New/", "Renamed/", "Dependency/" };
                    foreach (var prefix in prefixes)
                    {
                        binNode = FindNodeByPath(rootNodes, prefix + strategy.BinPath);
                        if (binNode != null) break;
                    }
                }
                return (binNode, baseName, strategy.Type);
            }
            else
            {
                Func<FileSystemNodeModel, Task> loader = async (node) => await _wadNodeLoaderService.EnsureAllChildrenLoadedAsync(node, currentRootPath);
                var targetWadNode = FindNodeByName(rootNodes, strategy.TargetWadName);
                if (targetWadNode != null)
                {
                    var binNode = await _wadSearchBoxService.PerformSearchAsync(strategy.BinPath, new ObservableRangeCollection<FileSystemNodeModel> { targetWadNode }, loader);
                    if (binNode != null) return (binNode, baseName, strategy.Type);
                }
                var commonWadNode = FindNodeByName(rootNodes, "Common.wad.client");
                if (commonWadNode != null && commonWadNode != targetWadNode)
                {
                    var binNodeInCommon = await _wadSearchBoxService.PerformSearchAsync(strategy.BinPath, new ObservableRangeCollection<FileSystemNodeModel> { commonWadNode }, loader);
                    if (binNodeInCommon != null) return (binNodeInCommon, baseName, strategy.Type);
                }
            }
            return (null, baseName, strategy.Type);
        }

        private string GetBaseName(string name)
        {
            if (name.EndsWith("_audio.wpk", StringComparison.OrdinalIgnoreCase)) return name.Substring(0, name.Length - 10);
            if (name.EndsWith("_audio.bnk", StringComparison.OrdinalIgnoreCase)) return name.Substring(0, name.Length - 10);
            if (name.EndsWith("_events.bnk", StringComparison.OrdinalIgnoreCase)) return name.Substring(0, name.Length - 11);
            if (name.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)) return name.Substring(0, name.Length - 4);
            if (name.EndsWith(".wpk", StringComparison.OrdinalIgnoreCase)) return name.Substring(0, name.Length - 4);
            return name;
        }

        private void FindAllNodesByNameRecursive(IEnumerable<FileSystemNodeModel> nodes, List<string> namesToFind, List<FileSystemNodeModel> foundNodes)
        {
            foreach (var node in nodes)
            {
                if (namesToFind.Contains(node.Name)) foundNodes.Add(node);
                if (node.Children != null && node.Children.Any()) FindAllNodesByNameRecursive(node.Children, namesToFind, foundNodes);
            }
        }

        private FileSystemNodeModel FindNodeByName(IEnumerable<FileSystemNodeModel> nodes, string name)
        {
            foreach (var node in nodes)
            {
                if (node.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return node;
                if (node.Children != null && node.Children.Any())
                {
                    var found = FindNodeByName(node.Children, name);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private FileSystemNodeModel FindNodeByPath(IEnumerable<FileSystemNodeModel> nodes, string fullPath)
        {
            foreach (var node in nodes)
            {
                if (node.FullPath != null && node.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)) return node;
                if (node.Children != null && node.Children.Any())
                {
                    var found = FindNodeByPath(node.Children, fullPath);
                    if (found != null) return found;
                }
            }
            return null;
        }
    }
}
