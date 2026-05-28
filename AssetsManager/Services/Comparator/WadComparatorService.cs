using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using AssetsManager.Services.Hashes;
using System.Linq;
using Serilog;
using System.Threading;
using System.Threading.Tasks;
using LeagueToolkit.Core.Wad;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Services.Core;
using AssetsManager.Utils;

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
                _logService.LogWarning("WADs comparison process was cancelled.");
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
                
                NotifyComparisonStarted(0);

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
                
                NotifyComparisonStarted(_totalChunksGlobal);

                // FINAL ENGINE: Session cache for string deduplication and speed
                var sessionCache = new Dictionary<ulong, string>();

                // Run the entire loop in a SINGLE background task to eliminate overhead
                await Task.Run(() =>
                {
                    int fileIndex = 0;
                    foreach (var file in scanResult.ValidFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        fileIndex++;

                        string statusMsg = $"{fileIndex} of {scanResult.ValidFiles.Count} files: {file.RelativePath}";

                        var diffs = CollectDiffsInternal(file.OldPath, file.NewPath, file.RelativePath, sessionCache, cancellationToken, statusMsg);
                        allDiffs.AddRange(diffs);
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logService.LogWarning("WADs comparison process was cancelled.");
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
            var cache = new Dictionary<ulong, string>();
            return await Task.Run(() => CollectDiffsInternal(oldWadFile, newWadFile, sourceWadFile, cache, cancellationToken, statusMsg), cancellationToken);
        }

        private List<ChunkDiff> CollectDiffsInternal(string oldWadFile, string newWadFile, string sourceWadFile, Dictionary<ulong, string> cache, CancellationToken cancellationToken, string statusMsg)
        {
            var diffs = new List<ChunkDiff>();

            Dictionary<ulong, WadChunk> oldChunks;
            Dictionary<ulong, WadChunk> newChunks;

            using (var oldWad = new WadFile(oldWadFile))
            using (var newWad = new WadFile(newWadFile))
            {
                oldChunks = oldWad.Chunks.ToDictionary(c => c.Key, c => c.Value);
                newChunks = newWad.Chunks.ToDictionary(c => c.Key, c => c.Value);

                foreach (var oldChunk in oldChunks.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!newChunks.TryGetValue(oldChunk.PathHash, out var newChunk))
                    {
                        var oldPath = ResolveNameOptimized(oldChunk.PathHash, oldChunk, oldWad, cache);
                        diffs.Add(new ChunkDiff { Type = ChunkDiffType.Removed, OldChunk = oldChunk, OldPath = oldPath, SourceWadFile = sourceWadFile });
                    }
                    else if (oldChunk.Checksum != newChunk.Checksum)
                    {
                        var oldPath = ResolveNameOptimized(oldChunk.PathHash, oldChunk, oldWad, cache);
                        diffs.Add(new ChunkDiff { Type = ChunkDiffType.Modified, OldChunk = oldChunk, NewChunk = newChunk, OldPath = oldPath, NewPath = oldPath, SourceWadFile = sourceWadFile });
                    }

                    ReportProgress(statusMsg);
                }

                var oldChecksumMap = new Dictionary<ulong, ulong>();
                foreach (var oldChunk in oldChunks.Values)
                {
                    if (!newChunks.ContainsKey(oldChunk.PathHash))
                    {
                        oldChecksumMap.TryAdd(oldChunk.Checksum, oldChunk.PathHash);
                    }
                }
                foreach (var oldChunk in oldChunks.Values)
                {
                    oldChecksumMap.TryAdd(oldChunk.Checksum, oldChunk.PathHash);
                }

                foreach (var newChunk in newChunks.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!oldChunks.ContainsKey(newChunk.PathHash))
                    {
                        var newPath = ResolveNameOptimized(newChunk.PathHash, newChunk, newWad, cache);
                        ulong newChecksum = newChunk.Checksum;

                        if (oldChecksumMap.TryGetValue(newChecksum, out ulong oldHash))
                        {
                            var oldPath = ResolveNameOptimized(oldHash, oldChunks[oldHash], oldWad, cache);
                            diffs.Add(new ChunkDiff { Type = ChunkDiffType.Renamed, OldChunk = oldChunks[oldHash], NewChunk = newChunk, OldPath = oldPath, NewPath = newPath, SourceWadFile = sourceWadFile });
                        }
                        else
                        {
                            diffs.Add(new ChunkDiff { Type = ChunkDiffType.New, NewChunk = newChunk, NewPath = newPath, SourceWadFile = sourceWadFile });
                        }
                    }

                    ReportProgress(statusMsg);
                }
            }

            return diffs;
        }

        private string ResolveNameOptimized(ulong hash, WadChunk chunk, WadFile wad, Dictionary<ulong, string> cache)
        {
            if (cache.TryGetValue(hash, out var cached)) return cached;

            string resolved = _hashResolverService.ResolveHash(hash);

            // SMART GUESSING: If hash is unknown (0x...), read header in-situ for extension
            if (resolved == hash.ToString("x16") || !Path.HasExtension(resolved))
            {
                try
                {
                    using (var stream = wad.OpenChunk(chunk))
                    {
                        var buffer = new byte[256];
                        var bytesRead = stream.Read(buffer, 0, buffer.Length);
                        var data = new Span<byte>(buffer, 0, bytesRead);
                        string ext = FileTypeDetector.GuessExtension(data);
                        if (!string.IsNullOrEmpty(ext))
                        {
                            resolved = resolved.EndsWith("." + ext) ? resolved : resolved + "." + ext;
                        }
                    }
                }
                catch { /* Fallback */ }
            }

            cache[hash] = resolved;
            return resolved;
        }

        private void ReportProgress(string statusMsg)
        {
            int completed = Interlocked.Increment(ref _completedChunksGlobal);
            // Report every 100 chunks for a smooth and progressive UI experience
            if (completed % 100 == 0 || completed == _totalChunksGlobal)
            {
                NotifyComparisonProgressChanged(completed, statusMsg, true, null);
            }
        }

        private async Task<Dictionary<ulong, ulong>> GetChunkChecksumsAsync(IEnumerable<WadChunk> chunks, CancellationToken cancellationToken, string statusMsg)
        {
            var checksums = new Dictionary<ulong, ulong>();
            // This method is now unused by CollectDiffsAsync but kept for compatibility if needed.
            await Task.Run(() =>
            {
                foreach (var chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    checksums[chunk.PathHash] = chunk.Checksum;
                    ReportProgress(statusMsg);
                }
            });
            return checksums;
        }

    }
}
