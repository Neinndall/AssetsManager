using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Services.Audio;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;

namespace AssetsManager.Services.Comparator
{
    public class WadPackagingService
    {
        private readonly LogService _logService;
        private readonly HashResolverService _hashResolverService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly AudioBankLinkerService _audioBankLinkerService;

        public WadPackagingService(LogService logService, HashResolverService hashResolverService, DirectoriesCreator directoriesCreator, AudioBankLinkerService audioBankLinkerService)
        {
            _logService = logService;
            _hashResolverService = hashResolverService;
            _directoriesCreator = directoriesCreator;
            _audioBankLinkerService = audioBankLinkerService;
        }

        public async Task<List<SerializableChunkDiff>> CreateLeanWadPackageAsync(IEnumerable<SerializableChunkDiff> diffs, string oldPbePath, string newPbePath, string targetOldWadsPath, string targetNewWadsPath)
        {
            var finalDiffs = diffs.ToList();
            var audioBankDiffs = finalDiffs.Where(d => 
                (d.NewPath ?? d.OldPath).EndsWith("_events.bnk", StringComparison.OrdinalIgnoreCase) ||
                (d.NewPath ?? d.OldPath).EndsWith("_audio.bnk", StringComparison.OrdinalIgnoreCase) ||
                (d.NewPath ?? d.OldPath).EndsWith("_audio.wpk", StringComparison.OrdinalIgnoreCase)
            ).ToList();
            _logService.LogDebug($"[CreateLeanWadPackageAsync] Found {audioBankDiffs.Count} audio bank diffs to process.");

            foreach (var audioBankDiff in audioBankDiffs)
            {
                audioBankDiff.Dependencies = new List<AssociatedDependency>();
                string pathForStrategy = audioBankDiff.NewPath ?? audioBankDiff.OldPath;
                _logService.LogDebug($"[CreateLeanWadPackageAsync] Processing audio bank: '{pathForStrategy}'");

                // --- 1. Handle .bin dependency ---
                _logService.LogDebug($"[CreateLeanWadPackageAsync] Searching for .bin dependency for '{pathForStrategy}'...");
                var binStrategy = _audioBankLinkerService.GetBinFileSearchStrategy(pathForStrategy, audioBankDiff.SourceWadFile);
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
                var potentialSiblingsList = _audioBankLinkerService.GetAudioBankSiblings(pathForStrategy, audioBankDiff.SourceWadFile);
                _logService.LogDebug($"[CreateLeanWadPackageAsync] Potential siblings identified: {string.Join(", ", potentialSiblingsList.Select(s => s.Path))}");

                foreach (var sibling in potentialSiblingsList)
                {
                    string siblingVirtualPath = sibling.Path;
                    _logService.LogDebug($"[CreateLeanWadPackageAsync] Attempting to create dependency for sibling: '{siblingVirtualPath}'");
                    var diffForSiblingDependency = finalDiffs.FirstOrDefault(d => (d.NewPath ?? d.OldPath).Equals(siblingVirtualPath, StringComparison.OrdinalIgnoreCase));
                    var siblingDependency = await CreateDependencyAsync(siblingVirtualPath, sibling.PathHash, audioBankDiff.SourceWadFile, oldPbePath, newPbePath, audioBankDiff.SourceWadFile, diffForSiblingDependency);
                    if (siblingDependency != null)
                    {
                        audioBankDiff.Dependencies.Add(siblingDependency);
                        _logService.LogDebug($"[CreateLeanWadPackageAsync] Successfully created and added sibling dependency for '{siblingVirtualPath}'.");
                    }
                    else
                    {
                        _logService.LogDebug($"[CreateLeanWadPackageAsync] Sibling dependency not found for '{siblingVirtualPath}'. This is normal if the bank doesn't use this specific container type.");
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
                        allChunks.Add(new SerializableChunkDiff 
                        { 
                            OldPath = dep.Path,
                            NewPath = dep.Path,
                            OldPathHash = dep.OldPathHash, 
                            NewPathHash = dep.NewPathHash, 
                            SourceWadFile = dep.SourceWad, 
                            Type = ChunkDiffType.Modified 
                        });
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
                        .ToList();
                    if (oldChunksToSave.Any())
                        await SaveChunksFromWadAsync(sourceOldWadPath, targetOldWadsPath, oldChunksToSave, wadFileRelativePath, true);
                }

                string sourceNewWadPath = Path.Combine(newPbePath, wadFileRelativePath);
                if (File.Exists(sourceNewWadPath))
                {
                    var newChunksToSave = wadGroup
                        .Where(d => d.Type == ChunkDiffType.Modified || d.Type == ChunkDiffType.Renamed || d.Type == ChunkDiffType.New)
                        .ToList();
                    if (newChunksToSave.Any())
                        await SaveChunksFromWadAsync(sourceNewWadPath, targetNewWadsPath, newChunksToSave, wadFileRelativePath, false);
                }
            }

            return finalDiffs;
        }

        public async Task<List<SerializableChunkDiff>> SaveBackupAsync(List<SerializableChunkDiff> diffs, string oldPbePath, string newPbePath, string destinationPath, string version = null)
        {
            // Use the centralized directory creator to prepare the structure
            _directoriesCreator.PrepareComparisonDirectory(destinationPath);

            string wadChunksOldDir = Path.Combine(destinationPath, "wad_chunks", "old");
            string wadChunksNewDir = Path.Combine(destinationPath, "wad_chunks", "new");
            string jsonFilePath = Path.Combine(destinationPath, "wadcomparison.json");

            _logService.LogDebug($"[WadPackagingService] Saving full backup to {destinationPath}");

            var leanDiffs = await CreateLeanWadPackageAsync(diffs, oldPbePath, newPbePath, wadChunksOldDir, wadChunksNewDir);

            var comparisonData = new WadComparisonData
            {
                OldLolPath = oldPbePath,
                NewLolPath = newPbePath,
                Version = version,
                Diffs = leanDiffs
            };

            string json = System.Text.Json.JsonSerializer.Serialize(comparisonData, new System.Text.Json.JsonSerializerOptions 
            { 
                WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });
            await File.WriteAllTextAsync(jsonFilePath, json);

            return leanDiffs;
        }

        private async Task<AssociatedDependency> CreateDependencyAsync(string filePath, ulong fileHash, string wadRelativePath, string oldPbePath, string newPbePath, string sourceWad, SerializableChunkDiff originalDiff)
        {
            _logService.LogDebug($"[CreateDependencyAsync] Attempting to create dependency for file '{filePath}' (Hash: {fileHash:X16}) in WAD '{wadRelativePath}'.");
            return await Task.Run(() =>
            {
                string wadVirtualPath = Path.Combine(newPbePath, wadRelativePath);
                if (!File.Exists(wadVirtualPath))
                {
                    _logService.LogDebug($"[CreateDependencyAsync] WAD not found at new path, trying old path: '{wadVirtualPath}'");
                    wadVirtualPath = Path.Combine(oldPbePath, wadRelativePath);
                }

                if (File.Exists(wadVirtualPath))
                {
                    _logService.LogDebug($"[CreateDependencyAsync] WAD found at '{wadVirtualPath}'. Reading...");
                    using var wad = new WadFile(wadVirtualPath);
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
                        _logService.LogDebug($"[CreateDependencyAsync] Chunk NOT found for file '{filePath}' (Hash: {fileHash:X16}) in WAD '{wadVirtualPath}'. This is normal for optional siblings.");
                    }
                }
                else
                {
                    _logService.LogDebug($"[CreateDependencyAsync] WAD file not found for dependency '{filePath}' at either new or old paths: '{wadRelativePath}'.");
                }
                return null;
            });
        }

