using System;
using System.Collections.Generic;
using System.IO;
using AssetsManager.Services.Hashes;
using System.Linq;
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

                if (!File.Exists(oldWadFile) || !File.Exists(newWadFile))
                {
                    _totalChunksGlobal = 0;
                    _completedChunksGlobal = 0;
                    NotifyComparisonStarted(0);
                    NotifyComparisonProgressChanged(0, Path.GetFileName(oldWadFile), false, "WAD file not found.");
                    return;
                }

                // Open both WADs ONCE and reuse the instances for counting and diffing
                using var oldWad = new WadFile(oldWadFile);
                using var newWad = new WadFile(newWadFile);

                int totalChunks = oldWad.Chunks.Count + newWad.Chunks.Count;
                _totalChunksGlobal = totalChunks;
                _completedChunksGlobal = 0;
                NotifyComparisonStarted(_totalChunksGlobal);

                bool success = totalChunks > 0;
                string errorMessage = success ? null : "WAD file is empty.";

                if (totalChunks > 0)
                {
                    var cache = new Dictionary<ulong, string>();
                    var relativePath = Path.GetFileName(oldWadFile);
                    var diffs = await CollectDiffsAsync(oldWad, newWad, relativePath, cache, cancellationToken);
                    allDiffs.AddRange(diffs);
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

                var sessionCache = new Dictionary<ulong, string>();

                // Two-phase approach with bounded memory:
                //   Phase 1: read just the WAD header (8-274 bytes) to count chunks - NO WadFile instances.
                //   Phase 2: open each WAD pair, diff, and dispose immediately.
                // Result: only 2 WadFile instances alive at any time, regardless of total file count.
                await Task.Run(() =>
                {
                    var searchPatterns = new[] { "*.wad.client", "*.wad" };
                    var files = searchPatterns
                        .SelectMany(pattern => Directory.GetFiles(oldDir, pattern, SearchOption.AllDirectories))
                        .ToList();

                    int total = 0;
                    var validPaths = new List<(string OldPath, string NewPath, string RelativePath)>();

                    foreach (var oldWadFile in files)
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        var relativePath = Path.GetRelativePath(oldDir, oldWadFile);
                        var newWadFileFullPath = Path.Combine(newDir, relativePath);
                        if (!File.Exists(newWadFileFullPath)) continue;

                        int oldCount = WadChunkUtils.ReadWadChunkCount(oldWadFile);
                        int newCount = WadChunkUtils.ReadWadChunkCount(newWadFileFullPath);

                        // Fallback: if header read fails, try a full open to salvage the count
                        if (oldCount < 0 || newCount < 0)
                        {
                            try
                            {
                                using var probeOld = new WadFile(oldWadFile);
                                using var probeNew = new WadFile(newWadFileFullPath);
                                oldCount = probeOld.Chunks.Count;
                                newCount = probeNew.Chunks.Count;
                            }
                            catch { continue; /* Skip corrupt WADs */ }
                        }

                        total += oldCount + newCount;
                        validPaths.Add((oldWadFile, newWadFileFullPath, relativePath));
                    }

                    _totalChunksGlobal = total;
                    _completedChunksGlobal = 0;
                    NotifyComparisonStarted(_totalChunksGlobal);

                    int fileIndex = 0;
                    foreach (var file in validPaths)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        fileIndex++;

                        string statusMsg = $"{fileIndex} of {validPaths.Count} files: {file.RelativePath}";

                        try
                        {
                            using var oldWad = new WadFile(file.OldPath);
                            using var newWad = new WadFile(file.NewPath);
                            var diffs = CollectDiffsInternal(oldWad, newWad, file.RelativePath, sessionCache, cancellationToken, statusMsg);
                            allDiffs.AddRange(diffs);
                        }
                        catch { /* Skip corrupt WADs */ }
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

        private async Task<List<ChunkDiff>> CollectDiffsAsync(WadFile oldWad, WadFile newWad, string sourceWadFile, Dictionary<ulong, string> cache, CancellationToken cancellationToken, string statusMsg = "")
        {
            return await Task.Run(() => CollectDiffsInternal(oldWad, newWad, sourceWadFile, cache, cancellationToken, statusMsg), cancellationToken);
        }

        private List<ChunkDiff> CollectDiffsInternal(WadFile oldWad, WadFile newWad, string sourceWadFile, Dictionary<ulong, string> cache, CancellationToken cancellationToken, string statusMsg)
        {
            var diffs = new List<ChunkDiff>();

            var oldChunks = oldWad.Chunks;
            var newChunks = newWad.Chunks;

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

    }
}
