using System;
using System.Collections.Generic;
using System.IO;
using AssetsManager.Services.Hashes;
using System.Linq;
using Serilog;
using System.Threading; // Added for CancellationToken and OperationCanceledException
using System.Threading.Tasks;
using LeagueToolkit.Core.Wad;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Services.Core;
using AssetsManager.Utils; // Added for TaskCancellationManager

namespace AssetsManager.Services.Comparator
{
    public class WadComparatorService
    {
        private readonly HashResolverService _hashResolverService;
        private readonly LogService _logService;

        public event Action<int> ComparisonStarted;
        public event Action<int, string, bool, string> ComparisonProgressChanged;
        public event Action<List<ChunkDiff>, string, string> ComparisonCompleted;

        private int _totalChunksGlobal;
        private int _completedChunksGlobal;

        public WadComparatorService(HashResolverService hashResolverService, LogService logService)
        {
            _hashResolverService = hashResolverService;
            _logService = logService;
        }

        public void NotifyComparisonStarted(int totalFiles)
        {
            ComparisonStarted?.Invoke(totalFiles);
        }

        public void NotifyComparisonProgressChanged(int completedFiles, string currentWadFile, bool isSuccess, string errorMessage)
        {
            ComparisonProgressChanged?.Invoke(completedFiles, currentWadFile, isSuccess, errorMessage);
        }

        public void NotifyComparisonCompleted(List<ChunkDiff> allDiffs, string oldPbePath, string newPbePath)
        {
            ComparisonCompleted?.Invoke(allDiffs, oldPbePath, newPbePath);
        }

