using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models.Audio;

namespace AssetsManager.Services.Comparator
{
    public class WadPackagingService
    {
        private readonly LogService _logService;
        private readonly HashResolverService _hashResolverService;

        public WadPackagingService(LogService logService, HashResolverService hashResolverService)
        {
            _logService = logService;
            _hashResolverService = hashResolverService;
        }

        private record BinFileStrategy(string BinPath, string TargetWadName, BinType Type);

        private BinFileStrategy GetBinFileSearchStrategy(string fullPath, string sourceWadPath)
        {
            _logService.LogDebug($"[GetBinFileSearchStrategy] Searching for BIN strategy. FullPath: '{fullPath}', SourceWad: '{sourceWadPath}'");
            string sourceWadName = Path.GetFileName(sourceWadPath);

            // Strategy 0: Infer from companion path structure
            _logService.LogDebug("[GetBinFileSearchStrategy] Attempting Strategy 0: Infer from companion path structure.");
            if (fullPath.Contains("/companions/pets/"))
            {
                var pathParts = fullPath.Split('/');
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
            if (fullPath.Contains("/characters/") && fullPath.Contains("/skins/"))
            {
                var pathParts = fullPath.Split('/');
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

            // Strategy 3: Infer for locale VO files
            _logService.LogDebug("[GetBinFileSearchStrategy] Attempting Strategy 3: Infer for locale VO files.");
            if (fullPath.Contains("_vo_", StringComparison.OrdinalIgnoreCase) &&
                sourceWadName.StartsWith("Common.", StringComparison.OrdinalIgnoreCase) &&
                sourceWadName.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase) &&
                !sourceWadName.Equals("Common.wad.client", StringComparison.OrdinalIgnoreCase))
            {
                string binPath = "data/maps/shipping/common/common.bin";
                string targetWadName = "Common.wad.client";
                var strategy = new BinFileStrategy(binPath, targetWadName, BinType.Map);
                _logService.LogDebug($"[GetBinFileSearchStrategy] Strategy 3 successful. Found: {strategy}");
                return strategy;
            }
            _logService.LogDebug("[GetBinFileSearchStrategy] Strategy 3 failed or was not applicable.");

            // Strategy 4 (Unified Map/Common): Infer from WAD file name for maps.
            _logService.LogDebug("[GetBinFileSearchStrategy] Attempting Strategy 4: Infer from WAD file name for Maps/Common.");
            if (wadNameParts.Length > 0 && (sourceWadName.StartsWith("Map", StringComparison.OrdinalIgnoreCase) || sourceWadName.StartsWith("Common", StringComparison.OrdinalIgnoreCase)))
            {
                string mapName = wadNameParts[0];
                if (!string.IsNullOrEmpty(mapName))
                {
                    string binPath = $"data/maps/shipping/{mapName.ToLower()}/{mapName.ToLower()}.bin";
                    string targetWadName = $"{mapName.ToLower()}.wad.client";
                    var strategy = new BinFileStrategy(binPath, targetWadName, BinType.Map);
                    _logService.LogDebug($"[GetBinFileSearchStrategy] Strategy 4 successful. Found: {strategy}");
                    return strategy;
                }
            }
            _logService.LogDebug("[GetBinFileSearchStrategy] Strategy 4 failed or was not applicable.");

            _logService.LogWarning($"[GetBinFileSearchStrategy] No BIN file strategy found for '{fullPath}'.");
            return null;
        }

        public async Task<List<SerializableChunkDiff>> CreateLeanWadPackageAsync(IEnumerable<SerializableChunkDiff> diffs, string oldPbePath, string newPbePath, string targetOldWadsPath, string targetNewWadsPath)
        {
            var finalDiffs = diffs.ToList();
            var audioBankDiffs = finalDiffs.Where(d => (d.NewPath ?? d.OldPath).EndsWith("_events.bnk", StringComparison.OrdinalIgnoreCase)).ToList();
            _logService.LogDebug($"[CreateLeanWadPackageAsync] Found {audioBankDiffs.Count} audio bank diffs to process.");

            foreach (var audioBankDiff in audioBankDiffs)
            {
                audioBankDiff.Dependencies = new List<AssociatedDependency>();
                string pathForStrategy = audioBankDiff.NewPath ?? audioBankDiff.OldPath;
                _logService.LogDebug($"[CreateLeanWadPackageAsync] Processing audio bank: '{pathForStrategy}'");

                // --- 1. Handle .bin dependency ---
                _logService.LogDebug($"[CreateLeanWadPackageAsync] Searching for .bin dependency for '{pathForStrategy}'...");
                var binStrategy = GetBinFileSearchStrategy(pathForStrategy, audioBankDiff.SourceWadFile);
                if (binStrategy != null)
                {
                    _logService.LogDebug($"[CreateLeanWadPackageAsync] Found bin strategy: {binStrategy}. Resolving target WAD path...");
                    
                    // Resolve the correct relative path for the target WAD
                    string targetWadRelativePath = binStrategy.TargetWadName;
                    string sourceWadRelativePath = audioBankDiff.SourceWadFile;
                    string sourceWadDirectory = Path.GetDirectoryName(sourceWadRelativePath);
                    string sourceWadFileName = Path.GetFileName(sourceWadRelativePath);

                    if (string.Equals(sourceWadFileName, binStrategy.TargetWadName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetWadRelativePath = sourceWadRelativePath;
                    }
                    else
                    {
                        string potentialPath = Path.Combine(sourceWadDirectory, binStrategy.TargetWadName).Replace('\\', '/');
                        if (File.Exists(Path.Combine(newPbePath, potentialPath)) || File.Exists(Path.Combine(oldPbePath, potentialPath)))
                        {
                            targetWadRelativePath = potentialPath;
                        }
                    }

                    _logService.LogDebug($"[CreateLeanWadPackageAsync] Resolved target WAD path: '{targetWadRelativePath}'. Creating dependency...");

                    var diffForBinDependency = finalDiffs.FirstOrDefault(d => d.NewPathHash == XxHash64Ext.Hash(binStrategy.BinPath.ToLower()) || d.OldPathHash == XxHash64Ext.Hash(binStrategy.BinPath.ToLower()));
                    var binDependency = await CreateDependencyAsync(binStrategy.BinPath, XxHash64Ext.Hash(binStrategy.BinPath.ToLower()), targetWadRelativePath, oldPbePath, newPbePath, targetWadRelativePath, diffForBinDependency);
                    if (binDependency != null)
                    {
                        audioBankDiff.Dependencies.Add(binDependency);
                        _logService.LogDebug($"[CreateLeanWadPackageAsync] Successfully created and added .bin dependency for '{binStrategy.BinPath}'.");

                        if (diffForBinDependency != null)
                        {
                            finalDiffs.Remove(diffForBinDependency);
                            _logService.LogDebug($"[CreateLeanWadPackageAsync] Removed top-level diff (now embedded as dependency): '{binStrategy.BinPath}'.");
                        }
                    }
                    else
                    {
                        _logService.LogWarning($"[CreateLeanWadPackageAsync] Failed to create .bin dependency for '{binStrategy.BinPath}'. It may not exist in the target WAD '{targetWadRelativePath}'.");
                    }
                }
                else
                {
                    _logService.LogWarning($"[CreateLeanWadPackageAsync] No .bin strategy found for '{pathForStrategy}'.");
                }

                // --- 2. Handle sibling audio dependency ---
                _logService.LogDebug($"[CreateLeanWadPackageAsync] Searching for sibling audio dependencies for '{pathForStrategy}'...");
                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(pathForStrategy);
                List<string> potentialSiblingsList = new List<string>();

                if (fileNameWithoutExtension.EndsWith("_vo_events", StringComparison.OrdinalIgnoreCase))
                {
                    string basePart = fileNameWithoutExtension.Replace("_vo_events", ""); // e.g., "yunara_base"
                    potentialSiblingsList.Add(basePart + "_vo_audio.wpk");
                }
                else if (fileNameWithoutExtension.EndsWith("_sfx_events", StringComparison.OrdinalIgnoreCase))
                {
                    string basePart = fileNameWithoutExtension.Replace("_sfx_events", ""); // e.g., "yunara_base"
                    potentialSiblingsList.Add(basePart + "_sfx_audio.bnk");
                }
                else if (fileNameWithoutExtension.EndsWith("_events", StringComparison.OrdinalIgnoreCase))
                {
                    // Generic _events.bnk
                    string basePart = fileNameWithoutExtension.Replace("_events", "");
                    potentialSiblingsList.Add(basePart + "_audio.bnk");
                    potentialSiblingsList.Add(basePart + "_audio.wpk");
                }
                _logService.LogDebug($"[CreateLeanWadPackageAsync] Potential siblings identified: {string.Join(", ", potentialSiblingsList)}");

                foreach (var siblingFileName in potentialSiblingsList)
                {
                    string siblingFullPath = Path.Combine(Path.GetDirectoryName(pathForStrategy), siblingFileName).Replace('\\', '/');
                    _logService.LogDebug($"[CreateLeanWadPackageAsync] Attempting to create dependency for sibling: '{siblingFullPath}'");
                    var diffForSiblingDependency = finalDiffs.FirstOrDefault(d => (d.NewPath ?? d.OldPath).Equals(siblingFullPath, StringComparison.OrdinalIgnoreCase));
                    var siblingDependency = await CreateDependencyAsync(siblingFullPath, XxHash64Ext.Hash(siblingFullPath.ToLower()), audioBankDiff.SourceWadFile, oldPbePath, newPbePath, audioBankDiff.SourceWadFile, diffForSiblingDependency);
                    if (siblingDependency != null)
                    {
                        audioBankDiff.Dependencies.Add(siblingDependency);
                        _logService.LogDebug($"[CreateLeanWadPackageAsync] Successfully created and added sibling dependency for '{siblingFullPath}'.");

                        // Always remove the top-level diff if it was explicitly a diff.
                        // The dependency now carries its own Type and WasTopLevelDiff flag.
                        if (diffForSiblingDependency != null)
                        {
                            finalDiffs.Remove(diffForSiblingDependency);
                            _logService.LogDebug($"[CreateLeanWadPackageAsync] Removed top-level diff (now embedded as dependency): '{siblingFullPath}'.");
                        }

                        break;
                    }
                    else
                    {
                        _logService.LogWarning($"[CreateLeanWadPackageAsync] Failed to create sibling dependency for '{siblingFullPath}'.");
                    }
                }
            }

            var allChunks = new List<SerializableChunkDiff>(finalDiffs);
            // We also need to package the chunks of the dependencies
            foreach (var audioBankDiff in audioBankDiffs)
            {
                if (audioBankDiff.Dependencies != null)
                {
                    _logService.LogDebug($"[CreateLeanWadPackageAsync] Packaging {audioBankDiff.Dependencies.Count} dependencies for '{audioBankDiff.NewPath ?? audioBankDiff.OldPath}'.");
                    foreach (var dep in audioBankDiff.Dependencies)
                    {
                        allChunks.Add(new SerializableChunkDiff { OldPathHash = dep.OldPathHash, NewPathHash = dep.NewPathHash, SourceWadFile = dep.SourceWad, Type = ChunkDiffType.Modified });
                    }
                }
            }

            var diffsByWad = allChunks.GroupBy(d => d.SourceWadFile);

            foreach (var wadGroup in diffsByWad)
            {
                var wadFileRelativePath = wadGroup.Key;
                _logService.LogDebug($"Processing {wadFileRelativePath} for chunk packaging...");

                string sourceOldWadPath = Path.Combine(oldPbePath, wadFileRelativePath);
                if (File.Exists(sourceOldWadPath))
                {
                    var oldChunksToSave = wadGroup
                        .Where(d => d.Type == ChunkDiffType.Modified || d.Type == ChunkDiffType.Renamed || d.Type == ChunkDiffType.Removed)
                        .Select(d => d.OldPathHash)
                        .Where(h => h != 0)
                        .Distinct();
                    if (oldChunksToSave.Any())
                        await SaveChunksFromWadAsync(sourceOldWadPath, targetOldWadsPath, oldChunksToSave);
                }

                string sourceNewWadPath = Path.Combine(newPbePath, wadFileRelativePath);
                if (File.Exists(sourceNewWadPath))
                {
                    var newChunksToSave = wadGroup
                        .Where(d => d.Type == ChunkDiffType.Modified || d.Type == ChunkDiffType.Renamed || d.Type == ChunkDiffType.New)
                        .Select(d => d.NewPathHash)
                        .Where(h => h != 0)
                        .Distinct();
                    if (newChunksToSave.Any())
                        await SaveChunksFromWadAsync(sourceNewWadPath, targetNewWadsPath, newChunksToSave);
                }
            }

            return finalDiffs;
        }

        private async Task<AssociatedDependency> CreateDependencyAsync(string filePath, ulong fileHash, string wadRelativePath, string oldPbePath, string newPbePath, string sourceWad, SerializableChunkDiff originalDiff)
        {
            _logService.LogDebug($"[CreateDependencyAsync] Attempting to create dependency for file '{filePath}' (Hash: {fileHash:X16}) in WAD '{wadRelativePath}'.");
            return await Task.Run(() =>
            {
                string wadFullPath = Path.Combine(newPbePath, wadRelativePath);
                if (!File.Exists(wadFullPath))
                {
                    _logService.LogDebug($"[CreateDependencyAsync] WAD not found at new path, trying old path: '{wadFullPath}'");
                    wadFullPath = Path.Combine(oldPbePath, wadRelativePath);
                }

                if (File.Exists(wadFullPath))
                {
                    _logService.LogDebug($"[CreateDependencyAsync] WAD found at '{wadFullPath}'. Reading...");
                    using var wad = new WadFile(wadFullPath);
                    if (wad.Chunks.TryGetValue(fileHash, out var chunk))
                    {
                        _logService.LogDebug($"[CreateDependencyAsync] Chunk found for hash {fileHash:X16}. Creating dependency object.");
                        return new AssociatedDependency
                        {
                            Path = filePath,
                            SourceWad = sourceWad,
                            OldPathHash = fileHash,
                            NewPathHash = fileHash,
                            CompressionType = chunk.Compression,
                            Type = originalDiff?.Type ?? ChunkDiffType.Dependency, // Assign Dependency if not a top-level diff
                            WasTopLevelDiff = true // Always true for dependencies so they are shown
                        };
                    }
                    else
                    {
                        _logService.LogWarning($"[CreateDependencyAsync] Chunk NOT found for hash {fileHash:X16} in WAD '{wadFullPath}'.");
                    }
                }
                else
                {
                    _logService.LogWarning($"[CreateDependencyAsync] WAD file not found at either new or old paths: '{wadRelativePath}'.");
                }
                return null;
            });
        }

        private async Task SaveChunksFromWadAsync(string sourceWadPath, string targetChunkPath, IEnumerable<ulong> chunkHashes)
        {
            try
            {
                using var sourceWad = new WadFile(sourceWadPath);
                
                // Get valid chunks and ORDER BY OFFSET for high-performance sequential reading
                var chunksToProcess = chunkHashes
                    .Select(h => sourceWad.Chunks.TryGetValue(h, out var c) ? c : (WadChunk?)null)
                    .Where(c => c.HasValue)
                    .Select(c => c.Value)
                    .OrderBy(c => c.DataOffset)
                    .ToList();

                if (chunksToProcess.Count == 0) return;

                Directory.CreateDirectory(targetChunkPath);
                
                // Open the stream ONCE for the entire WAD file processing
                await using var fs = new FileStream(sourceWadPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: true);

                foreach (var chunk in chunksToProcess)
                {
                    fs.Seek(chunk.DataOffset, SeekOrigin.Begin);
                    byte[] rawChunkData = new byte[chunk.CompressedSize];
                    await fs.ReadExactlyAsync(rawChunkData, 0, rawChunkData.Length);

                    string chunkFileName = $"{chunk.PathHash:X16}.chunk";
                    string destChunkPath = Path.Combine(targetChunkPath, chunkFileName);

                    await File.WriteAllBytesAsync(destChunkPath, rawChunkData);
                }
            }
            catch (System.Exception ex)
            {
                _logService.LogError(ex, $"Failed to save chunks from {sourceWadPath}");
            }
        }
    }
}
