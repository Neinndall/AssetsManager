using System;
using System.Collections.Generic;
using System.IO;
using AssetsManager.Services.Hashes;
using System.IO.Hashing;
using System.Linq;
using Serilog;
using System.Threading.Tasks;
using LeagueToolkit.Core.Wad;
using AssetsManager.Views.Models;
using AssetsManager.Services.Core;

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

        public async Task CompareSingleWadAsync(string oldWadFile, string newWadFile)
        {
            List<ChunkDiff> allDiffs = new List<ChunkDiff>();
            string oldDir = Path.GetDirectoryName(oldWadFile);
            string newDir = Path.GetDirectoryName(newWadFile);

            try
            {
                _logService.Log($"Starting WAD comparison for a single file: {Path.GetFileName(oldWadFile)}");
                NotifyComparisonStarted(1);

                bool success = true;
                string errorMessage = null;

                if (File.Exists(oldWadFile) && File.Exists(newWadFile))
                {
                    var relativePath = Path.GetFileName(oldWadFile);
                    Log.Information($"Comparing {relativePath}...");
                    using var oldWad = new WadFile(oldWadFile);
                    using var newWad = new WadFile(newWadFile);

                    var diffs = await CollectDiffsAsync(oldWad, newWad, relativePath);
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
            catch (Exception ex)
            {
                _logService.LogError(ex, "An error occurred during single WAD comparison.");
            }
            finally
            {
                NotifyComparisonCompleted(allDiffs, oldDir, newDir);
                if (allDiffs != null)
                {
                    _logService.LogSuccess($"Single WAD comparison completed. Found {allDiffs.Count} differences.");
                }
                else
                {
                    _logService.LogError("Single WAD comparison completed with errors.");
                }
            }
        }

        public async Task CompareWadsAsync(string oldDir, string newDir)
        {
            List<ChunkDiff> allDiffs = new List<ChunkDiff>();
            try
            {
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
                    var relativePath = Path.GetRelativePath(oldDir, oldWadFile);
                    var newWadFileFullPath = Path.Combine(newDir, relativePath);

                    processedFiles++;
                    bool success = true;
                    string errorMessage = null;

                    if (File.Exists(newWadFileFullPath))
                    {
                        Log.Information($"Comparing {relativePath}...");
                        using var oldWad = new WadFile(oldWadFile);
                        using var newWad = new WadFile(newWadFileFullPath);

                        var diffs = await CollectDiffsAsync(oldWad, newWad, relativePath);
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
            catch (Exception ex)
            {
                _logService.LogError(ex, "An error occurred during WAD comparison.");
            }
            finally
            {
                NotifyComparisonCompleted(allDiffs, oldDir, newDir);
                if (allDiffs != null)
                {
                    _logService.LogSuccess($"WADs comparison completed. Found {allDiffs.Count} differences.");
                }
                else
                {
                    _logService.LogError("WADs comparison completed with errors.");
                }
            }
        }

        private async Task<List<ChunkDiff>> CollectDiffsAsync(WadFile oldWad, WadFile newWad, string sourceWadFile)
        {
            var diffs = new List<ChunkDiff>();

            var oldChunks = oldWad.Chunks.ToDictionary(c => c.Key, c => c.Value);
            var newChunks = newWad.Chunks.ToDictionary(c => c.Key, c => c.Value);

            var oldChunkChecksums = await GetChunkChecksumsAsync(oldWad, oldChunks.Values);
            var newChunkChecksums = await GetChunkChecksumsAsync(newWad, newChunks.Values);

            // Removed and Modified
            foreach (var oldChunk in oldChunks.Values)
            {
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

        private async Task<Dictionary<ulong, ulong>> GetChunkChecksumsAsync(WadFile wadFile, IEnumerable<WadChunk> chunks)
        {
            var checksums = new Dictionary<ulong, ulong>();

            await Task.Run(() =>
            {
                foreach (var chunk in chunks)
                {
                    using var decompressedChunk = wadFile.LoadChunkDecompressed(chunk);
                    var checksum = System.IO.Hashing.XxHash64.HashToUInt64(decompressedChunk.Span);
                    checksums[chunk.PathHash] = checksum;
                }
            });

            return checksums;
        }
    }
}