        private async Task SaveChunksFromWadAsync(string sourceWadPath, string targetChunkPath, IEnumerable<SerializableChunkDiff> chunkDiffs, string wadRelativePath, bool useOld)
        {
            try
            {
                using var sourceWad = new WadFile(sourceWadPath);
                
                // Get valid chunks and ORDER BY OFFSET for high-performance sequential reading
                var hashes = chunkDiffs.Select(d => useOld ? d.OldPathHash : d.NewPathHash).Distinct().ToList();
                var chunksToProcess = hashes
                    .Select(h => sourceWad.Chunks.TryGetValue(h, out var c) ? c : (WadChunk?)null)
                    .Where(c => c.HasValue)
                    .Select(c => c.Value)
                    .OrderBy(c => c.DataOffset)
                    .ToList();

                if (chunksToProcess.Count == 0) return;

                // Create a subfolder for the specific WAD to avoid hash collisions (e.g., localized files)
                string finalTargetDir = Path.Combine(targetChunkPath, wadRelativePath);
                _directoriesCreator.CreateDirectory(finalTargetDir);
                
                // Open the stream ONCE for the entire WAD file processing
                await using var fs = new FileStream(sourceWadPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: true);

                foreach (var chunk in chunksToProcess)
                {
                    fs.Seek(chunk.DataOffset, SeekOrigin.Begin);
                    byte[] rawChunkData = ArrayPool<byte>.Shared.Rent((int)chunk.CompressedSize);
                    try
                    {
                        await fs.ReadExactlyAsync(rawChunkData, 0, (int)chunk.CompressedSize);

                        string chunkFileName = $"{chunk.PathHash:X16}.chunk";
                        string destChunkPath = Path.Combine(finalTargetDir, chunkFileName);

                        await using (var destFs = new FileStream(destChunkPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
                        {
                            await destFs.WriteAsync(rawChunkData.AsMemory(0, (int)chunk.CompressedSize));
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rawChunkData);
                    }
                }
            }
            catch (System.Exception ex)
            {
                _logService.LogError(ex, $"Failed to save chunks from {sourceWadPath}");
            }
        }
    }
}
