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
using AssetsManager.Utils;

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
        private readonly AudioBankService _audioBankService;

        public AudioBankLinkerService(
            WadContentProvider wadContentProvider,
            WadSearchBoxService wadSearchBoxService,
            LogService logService,
            TreeUIManager treeUIManager,
            HashResolverService hashResolverService,
            WadNodeLoaderService wadNodeLoaderService,
            AudioBankService audioBankService)
        {
            _wadContentProvider = wadContentProvider;
            _wadSearchBoxService = wadSearchBoxService;
            _logService = logService;
            _treeUIManager = treeUIManager;
            _hashResolverService = hashResolverService;
            _wadNodeLoaderService = wadNodeLoaderService;
            _audioBankService = audioBankService;
        }

        public async Task<LinkedAudioBank> LinkAudioBankAsync(FileSystemNodeModel clickedNode, ObservableRangeCollection<FileSystemNodeModel> rootNodes, string currentRootPath)
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

        private async Task<(FileSystemNodeModel WpkNode, FileSystemNodeModel AudioBnkNode, FileSystemNodeModel EventsBnkNode)> FindSiblingFilesFromWadsInternalAsync(FileSystemNodeModel clickedNode, string wadPath, string baseName)
        {
            if (!File.Exists(wadPath)) return (null, null, null);

            var wadContent = await _wadNodeLoaderService.LoadWadContentAsync(wadPath);

            FileSystemNodeModel wpkNode = FindNodeByName(wadContent, baseName + "_audio.wpk");
            FileSystemNodeModel audioBnkNode = FindNodeByName(wadContent, baseName + "_audio.bnk");
            FileSystemNodeModel eventsBnkNode = FindNodeByName(wadContent, baseName + "_events.bnk");

            return (wpkNode, audioBnkNode, eventsBnkNode);
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
                string baseName = PathUtils.StripBankSuffix(fileName);
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
            var siblings = GetAudioBankSiblings(pathForStrategy, audioBankDiff.SourceWadFile);
            deps.AddRange(siblings);
            return deps;
        }

        private (FileSystemNodeModel WpkNode, FileSystemNodeModel AudioBnkNode, FileSystemNodeModel EventsBnkNode) FindSiblingFilesByName(FileSystemNodeModel clickedNode, ObservableRangeCollection<FileSystemNodeModel> rootNodes)
        {
            bool isBackupMode = clickedNode.ChunkDiff != null;
            string baseName = PathUtils.StripBankSuffix(clickedNode.Name);
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

        private async Task<(FileSystemNodeModel BinNode, string baseName, BinType Type)> FindAssociatedBinFileAsync(FileSystemNodeModel clickedNode, ObservableRangeCollection<FileSystemNodeModel> rootNodes, string currentRootPath)
        {
            string baseName = PathUtils.StripBankSuffix(clickedNode.Name);
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

        // LIVE mode entry point for direct (non-archived) comparison: opens the
        // local source/target WADs from the user-supplied PBE paths, resolves
        // the audio bank siblings (audio.bnk / .wpk / .bin) and feeds the
        // resulting bytes to AudioBankService.ParseAudioBank for both sides.
        // Returns the parsed audio event trees ready for JSON serialization
        // and side-by-side comparison. Throws on unrecoverable I/O errors so
        // the caller can fall back to the raw JSON view.
        public async Task<(List<AudioEventNode> OldNodes, List<AudioEventNode> NewNodes)> ResolveLiveAudioBankDiffAsync(
            SerializableChunkDiff diff, string oldPbePath, string newPbePath,
            byte[] oldClickedBnk, byte[] newClickedBnk)
        {
            string sourceWadRelativePath = diff.SourceWadFile;
            string fileName = Path.GetFileName(diff.NewPath ?? diff.OldPath);
            string baseName = PathUtils.StripBankSuffix(fileName);
            bool clickedIsEventsBnk = fileName.Contains("_events", StringComparison.OrdinalIgnoreCase);

            string pathForStrategy = diff.NewPath ?? diff.OldPath;
            var binStrategy = GetBinFileSearchStrategy(pathForStrategy, sourceWadRelativePath);

            byte[] oldEventsBnk = null, oldAudioBnk = null, oldWpk = null, oldBin = null;
            byte[] newEventsBnk = null, newAudioBnk = null, newWpk = null, newBin = null;

            if (!string.IsNullOrEmpty(oldPbePath))
            {
                (oldEventsBnk, oldAudioBnk, oldWpk, oldBin) = await ReadAudioBankSiblingsFromLocalWadAsync(
                    oldPbePath, sourceWadRelativePath, baseName, binStrategy);
            }
            if (!string.IsNullOrEmpty(newPbePath))
            {
                (newEventsBnk, newAudioBnk, newWpk, newBin) = await ReadAudioBankSiblingsFromLocalWadAsync(
                    newPbePath, sourceWadRelativePath, baseName, binStrategy);
            }

            if (clickedIsEventsBnk)
            {
                oldEventsBnk = oldEventsBnk ?? oldClickedBnk;
                newEventsBnk = newEventsBnk ?? newClickedBnk;
            }
            else
            {
                oldAudioBnk = oldAudioBnk ?? oldClickedBnk;
                newAudioBnk = newAudioBnk ?? newClickedBnk;
            }

            if (oldEventsBnk == null && oldAudioBnk == null && newEventsBnk == null && newAudioBnk == null)
            {
                return (null, null);
            }

            var oldNodes = _audioBankService.ParseAudioBank(
                wpkData: oldWpk,
                audioBnkData: oldAudioBnk,
                eventsData: oldEventsBnk,
                binData: oldBin,
                baseName: baseName,
                binType: binStrategy?.Type ?? BinType.Unknown);

            var newNodes = _audioBankService.ParseAudioBank(
                wpkData: newWpk,
                audioBnkData: newAudioBnk,
                eventsData: newEventsBnk,
                binData: newBin,
                baseName: baseName,
                binType: binStrategy?.Type ?? BinType.Unknown);

            return (oldNodes, newNodes);
        }

        // ARCHIVED mode entry point for comparisons saved in wad_chunks/:
        // resolves audio bank siblings (.bnk / .wpk / .bin) via the comparison's
        // dependency index, reads the bytes from the archived .chunk files via
        // WadContentProvider, and returns the parsed audio event trees.
        public async Task<(List<AudioEventNode> OldNodes, List<AudioEventNode> NewNodes)> ResolveArchivedAudioBankDiffAsync(
            SerializableChunkDiff diff, string backupRoot, byte[] oldClickedBnk, byte[] newClickedBnk)
        {
            if (string.IsNullOrEmpty(backupRoot)) return (null, null);

            List<AudioDependencyInfo> resolvedDeps = ResolveAudioBankDependencies(diff);
            Dictionary<string, AssociatedDependency> depByPath = (diff.Dependencies ?? new List<AssociatedDependency>())
                .GroupBy(d => d.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            string fileName = Path.GetFileName(diff.NewPath ?? diff.OldPath);
            string baseName = PathUtils.StripBankSuffix(fileName);
            bool clickedIsEventsBnk = fileName.Contains("_events", StringComparison.OrdinalIgnoreCase);

            var binStrategy = GetBinFileSearchStrategy(diff.NewPath ?? diff.OldPath, diff.SourceWadFile);
            var binType = binStrategy?.Type ?? BinType.Unknown;

            byte[] oldEventsBnk = null, oldAudioBnk = null, oldWpk = null, oldBin = null;
            byte[] newEventsBnk = null, newAudioBnk = null, newWpk = null, newBin = null;

            foreach (var dep in resolvedDeps)
            {
                if (!depByPath.TryGetValue(dep.Path, out var assoc) || assoc == null) continue;

                byte[] oldBytes = await _wadContentProvider.GetBackupChunkBytesAsync(backupRoot, dep.SourceWad, assoc.OldPathHash, assoc.CompressionType, isOld: true);
                byte[] newBytes = await _wadContentProvider.GetBackupChunkBytesAsync(backupRoot, dep.SourceWad, assoc.NewPathHash, assoc.CompressionType, isOld: false);

                switch (dep.Type)
                {
                    case AudioDependencyType.EventsBnk: oldEventsBnk = oldBytes; newEventsBnk = newBytes; break;
                    case AudioDependencyType.AudioBnk: oldAudioBnk = oldBytes; newAudioBnk = newBytes; break;
                    case AudioDependencyType.AudioWpk: oldWpk = oldBytes; newWpk = newBytes; break;
                    case AudioDependencyType.Bin: oldBin = oldBytes; newBin = newBytes; break;
                }
            }

            if (clickedIsEventsBnk)
            {
                oldEventsBnk = oldEventsBnk ?? oldClickedBnk;
                newEventsBnk = newEventsBnk ?? newClickedBnk;
            }
            else
            {
                oldAudioBnk = oldAudioBnk ?? oldClickedBnk;
                newAudioBnk = newAudioBnk ?? newClickedBnk;
            }

            if (oldEventsBnk == null && oldAudioBnk == null && newEventsBnk == null && newAudioBnk == null)
            {
                return (null, null);
            }

            var oldNodes = _audioBankService.ParseAudioBank(
                wpkData: oldWpk,
                audioBnkData: oldAudioBnk,
                eventsData: oldEventsBnk,
                binData: oldBin,
                baseName: baseName,
                binType: binType);

            var newNodes = _audioBankService.ParseAudioBank(
                wpkData: newWpk,
                audioBnkData: newAudioBnk,
                eventsData: newEventsBnk,
                binData: newBin,
                baseName: baseName,
                binType: binType);

            return (oldNodes, newNodes);
        }

        // Opens `basePath/sourceWadRelativePath`, resolves the audio bank
        // siblings (events.bnk / audio.bnk / audio.wpk) by name, and the .bin
        // from the target WAD identified by the 5-strategy resolver. Returns
        // nulls when a sibling is absent (callers fall back to the clicked
        // file's bytes).
        private async Task<(byte[] eventsBnk, byte[] audioBnk, byte[] wpk, byte[] bin)> ReadAudioBankSiblingsFromLocalWadAsync(
            string basePath, string sourceWadRelativePath, string baseName, BinFileStrategy binStrategy)
        {
            byte[] eventsBnk = null, audioBnk = null, wpk = null, bin = null;

            string sourceWadFullPath = Path.Combine(basePath, sourceWadRelativePath);
            if (!File.Exists(sourceWadFullPath))
            {
                return (eventsBnk, audioBnk, wpk, bin);
            }

            try
            {
                var wadContent = await _wadNodeLoaderService.LoadWadContentAsync(sourceWadFullPath);
                var wpkNode = FindNodeByName(wadContent, baseName + "_audio.wpk");
                var audioBnkNode = FindNodeByName(wadContent, baseName + "_audio.bnk");
                var eventsBnkNode = FindNodeByName(wadContent, baseName + "_events.bnk");

                if (wpkNode != null) wpk = await _wadContentProvider.GetVirtualFileBytesAsync(wpkNode);
                if (audioBnkNode != null) audioBnk = await _wadContentProvider.GetVirtualFileBytesAsync(audioBnkNode);
                if (eventsBnkNode != null) eventsBnk = await _wadContentProvider.GetVirtualFileBytesAsync(eventsBnkNode);
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed reading source WAD: '{sourceWadFullPath}'");
            }

            if (binStrategy != null)
            {
                string sourceWadDirectory = Path.GetDirectoryName(sourceWadRelativePath);
                string sourceWadFileName = Path.GetFileName(sourceWadRelativePath);
                string targetWadRelativePath;
                if (string.Equals(sourceWadFileName, binStrategy.TargetWadName, StringComparison.OrdinalIgnoreCase))
                {
                    targetWadRelativePath = sourceWadRelativePath;
                }
                else
                {
                    targetWadRelativePath = string.IsNullOrEmpty(sourceWadDirectory)
                        ? binStrategy.TargetWadName
                        : Path.Combine(sourceWadDirectory, binStrategy.TargetWadName).Replace('\\', '/');
                }

                string targetWadFullPath = Path.Combine(basePath, targetWadRelativePath);
                if (File.Exists(targetWadFullPath))
                {
                    try
                    {
                        var targetWadContent = await _wadNodeLoaderService.LoadWadContentAsync(targetWadFullPath);
                        var binNode = FindNodeByPath(targetWadContent, binStrategy.BinPath);
                        if (binNode != null)
                        {
                            bin = await _wadContentProvider.GetVirtualFileBytesAsync(binNode);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logService.LogError(ex, $"Failed reading target WAD for .bin: '{targetWadFullPath}'");
                    }
                }
            }

            return (eventsBnk, audioBnk, wpk, bin);
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
