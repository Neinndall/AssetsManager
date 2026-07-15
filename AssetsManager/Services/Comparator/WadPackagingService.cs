using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Services.Audio;
using AssetsManager.Services.Core;
using AssetsManager.Utils;

namespace AssetsManager.Services.Comparator
{
    public class WadPackagingService
    {
        private readonly LogService _logService;
        private readonly DirectoriesCreator _directoriesCreator;
        private readonly AudioBankLinkerService _audioBankLinkerService;

        public WadPackagingService(LogService logService, DirectoriesCreator directoriesCreator, AudioBankLinkerService audioBankLinkerService)
        {
            _logService = logService;
            _directoriesCreator = directoriesCreator;
            _audioBankLinkerService = audioBankLinkerService;
        }

        public async Task<List<SerializableChunkDiff>> CreateLeanWadPackageAsync(IEnumerable<SerializableChunkDiff> diffs, string oldPbePath, string newPbePath, string targetOldWadsPath, string targetNewWadsPath, CancellationToken cancellationToken = default)
        {
            var finalDiffs = diffs.ToList();
            var diffsByHash = new Dictionary<ulong, SerializableChunkDiff>();
            var diffsByPath = new Dictionary<string, SerializableChunkDiff>(StringComparer.OrdinalIgnoreCase);
            foreach (var diff in finalDiffs)
            {
                if (diff.OldPathHash != 0) diffsByHash.TryAdd(diff.OldPathHash, diff);
                if (diff.NewPathHash != 0) diffsByHash.TryAdd(diff.NewPathHash, diff);
                string path = diff.NewPath ?? diff.OldPath;
                if (!string.IsNullOrEmpty(path)) diffsByPath.TryAdd(path, diff);
            }

            var audioBankDiffs = finalDiffs.Where(d => 
                (d.NewPath ?? d.OldPath).EndsWith("_events.bnk", StringComparison.OrdinalIgnoreCase) ||
                (d.NewPath ?? d.OldPath).EndsWith("_audio.bnk", StringComparison.OrdinalIgnoreCase) ||
                (d.NewPath ?? d.OldPath).EndsWith("_audio.wpk", StringComparison.OrdinalIgnoreCase)
            ).ToList();
            _logService.LogDebug($"[CreateLeanWadPackageAsync] Found {audioBankDiffs.Count} audio bank diffs to process.");
            var dependencyRequests = new List<DependencyRequest>(audioBankDiffs.Count * 3);

            foreach (var audioBankDiff in audioBankDiffs)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                        if (File.Exists(PathUtils.ResolveWadPath(newPbePath, potentialPath)) || File.Exists(PathUtils.ResolveWadPath(oldPbePath, potentialPath)))
                        {
                            targetWadRelativePath = potentialPath;
                        }
                    }

                    _logService.LogDebug($"[CreateLeanWadPackageAsync] Resolved target WAD path: '{targetWadRelativePath}'. Creating dependency...");