        public async Task CompareSingleWadAsync(string oldWadFile, string newWadFile, CancellationToken cancellationToken)
        {
            List<ChunkDiff> allDiffs = new List<ChunkDiff>();
            string oldDir = Path.GetDirectoryName(oldWadFile);
            string newDir = Path.GetDirectoryName(newWadFile);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                _logService.Log($"Starting WAD comparison for a single file: {Path.GetFileName(oldWadFile)}");
                
                int totalChunks = 0;
                if (File.Exists(oldWadFile) && File.Exists(newWadFile))
                {
                    using var oldWad = new WadFile(oldWadFile);
                    using var newWad = new WadFile(newWadFile);
                    totalChunks = oldWad.Chunks.Count + newWad.Chunks.Count;
                }

                _totalChunksGlobal = totalChunks;
                _completedChunksGlobal = 0;
                NotifyComparisonStarted(_totalChunksGlobal);

                bool success = true;
                string errorMessage = null;

                if (totalChunks > 0)
                {
                    var relativePath = Path.GetFileName(oldWadFile);
                    var diffs = await CollectDiffsAsync(oldWadFile, newWadFile, relativePath, cancellationToken);
                    allDiffs.AddRange(diffs);
                }
                else
                {
                    success = false;
                    errorMessage = $"WAD file not found or empty.";
                }

                NotifyComparisonProgressChanged(_completedChunksGlobal, Path.GetFileName(oldWadFile), success, errorMessage);
            }
            catch (OperationCanceledException)
            {
                _logService.LogWarning("Single WAD comparison was cancelled.");
                allDiffs = null;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "An error occurred during single WAD comparison.");
            }
            finally
            {
                NotifyComparisonCompleted(allDiffs, oldDir, newDir);
                if (allDiffs != null)
                {
                    if (allDiffs.Count == 0)
                    {
                        _logService.LogSuccess("Comparison completed with no differences found.");
                    }
                    else
                    {
                        _logService.LogSuccess($"Single WAD comparison completed. Found {allDiffs.Count} {(allDiffs.Count == 1 ? "difference" : "differences")}.");
                    }
                }
                else if (allDiffs == null && !cancellationToken.IsCancellationRequested)
                {
                    _logService.LogError("Single WAD comparison completed with errors.");
                }
            }
        }

        public async Task CompareWadsAsync(string oldDir, string newDir, CancellationToken cancellationToken)
        {
            List<ChunkDiff> allDiffs = new List<ChunkDiff>();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                _logService.Log("Starting WADs comparison...");
                
                // Notify UI immediately to show activity (Indeterminate spinner)
                NotifyComparisonStarted(0);

                // Run heavy scanning in a background task to keep UI responsive
                var scanResult = await Task.Run(() =>
                {
                    var searchPatterns = new[] { "*.wad.client", "*.wad" };
                    var files = searchPatterns
                        .SelectMany(pattern => Directory.GetFiles(oldDir, pattern, SearchOption.AllDirectories))
                        .ToList();

                    int total = 0;
                    var valid = new List<(string OldPath, string NewPath, string RelativePath)>();

                    foreach (var oldWadFile in files)
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        var relativePath = Path.GetRelativePath(oldDir, oldWadFile);
                        var newWadFileFullPath = Path.Combine(newDir, relativePath);
                        if (File.Exists(newWadFileFullPath))
                        {
                            using var oldWad = new WadFile(oldWadFile);
                            using var newWad = new WadFile(newWadFileFullPath);
                            total += oldWad.Chunks.Count + newWad.Chunks.Count;
                            valid.Add((oldWadFile, newWadFileFullPath, relativePath));
                        }
                    }
                    return (TotalChunks: total, ValidFiles: valid);
                }, cancellationToken);

                _totalChunksGlobal = scanResult.TotalChunks;
                _completedChunksGlobal = 0;
                
                // Update UI with the real total discovered
                NotifyComparisonStarted(_totalChunksGlobal);

                // Phase 2: Compare
                int fileIndex = 0;
                foreach (var file in scanResult.ValidFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    fileIndex++;

                    // Use the full RelativePath for consistent technical context
                    string statusMsg = $"{fileIndex} of {scanResult.ValidFiles.Count} files: {file.RelativePath}";

                    var diffs = await CollectDiffsAsync(file.OldPath, file.NewPath, file.RelativePath, cancellationToken, statusMsg);
                    allDiffs.AddRange(diffs);
                }
            }
            catch (OperationCanceledException)
            {
                _logService.LogWarning("WADs comparison was cancelled.");
                allDiffs = null;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "An error occurred during WAD comparison.");
            }
            finally
            {
                NotifyComparisonCompleted(allDiffs, oldDir, newDir);
                if (allDiffs != null)
                {
                    if (allDiffs.Count == 0)
                    {
                        _logService.LogSuccess("Comparison completed with no differences found.");
                    }
                    else
                    {
                        _logService.LogSuccess($"WADs comparison completed. Found {allDiffs.Count} {(allDiffs.Count == 1 ? "difference" : "differences")}.");
                    }
                }
                else if (allDiffs == null && !cancellationToken.IsCancellationRequested)
                {
                    _logService.LogError("WADs comparison completed with errors.");
                }
            }
        }

        private async Task<List<ChunkDiff>> CollectDiffsAsync(string oldWadPath, string newWadPath, string sourceWadFile, CancellationToken cancellationToken, string statusMsg = "")
        {
            var diffs = new List<ChunkDiff>();

            Dictionary<ulong, WadChunk> oldChunks;
            Dictionary<ulong, WadChunk> newChunks;

            using (var oldWad = new WadFile(oldWadPath))
            using (var newWad = new WadFile(newWadPath))
            {
                oldChunks = oldWad.Chunks.ToDictionary(c => c.Key, c => c.Value);
                newChunks = newWad.Chunks.ToDictionary(c => c.Key, c => c.Value);
            }

            // We report progress during hashing, which is the slow part
            var oldChunkChecksums = await GetChunkChecksumsAsync(oldWadPath, oldChunks.Values, cancellationToken, statusMsg);
            var newChunkChecksums = await GetChunkChecksumsAsync(newWadPath, newChunks.Values, cancellationToken, statusMsg);

            // Comparison logic (fast)
            foreach (var oldChunk in oldChunks.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var oldPath = _hashResolverService.ResolveHash(oldChunk.PathHash);
                if (!newChunks.ContainsKey(oldChunk.PathHash))
                {
                    diffs.Add(new ChunkDiff { Type = ChunkDiffType.Removed, OldChunk = oldChunk, OldPath = oldPath, SourceWadFile = sourceWadFile });
                }
                else
                {
                    var newChunk = newChunks[oldChunk.PathHash];
                    if (oldChunkChecksums[oldChunk.PathHash] != newChunkChecksums[newChunk.PathHash])
                    {
                        var newPath = _hashResolverService.ResolveHash(newChunk.PathHash);
                        diffs.Add(new ChunkDiff { Type = ChunkDiffType.Modified, OldChunk = oldChunk, NewChunk = newChunk, OldPath = oldPath, NewPath = newPath, SourceWadFile = sourceWadFile });
                    }
                }
            }

            foreach (var newChunk in newChunks.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!oldChunks.ContainsKey(newChunk.PathHash))
                {
                    var newPath = _hashResolverService.ResolveHash(newChunk.PathHash);
                    var oldChecksum = oldChunkChecksums.FirstOrDefault(c => c.Value == newChunkChecksums[newChunk.PathHash]);
                    if (oldChecksum.Key != 0)
                    {
                        var oldPath = _hashResolverService.ResolveHash(oldChecksum.Key);
                        diffs.Add(new ChunkDiff { Type = ChunkDiffType.Renamed, OldChunk = oldChunks[oldChecksum.Key], NewChunk = newChunk, OldPath = oldPath, NewPath = newPath, SourceWadFile = sourceWadFile });
                    }
                    else
                    {
                        diffs.Add(new ChunkDiff { Type = ChunkDiffType.New, NewChunk = newChunk, NewPath = newPath, SourceWadFile = sourceWadFile });
                    }
                }
            }

            return diffs;
        }

        private async Task<Dictionary<ulong, ulong>> GetChunkChecksumsAsync(string wadPath, IEnumerable<WadChunk> chunks, CancellationToken cancellationToken, string statusMsg)
        {
            var checksums = new System.Collections.Concurrent.ConcurrentDictionary<ulong, ulong>();
            var chunkList = chunks.ToList();
            int totalInWad = chunkList.Count;

            // Fixed threading (Max 4 threads for stability and performance)
            int threadCount = Math.Clamp(Environment.ProcessorCount, 1, 4);
            
            _logService.Log($"[Thread Audit] Comparing {Path.GetFileName(wadPath)} using {threadCount} threads (Total chunks: {totalInWad})");

            var parallelOptions = new ParallelOptions 
            { 
                MaxDegreeOfParallelism = threadCount, 
                CancellationToken = cancellationToken 
            };

            await Task.Run(() =>
            {
                Parallel.ForEach(chunkList, parallelOptions, 
                () => new WadFile(wadPath), // Thread Local Init: Open a new WadFile for this thread
                (chunk, state, localWad) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    using var decompressedChunk = localWad.LoadChunkDecompressed(chunk);
                    var checksum = System.IO.Hashing.XxHash64.HashToUInt64(decompressedChunk.Span);
                    checksums[chunk.PathHash] = checksum;

                    int completed = Interlocked.Increment(ref _completedChunksGlobal);
                    if (completed % 20 == 0 || completed == _totalChunksGlobal)
                    {
                        NotifyComparisonProgressChanged(completed, statusMsg, true, null);
                    }
                    return localWad;
                },
                localWad => localWad.Dispose() // Thread Local Cleanup
                );
            });

            return checksums.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
    }
}
