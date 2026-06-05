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
            _logService.LogDebug($"[LinkAudioBankForDiffAsync] Linking audio bank for diff view. Node: '{clickedNode.Name}', Path: '{clickedNode.VirtualPath}', PreferOld: {preferOld}, BackupRootDir: '{backupRootDir}'");

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
                        string directoryPath = Path.GetDirectoryName(clickedNode.VirtualPath).Replace('\\', '/');
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
                            string targetPath = clickedNode.VirtualPath;
                            
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
                    string targetWadVirtualPath = ResolveTargetWadPath(clickedNode, basePath, strategy.TargetWadName);
                    
                    if (File.Exists(targetWadVirtualPath))
                    {
                        var wadContent = await _wadNodeLoaderService.LoadWadContentAsync(targetWadVirtualPath);
                        binNode = FindNodeByPath(wadContent, strategy.BinPath);
                        if (binNode != null)
                        {
                            binData = await _wadContentProvider.GetVirtualFileBytesAsync(binNode);
                            _logService.LogDebug($"[LinkAudioBankForDiffAsync] [LIVE] Loaded .bin data from live WAD. Size: {binData?.Length ?? 0} bytes.");
                        }

                        // Siblings
                        string sourceWadVirtualPath = Path.Combine(basePath, clickedNode.ChunkDiff.SourceWadFile);
                        if (string.Equals(targetWadVirtualPath, sourceWadVirtualPath, StringComparison.OrdinalIgnoreCase))
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
            bool isBackupMode = clickedNode.ChunkDiff != null;
            string modeLabel = isBackupMode ? "BACKUP" : "LIVE";
            
            _logService.LogDebug($"[LinkAudioBankAsync] [{modeLabel} MODE] Linking audio bank. Node: '{clickedNode.Name}', Path: '{clickedNode.VirtualPath}'");

            if (isBackupMode)
            {
                var (binNode, baseName, binType) = await FindAssociatedBinFileAsync(clickedNode, rootNodes, null);
                _logService.LogDebug($"[LinkAudioBankAsync] [BACKUP] BinNode resolution result: {(binNode != null ? "FOUND" : "NOT FOUND")}. Strategy: {binType}");

                byte[] binData = null;
                if (binNode != null) binData = await _wadContentProvider.GetVirtualFileBytesAsync(binNode);

                var siblingsResult = FindSiblingFilesByName(clickedNode, rootNodes);
                _logService.LogDebug($"[LinkAudioBankAsync] [BACKUP] Siblings: WPK={siblingsResult.WpkNode != null}, AudioBNK={siblingsResult.AudioBnkNode != null}, EventsBNK={siblingsResult.EventsBnkNode != null}");

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
                _logService.LogDebug($"[LinkAudioBankAsync] [LIVE] BinNode resolved: {binNode != null}, BaseName: {baseName}, Type: {binType}");

                byte[] binData = null;
                if (binNode != null) binData = await _wadContentProvider.GetVirtualFileBytesAsync(binNode);

                var siblingsResult = await FindSiblingFilesFromWadsInternalAsync(clickedNode, clickedNode.SourceWadPath, baseName);
                _logService.LogDebug($"[LinkAudioBankAsync] [LIVE] Siblings: WPK={siblingsResult.WpkNode != null}, AudioBNK={siblingsResult.AudioBnkNode != null}, EventsBNK={siblingsResult.EventsBnkNode != null}");

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

                            string directoryPath = Path.GetDirectoryName(clickedNode.VirtualPath).Replace('\\', '/');
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
                                    string fPath = useOld ? diff.OldPath : diff.NewPath;

                                    return new FileSystemNodeModel(Path.GetFileName(fPath), false, fPath, diff.SourceWadFile)
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

        public record BinFileStrategy(string BinPath, string TargetWadName, BinType Type);

        // Public metadata for an audio-bank dependency (used by DiffViewService
        // to read bytes from the wad_chunks/old|new backup directory without
        // re-running the resolution logic).
        public enum AudioDependencyType { Bin, EventsBnk, AudioBnk, AudioWpk }

        public class AudioDependencyInfo
        {
            public string Path { get; set; }
            public string SourceWad { get; set; }
            public ulong PathHash { get; set; }
            public AudioDependencyType Type { get; set; }
        }

        // String-based public entry point. Reuses the exact same 5-strategy
        // resolution as the FileSystemNodeModel overload so that WadPackagingService
        // (which works on SerializableChunkDiff / virtual paths) and DiffViewService
        // get identical results to the Explorer tree linking flow.
        public BinFileStrategy GetBinFileSearchStrategy(string virtualPath, string sourceWadPath)
        {
            if (string.IsNullOrEmpty(virtualPath) || string.IsNullOrEmpty(sourceWadPath)) return null;
            return GetBinFileSearchStrategyCore(virtualPath, sourceWadPath, Path.GetFileName(virtualPath));
        }

        private BinFileStrategy GetBinFileSearchStrategy(FileSystemNodeModel clickedNode)
        {
            // 1. IDENTIDAD DEL CONTENEDOR (WAD) - Fuente de verdad absoluta
            string sourceWadPath = clickedNode.SourceWadPath ?? clickedNode.ChunkDiff?.SourceWadFile;
            if (string.IsNullOrEmpty(sourceWadPath)) return null;
            return GetBinFileSearchStrategyCore(clickedNode.VirtualPath, sourceWadPath, clickedNode.Name);
        }

        // Core resolution: container-aware (Map/Common) -> container-aware
        // (Champion/regional) -> path-based (Companion) -> filename-fallback (VO).
        // Shared by the FileSystemNodeModel-based tree linking flow and the
        // string-based diff/packaging flows.
        private BinFileStrategy GetBinFileSearchStrategyCore(string virtualPath, string sourceWadPath, string fileName)
        {
            if (string.IsNullOrEmpty(virtualPath) || string.IsNullOrEmpty(sourceWadPath)) return null;

            string sourceWadName = Path.GetFileName(sourceWadPath); // Ej: Aatrox.en_US.wad.client
            var wadParts = sourceWadName.Split('.');
            if (wadParts.Length < 1) return null;

            string wadPrefix = wadParts[0]; // Map11, Common, Aatrox, etc.
            bool isMap = wadPrefix.StartsWith("Map", StringComparison.OrdinalIgnoreCase);
            bool isCommon = wadPrefix.Equals("Common", StringComparison.OrdinalIgnoreCase);

            _logService.LogDebug($"[GetBinFileSearchStrategy] Container identified: '{sourceWadName}' (Prefix: {wadPrefix})");

            // 2. DETECCIÓN POR CONTENEDOR (Mapas)
            if (isMap || isCommon)
            {
                string mapId = wadPrefix.ToLower();
                string binPath = isCommon ? "data/maps/shipping/common/common.bin" : $"data/maps/shipping/{mapId}/{mapId}.bin";
                string targetWad = $"{mapId}.wad.client";

                _logService.LogDebug($"[GetBinFileSearchStrategy] Container-Aware (MAP): '{sourceWadName}' -> Linking to Master Map WAD '{targetWad}'");
                return new BinFileStrategy(binPath, targetWad, BinType.Map);
            }

            // 3. DETECCIÓN POR CONTENEDOR (Campeones Regionales / Skins)
            // Aquí aplicamos la lógica de prioridad: Nombre de archivo > Carpeta > Skin0
            if (wadParts.Length >= 4 || virtualPath.Contains("/characters/"))
            {
                string champName = wadPrefix.ToLower();

                // Si la ruta no coincide con el prefijo, lo sacamos de la ruta (caso de archivos sueltos en WADs globales)
                if (virtualPath.Contains("/characters/"))
                {
                    var pathParts = virtualPath.Split('/');
                    int charIndex = Array.IndexOf(pathParts, "characters");
                    if (pathParts.Length > charIndex + 1) champName = pathParts[charIndex + 1].ToLower();
                }

                string skinName = "skin0";
                string fileNameLower = (fileName ?? string.Empty).ToLower();

                // PRIORIDAD 1: Buscar patrón _skinXX_ en el nombre del archivo
                if (fileNameLower.Contains("_skin"))
                {
                    int skinIdx = fileNameLower.IndexOf("_skin");
                    string sub = fileNameLower.Substring(skinIdx + 5);
                    string idStr = "";
                    foreach (char c in sub)
                    {
                        if (char.IsDigit(c)) idStr += c;
                        else break;
                    }
                    if (!string.IsNullOrEmpty(idStr) && int.TryParse(idStr, out int id))
                    {
                        skinName = $"skin{id}";
                        _logService.LogDebug($"[GetBinFileSearchStrategy] Skin ID extracted from FILENAME: '{skinName}'");
                    }
                }
                // PRIORIDAD 2: Buscar en la ruta de carpetas
                else if (virtualPath.Contains("/skins/"))
                {
                    var pathParts = virtualPath.Split('/');
                    int skinsIndex = Array.IndexOf(pathParts, "skins");
                    if (pathParts.Length > skinsIndex + 1)
                    {
                        string skinFolder = pathParts[skinsIndex + 1].ToLower();
                        int id = (skinFolder == "base") ? 0 : -1;
                        if (id == -1)
                        {
                            string idStr = new string(skinFolder.Where(char.IsDigit).ToArray());
                            if (!string.IsNullOrEmpty(idStr)) int.TryParse(idStr, out id);
                        }

                        if (id != -1)
                        {
                            skinName = $"skin{id}";
                            _logService.LogDebug($"[GetBinFileSearchStrategy] Skin ID extracted from FOLDER: '{skinName}'");
                        }
                    }
                }

                _logService.LogDebug($"[GetBinFileSearchStrategy] CHAMP Strategy: '{champName}' -> Linking to '{skinName}.bin' in '{champName}.wad.client'");
                return new BinFileStrategy($"data/characters/{champName}/skins/{skinName}.bin", $"{champName}.wad.client", BinType.Champion);
            }

            // 4. ESTRATEGIAS POR RUTA (Companions)
            if (virtualPath.Contains("/companions/pets/"))
            {
                var pathParts = virtualPath.Split('/');
                int petsIndex = Array.IndexOf(pathParts, "pets");
                if (petsIndex != -1 && pathParts.Length > petsIndex + 2)
                {
                    string petName = pathParts[petsIndex + 1];
                    string themeName = pathParts[petsIndex + 2];
                    if (!string.IsNullOrEmpty(petName) && !string.IsNullOrEmpty(themeName))
                        return new BinFileStrategy($"data/characters/{petName}/themes/{themeName}/root.bin", "companions.wad.client", BinType.Companion);
                }
            }

            // 5. INFERENCIA FINAL (Fallback de emergencia)
            if ((fileName ?? string.Empty).Contains("_vo_", StringComparison.OrdinalIgnoreCase))
            {
                string baseName = GetBaseName(fileName);
                string[] baseParts = baseName.Split('_');
                if (baseParts.Length > 0)
                {
                    string champName = baseParts[0].ToLower();
                    string[] reserved = { "map", "common", "misc", "announcer", "global", "mode", "tutorial" };
                    if (!reserved.Contains(champName))
                    {
                        return new BinFileStrategy($"data/characters/{champName}/skins/skin0.bin", $"{champName}.wad.client", BinType.Champion);
                    }
                }
            }

            return null;
        }

        // Sibling detection for an audio bank path. Returns the metadata of the
        // _audio.bnk / _events.bnk / _audio.wpk companions that live alongside
        // the audio bank inside its WAD. Used by WadPackagingService and
        // DiffViewService to identify the bank family without touching the disk.
        public List<AudioDependencyInfo> GetAudioBankSiblings(string pathForStrategy, string sourceWadFile)
        {
            var siblings = new List<AudioDependencyInfo>();
            if (string.IsNullOrEmpty(pathForStrategy) || string.IsNullOrEmpty(sourceWadFile)) return siblings;

            string fileName = Path.GetFileName(pathForStrategy);
            List<(string siblingFileName, AudioDependencyType siblingType)> potential = new();

            if (fileName.EndsWith("_events.bnk", StringComparison.OrdinalIgnoreCase))
            {
                string basePart = fileName.Substring(0, fileName.Length - 11);
                potential.Add((basePart + "_audio.bnk", AudioDependencyType.AudioBnk));
                potential.Add((basePart + "_audio.wpk", AudioDependencyType.AudioWpk));
            }
            else if (fileName.EndsWith("_audio.bnk", StringComparison.OrdinalIgnoreCase))
            {
                string basePart = fileName.Substring(0, fileName.Length - 10);
                potential.Add((basePart + "_events.bnk", AudioDependencyType.EventsBnk));
                potential.Add((basePart + "_audio.wpk", AudioDependencyType.AudioWpk));
            }
            else if (fileName.EndsWith("_audio.wpk", StringComparison.OrdinalIgnoreCase))
            {
                string basePart = fileName.Substring(0, fileName.Length - 10);
                potential.Add((basePart + "_events.bnk", AudioDependencyType.EventsBnk));
                potential.Add((basePart + "_audio.bnk", AudioDependencyType.AudioBnk));
            }

            string siblingDir = Path.GetDirectoryName(pathForStrategy)?.Replace('\\', '/') ?? string.Empty;
            foreach (var (siblingFileName, siblingType) in potential)
            {
                string siblingVirtualPath = string.IsNullOrEmpty(siblingDir)
                    ? siblingFileName
                    : Path.Combine(siblingDir, siblingFileName).Replace('\\', '/');

                siblings.Add(new AudioDependencyInfo
                {
                    Path = siblingVirtualPath,
                    SourceWad = sourceWadFile,
                    PathHash = XxHash64Ext.Hash(siblingVirtualPath.ToLower()),
                    Type = siblingType
                });
            }

            return siblings;
        }

        // Resolves the metadata for every dependency of an audio-bank diff:
        // the .bin sibling (if any strategy matches) plus the audio bank
        // companions. Path hashes are pre-computed for convenience.
        public List<AudioDependencyInfo> ResolveAudioBankDependencies(SerializableChunkDiff audioBankDiff)
        {
            var deps = new List<AudioDependencyInfo>();
            string pathForStrategy = audioBankDiff.NewPath ?? audioBankDiff.OldPath;
            if (string.IsNullOrEmpty(pathForStrategy)) return deps;

            // 1. .bin sibling (5 strategies)
            var binStrategy = GetBinFileSearchStrategy(pathForStrategy, audioBankDiff.SourceWadFile);
            if (binStrategy != null)
            {
                string binVirtualPath = binStrategy.BinPath;
                string targetWadRelativePath = binStrategy.TargetWadName;
                string sourceWadRelativePath = audioBankDiff.SourceWadFile;
                string sourceWadDirectory = Path.GetDirectoryName(sourceWadRelativePath);
                string sourceWadFileName = Path.GetFileName(sourceWadRelativePath);

                if (string.Equals(sourceWadFileName, binStrategy.TargetWadName, StringComparison.OrdinalIgnoreCase))
                {
                    targetWadRelativePath = sourceWadRelativePath;
                }
                else if (!string.IsNullOrEmpty(sourceWadDirectory))
                {
                    targetWadRelativePath = Path.Combine(sourceWadDirectory, binStrategy.TargetWadName).Replace('\\', '/');
                }

                deps.Add(new AudioDependencyInfo
                {
                    Path = binVirtualPath,
                    SourceWad = targetWadRelativePath,
                    PathHash = XxHash64Ext.Hash(binVirtualPath.ToLower()),
                    Type = AudioDependencyType.Bin
                });
            }

            // 2. Sibling audio banks
            deps.AddRange(GetAudioBankSiblings(pathForStrategy, audioBankDiff.SourceWadFile));
            return deps;
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
                string targetWadVirtualPath = ResolveTargetWadPath(clickedNode, basePath, strategy.TargetWadName);
                if (File.Exists(targetWadVirtualPath))
                {
                    var wadContent = await _wadNodeLoaderService.LoadWadContentAsync(targetWadVirtualPath);
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

            if (rootNodes == null) return (null, baseName, strategy.Type);

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
                
                // BRIDGE FIX: Try to find the target WAD node anywhere in the tree (not just top-level)
                var targetWadNode = FindNodeByName(rootNodes, strategy.TargetWadName);
                
                if (targetWadNode != null)
                {
                    _logService.LogDebug($"[FindAssociatedBinFileAsync] [LIVE] Target WAD '{strategy.TargetWadName}' found in tree. Searching for BIN '{strategy.BinPath}'...");
                    var binNode = await _wadSearchBoxService.PerformSearchAsync(strategy.BinPath, new ObservableRangeCollection<FileSystemNodeModel> { targetWadNode }, loader);
                    if (binNode != null) return (binNode, baseName, strategy.Type);
                }
                else
                {
                    _logService.LogWarning($"[FindAssociatedBinFileAsync] [LIVE] Target WAD '{strategy.TargetWadName}' NOT found in tree. Scanning all loaded WADs as fallback...");
                    // FALLBACK: Deep search in all root nodes (WADs)
                    foreach(var root in rootNodes)
                    {
                        if (root.Type == NodeType.WadFile || root.Children.Any(c => c.Type == NodeType.WadFile))
                        {
                           var match = FindNodeByName(new[] { root }, strategy.TargetWadName);
                           if (match != null)
                           {
                               _logService.LogDebug($"[FindAssociatedBinFileAsync] [LIVE] Fallback: Found target WAD '{strategy.TargetWadName}' in sub-hierarchy.");
                               var binNode = await _wadSearchBoxService.PerformSearchAsync(strategy.BinPath, new ObservableRangeCollection<FileSystemNodeModel> { match }, loader);
                               if (binNode != null) return (binNode, baseName, strategy.Type);
                           }
                        }
                    }
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

        private FileSystemNodeModel FindNodeByPath(IEnumerable<FileSystemNodeModel> nodes, string virtualPath)
        {
            foreach (var node in nodes)
            {
                if (node.VirtualPath != null && node.VirtualPath.Equals(virtualPath, StringComparison.OrdinalIgnoreCase)) return node;
                if (node.Children != null && node.Children.Any())
                {
                    var found = FindNodeByPath(node.Children, virtualPath);
                    if (found != null) return found;
                }
            }
            return null;
        }
    }
}
