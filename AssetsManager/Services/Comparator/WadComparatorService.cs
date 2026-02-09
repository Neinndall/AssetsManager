using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
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

                _logService.Log("Starting WADs comparison (Fast Mode)...");
                
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
                            try
                            {
                                using var oldWad = new WadFile(oldWadFile);
                                using var newWad = new WadFile(newWadFileFullPath);
                                total += oldWad.Chunks.Count + newWad.Chunks.Count;
                                valid.Add((oldWadFile, newWadFileFullPath, relativePath));
                            }
                            catch { /* Skip corrupt WADs */ }
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

        private async Task<List<ChunkDiff>> CollectDiffsAsync(string oldWadFile, string newWadFile, string sourceWadFile, CancellationToken cancellationToken, string statusMsg = "")
        {
            var diffs = new List<ChunkDiff>();

            Dictionary<ulong, WadChunk> oldChunks;
            Dictionary<ulong, WadChunk> newChunks;

            using (var oldWad = new WadFile(oldWadFile))
            using (var newWad = new WadFile(newWadFile))
            {
                oldChunks = oldWad.Chunks.ToDictionary(c => c.Key, c => c.Value);
                newChunks = newWad.Chunks.ToDictionary(c => c.Key, c => c.Value);
            }

            // Fast Mode: We use the pre-calculated hashes from the WAD header
            var oldChunkChecksums = await GetChunkChecksumsAsync(oldChunks.Values, cancellationToken, statusMsg);
            var newChunkChecksums = await GetChunkChecksumsAsync(newChunks.Values, cancellationToken, statusMsg);

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

        private static readonly FieldInfo _checksumField = typeof(WadChunk).GetField("_checksum", BindingFlags.NonPublic | BindingFlags.Instance);

        private async Task<Dictionary<ulong, ulong>> GetChunkChecksumsAsync(IEnumerable<WadChunk> chunks, CancellationToken cancellationToken, string statusMsg)
        {
            var checksums = new Dictionary<ulong, ulong>();

            await Task.Run(() =>
            {
                foreach (var chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Fast Mode: Use Reflection to access the private '_checksum' field in the WAD header
                    // This is nearly instant and avoids decompressing the data.
                    ulong checksum = 0;
                    if (_checksumField != null)
                    {
                        checksum = (ulong)_checksumField.GetValue(chunk);
                    }
                    
                    checksums[chunk.PathHash] = checksum;

                    int completed = Interlocked.Increment(ref _completedChunksGlobal);
                    if (completed % 100 == 0 || completed == _totalChunksGlobal)
                    {
                        NotifyComparisonProgressChanged(completed, statusMsg, true, null);
                    }
                }
            });

            return checksums;
        }
    }
}