                    ulong binHash = XxHash64Ext.Hash(binStrategy.BinPath.ToLowerInvariant());
                    diffsByHash.TryGetValue(binHash, out var diffForBinDependency);
                    dependencyRequests.Add(new DependencyRequest(audioBankDiff, binStrategy.BinPath, binHash,
                        targetWadRelativePath, targetWadRelativePath, diffForBinDependency));
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
                    diffsByPath.TryGetValue(siblingVirtualPath, out var diffForSiblingDependency);
                    dependencyRequests.Add(new DependencyRequest(audioBankDiff, siblingVirtualPath, sibling.PathHash,
                        audioBankDiff.SourceWadFile, audioBankDiff.SourceWadFile, diffForSiblingDependency));
                }
            }

            ResolveDependencies(dependencyRequests, oldPbePath, newPbePath, cancellationToken);
            foreach (var request in dependencyRequests)
            {
                if (request.Dependency != null) request.Owner.Dependencies.Add(request.Dependency);
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
                cancellationToken.ThrowIfCancellationRequested();
                var wadFileRelativePath = wadGroup.Key;
                _logService.LogDebug($"Processing {wadFileRelativePath} for chunk packaging...");

                string sourceOldWadPath = PathUtils.ResolveWadPath(oldPbePath, wadFileRelativePath);
                var oldChunksToSave = wadGroup
                    .Where(d => d.Type == ChunkDiffType.Modified || d.Type == ChunkDiffType.Renamed || d.Type == ChunkDiffType.Removed)
                    .ToList();
                if (oldChunksToSave.Any())
                {
                    if (!File.Exists(sourceOldWadPath))
                        throw new FileNotFoundException($"Unable to package OLD chunks because the source WAD could not be resolved: '{wadFileRelativePath}'.", sourceOldWadPath);

                    await SaveChunksFromWadAsync(sourceOldWadPath, targetOldWadsPath, oldChunksToSave, wadFileRelativePath, true, cancellationToken);
                }

                string sourceNewWadPath = PathUtils.ResolveWadPath(newPbePath, wadFileRelativePath);
                var newChunksToSave = wadGroup
                    .Where(d => d.Type == ChunkDiffType.Modified || d.Type == ChunkDiffType.Renamed || d.Type == ChunkDiffType.New)
                    .ToList();
                if (newChunksToSave.Any())
                {
                    if (!File.Exists(sourceNewWadPath))
                        throw new FileNotFoundException($"Unable to package NEW chunks because the source WAD could not be resolved: '{wadFileRelativePath}'.", sourceNewWadPath);

                    await SaveChunksFromWadAsync(sourceNewWadPath, targetNewWadsPath, newChunksToSave, wadFileRelativePath, false, cancellationToken);
                }
            }

            return finalDiffs;
        }

        public async Task<List<SerializableChunkDiff>> SaveBackupAsync(List<SerializableChunkDiff> diffs, string oldPbePath, string newPbePath, string destinationPath, string version = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Use the centralized directory creator to prepare the structure
            _directoriesCreator.PrepareComparisonDirectory(destinationPath);

            string wadChunksOldDir = Path.Combine(destinationPath, "wad_chunks", "old");
            string wadChunksNewDir = Path.Combine(destinationPath, "wad_chunks", "new");
            string jsonFilePath = Path.Combine(destinationPath, "wadcomparison.json");
            string temporaryJsonFilePath = jsonFilePath + ".tmp";

            _logService.LogDebug($"[WadPackagingService] Saving full backup to {destinationPath}");

            var leanDiffs = await CreateLeanWadPackageAsync(diffs, oldPbePath, newPbePath, wadChunksOldDir, wadChunksNewDir, cancellationToken);

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
            try
            {
                await File.WriteAllTextAsync(temporaryJsonFilePath, json, cancellationToken);
                File.Move(temporaryJsonFilePath, jsonFilePath, true);
            }
            finally
            {
                if (File.Exists(temporaryJsonFilePath)) File.Delete(temporaryJsonFilePath);
            }

            return leanDiffs;
        }

        private void ResolveDependencies(List<DependencyRequest> requests, string oldPbePath, string newPbePath, CancellationToken cancellationToken)
        {
            foreach (var wadGroup in requests.GroupBy(request => request.WadRelativePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string wadVirtualPath = PathUtils.ResolveWadPath(newPbePath, wadGroup.Key);
                if (!File.Exists(wadVirtualPath))
                {
                    wadVirtualPath = PathUtils.ResolveWadPath(oldPbePath, wadGroup.Key);
                }

                if (!File.Exists(wadVirtualPath)) continue;

                using var wad = new WadFile(wadVirtualPath);
                foreach (var request in wadGroup)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (wad.Chunks.TryGetValue(request.FileHash, out var chunk))
                    {
                        request.Dependency = new AssociatedDependency
                        {
                            Path = request.FilePath,
                            SourceWad = request.SourceWad,
                            OldPathHash = request.FileHash,
                            NewPathHash = request.FileHash,
                            CompressionType = chunk.Compression,
                            Type = request.OriginalDiff?.Type ?? ChunkDiffType.Dependency,
                            WasTopLevelDiff = true
                        };
                    }
                }
            }
        }

        private async Task SaveChunksFromWadAsync(string sourceWadPath, string targetChunkPath, IEnumerable<SerializableChunkDiff> chunkDiffs, string wadRelativePath, bool useOld, CancellationToken cancellationToken)
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
                    cancellationToken.ThrowIfCancellationRequested();
                    fs.Seek(chunk.DataOffset, SeekOrigin.Begin);
                    byte[] rawChunkData = ArrayPool<byte>.Shared.Rent(chunk.CompressedSize);
                    try
                    {
                        await fs.ReadExactlyAsync(rawChunkData.AsMemory(0, chunk.CompressedSize), cancellationToken);

                        string chunkFileName = $"{chunk.PathHash:X16}.chunk";
                        string destChunkPath = Path.Combine(finalTargetDir, chunkFileName);

                        await using (var destFs = new FileStream(destChunkPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
                        {
                            await destFs.WriteAsync(rawChunkData.AsMemory(0, chunk.CompressedSize), cancellationToken);
                        }

                        await WadChunkMetadataStore.WriteAsync(destChunkPath, sourceWad, chunk, cancellationToken);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rawChunkData);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logService.LogError(ex, $"Failed to save chunks from {sourceWadPath}");
                throw;
            }
        }

        private sealed record DependencyRequest(
            SerializableChunkDiff Owner,
            string FilePath,
            ulong FileHash,
            string WadRelativePath,
            string SourceWad,
            SerializableChunkDiff OriginalDiff)
        {
            public AssociatedDependency Dependency { get; set; }
        }
    }
}
