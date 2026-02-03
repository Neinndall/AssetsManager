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
                cancellationToken.ThrowIfCancellationRequested(); // Check for cancellation at the start

                _logService.Log($"Starting WAD comparison for a single file: {Path.GetFileName(oldWadFile)}");
                NotifyComparisonStarted(1);

                bool success = true;
                string errorMessage = null;

                if (File.Exists(oldWadFile) && File.Exists(newWadFile))
                {
                    cancellationToken.ThrowIfCancellationRequested(); // Check before long operation
                    var relativePath = Path.GetFileName(oldWadFile);
                    Log.Information($"Comparing {relativePath}...");
                    using var oldWad = new WadFile(oldWadFile);
                    using var newWad = new WadFile(newWadFile);

                    var diffs = await CollectDiffsAsync(oldWad, newWad, relativePath, cancellationToken);
                    Log.Information($"Found {diffs.Count} differences in {relativePath}.");
                    allDiffs.AddRange(diffs);
                }
                else
                {
                    success = false;
                    errorMessage = $"One or both WAD files not found. Old: {oldWadFile}, New: {newWadFile}";
                    Log.Warning(errorMessage);
                }

                NotifyComparisonProgressChanged(1, Path.GetFileName(oldWadFile), success, errorMessage);
            }
            catch (OperationCanceledException)
            {
                _logService.LogWarning("Single WAD comparison was cancelled.");
                allDiffs = null; // Indicate cancellation by nulling diffs
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
                else if (allDiffs == null && !cancellationToken.IsCancellationRequested) // Only log error if not cancelled
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
                cancellationToken.ThrowIfCancellationRequested(); // Check for cancellation at the start

                _logService.Log("Starting WADs comparison...");
                var searchPatterns = new[] { "*.wad.client", "*.wad" };
                var oldWadFiles = searchPatterns
                    .SelectMany(pattern => Directory.GetFiles(oldDir, pattern, SearchOption.AllDirectories))
                    .ToList();

                int totalFiles = oldWadFiles.Count;
                int processedFiles = 0;

                NotifyComparisonStarted(totalFiles);

                foreach (var oldWadFile in oldWadFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested(); // Check for cancellation in loop

                    var relativePath = Path.GetRelativePath(oldDir, oldWadFile);
                    var newWadFileFullPath = Path.Combine(newDir, relativePath);

                    processedFiles++;
                    bool success = true;
                    string errorMessage = null;

                    if (File.Exists(newWadFileFullPath))
                    {
                        cancellationToken.ThrowIfCancellationRequested(); // Check before long operation
                        Log.Information($"Comparing {relativePath}...");
                        using var oldWad = new WadFile(oldWadFile);
                        using var newWad = new WadFile(newWadFileFullPath);

                        var diffs = await CollectDiffsAsync(oldWad, newWad, relativePath, cancellationToken);
                        Log.Information($"Found {diffs.Count} differences in {relativePath}.");
                        allDiffs.AddRange(diffs);
                    }
                    else
                    {
                        success = false;
                        errorMessage = $"New WAD file not found: {newWadFileFullPath}.";
                        Log.Warning($"New WAD file not found: {newWadFileFullPath}. Skipping comparison for this file.");
                    }
                    NotifyComparisonProgressChanged(processedFiles, Path.GetFileName(relativePath), success, errorMessage);
                }
            }
            catch (OperationCanceledException)
            {
                _logService.LogWarning("WADs comparison was cancelled.");
                allDiffs = null; // Indicate cancellation by nulling diffs
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
                else if (allDiffs == null && !cancellationToken.IsCancellationRequested) // Only log error if not cancelled
                {
                    _logService.LogError("WADs comparison completed with errors.");
                }
            }
        }

        private async Task<List<ChunkDiff>> CollectDiffsAsync(WadFile oldWad, WadFile newWad, string sourceWadFile, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); // Check for cancellation at the start

            var diffs = new List<ChunkDiff>();

            var oldChunks = oldWad.Chunks.ToDictionary(c => c.Key, c => c.Value);
            var newChunks = newWad.Chunks.ToDictionary(c => c.Key, c => c.Value);

            var oldChunkChecksums = await GetChunkChecksumsAsync(oldWad, oldChunks.Values, cancellationToken); // Pass token
            var newChunkChecksums = await GetChunkChecksumsAsync(newWad, newChunks.Values, cancellationToken); // Pass token

            // Removed and Modified
            foreach (var oldChunk in oldChunks.Values)
            {
                cancellationToken.ThrowIfCancellationRequested(); // Check for cancellation in loop

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

            // New and Renamed
            foreach (var newChunk in newChunks.Values)
            {
                cancellationToken.ThrowIfCancellationRequested(); // Check for cancellation in loop

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

        private async Task<Dictionary<ulong, ulong>> GetChunkChecksumsAsync(WadFile wadFile, IEnumerable<WadChunk> chunks, CancellationToken cancellationToken)
        {
            var checksums = new Dictionary<ulong, ulong>();

            await Task.Run(() =>
            {
                foreach (var chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    using var decompressedChunk = wadFile.LoadChunkDecompressed(chunk);
                    // Use Blake3 (Pure C#) for high-performance and secure hashing
                    var hash = Blake3.Hasher.Hash(decompressedChunk.Span);
                    
                    // Convert the first 8 bytes of the hash to ulong for comparison
                    ulong checksum = BitConverter.ToUInt64(hash.AsSpan().Slice(0, 8));
                    checksums[chunk.PathHash] = checksum;
                }
            });

            return checksums;
        }
    }
}
