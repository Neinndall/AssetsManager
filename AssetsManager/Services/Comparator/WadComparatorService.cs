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

                _logService.Log("Starting WADs comparison...");
                
                // Notify UI immediately to show activity (Indeterminate spinner)
                NotifyComparisonStarted(0);

                // Phase 1: Fast scanning to find valid pairs
                var scanResult = await Task.Run(() =>
                {
                    var searchPatterns = new[] { "*.wad.client", "*.wad" };
                    var oldWadFiles = searchPatterns
                        .SelectMany(pattern => Directory.GetFiles(oldDir, pattern, SearchOption.AllDirectories))
                        .ToList();

                    var validPairs = new List<(string OldPath, string NewPath, string RelativePath)>();

                    foreach (var oldWadFile in oldWadFiles)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        var relativePath = Path.GetRelativePath(oldDir, oldWadFile);
                        var newWadFileFullPath = Path.Combine(newDir, relativePath);
                        
                        if (File.Exists(newWadFileFullPath))
                        {
                            validPairs.Add((oldWadFile, newWadFileFullPath, relativePath));
                        }
                    }

                    return validPairs;
                }, cancellationToken);

                _totalChunksGlobal = 0;
                _completedChunksGlobal = 0;

                // Phase 2: Parallel Comparison
                var concurrentDiffs = new ConcurrentBag<List<ChunkDiff>>();
                
                await Task.Run(() =>
                {
                    Parallel.ForEach(scanResult, new ParallelOptions 
                    { 
                        MaxDegreeOfParallelism = Environment.ProcessorCount, 
                        CancellationToken = cancellationToken 
                    }, pair =>
                    {
                        var diffs = CollectDiffsInternal(pair.OldPath, pair.NewPath, pair.RelativePath, cancellationToken);
                        concurrentDiffs.Add(diffs);
                    });
                }, cancellationToken);

                foreach (var diffList in concurrentDiffs)
                {
                    allDiffs.AddRange(diffList);
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

        private List<ChunkDiff> CollectDiffsInternal(string oldWadFile, string newWadFile, string sourceWadFile, CancellationToken cancellationToken)
        {
            var diffs = new List<ChunkDiff>();

            try
            {
                using var oldWad = new WadFile(oldWadFile);
                using var newWad = new WadFile(newWadFile);

                // Actualizar total global de forma segura
                Interlocked.Add(ref _totalChunksGlobal, oldWad.Chunks.Count + newWad.Chunks.Count);
                NotifyComparisonStarted(_totalChunksGlobal);

                // Obtenemos checksums usando la lógica rápida (Reflection)
                var oldChunkChecksums = GetChunkChecksumsInternal(oldWad.Chunks.Values, cancellationToken, sourceWadFile);
                var newChunkChecksums = GetChunkChecksumsInternal(newWad.Chunks.Values, cancellationToken, sourceWadFile);

                // COMPARATIVA 1: Detectar Modificados y Eliminados (O(N))
                foreach (var oldChunk in oldWad.Chunks.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    if (!newWad.Chunks.TryGetValue(oldChunk.PathHash, out var newChunk))
                    {
                        var oldPath = _hashResolverService.ResolveHash(oldChunk.PathHash);
                        diffs.Add(new ChunkDiff { Type = ChunkDiffType.Removed, OldChunk = oldChunk, OldPath = oldPath, SourceWadFile = sourceWadFile });
                    }
                    else
                    {
                        if (oldChunkChecksums[oldChunk.PathHash] != newChunkChecksums[oldChunk.PathHash])
                        {
                            var oldPath = _hashResolverService.ResolveHash(oldChunk.PathHash);
                            var newPath = _hashResolverService.ResolveHash(oldChunk.PathHash);
                            diffs.Add(new ChunkDiff { Type = ChunkDiffType.Modified, OldChunk = oldChunk, NewChunk = newChunk, OldPath = oldPath, NewPath = newPath, SourceWadFile = sourceWadFile });
                        }
                    }
                }

                // OPTIMIZACIÓN CRÍTICA: Mapeo de Checksum -> Hash para detección de Renombrados en O(1)
                // Solo incluimos archivos que NO están en el nuevo WAD para evitar falsos positivos de archivos idénticos
                var oldChecksumMap = new Dictionary<ulong, ulong>();
                foreach (var kvp in oldChunkChecksums)
                {
                    if (!newWad.Chunks.ContainsKey(kvp.Key))
                    {
                        // Si hay colisión de checksum (mismo contenido, distinta ruta), el primero gana.
                        oldChecksumMap.TryAdd(kvp.Value, kvp.Key);
                    }
                }

                // COMPARATIVA 2: Detectar Nuevos y Renombrados (O(M))
                foreach (var newChunk in newWad.Chunks.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    if (!oldWad.Chunks.ContainsKey(newChunk.PathHash))
                    {
                        var newPath = _hashResolverService.ResolveHash(newChunk.PathHash);
                        ulong newChecksum = newChunkChecksums[newChunk.PathHash];

                        // Búsqueda O(1) en el mapa de checksums
                        if (newChecksum != 0 && oldChecksumMap.TryGetValue(newChecksum, out ulong oldHash))
                        {
                            var oldPath = _hashResolverService.ResolveHash(oldHash);
                            diffs.Add(new ChunkDiff { 
                                Type = ChunkDiffType.Renamed, 
                                OldChunk = oldWad.Chunks[oldHash], 
                                NewChunk = newChunk, 
                                OldPath = oldPath, 
                                NewPath = newPath, 
                                SourceWadFile = sourceWadFile 
                            });
                        }
                        else
                        {
                            diffs.Add(new ChunkDiff { Type = ChunkDiffType.New, NewChunk = newChunk, NewPath = newPath, SourceWadFile = sourceWadFile });
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logService.LogError(ex, $"Error comparing WAD file: {sourceWadFile}");
            }

            return diffs;
        }

        private static readonly FieldInfo _checksumField = typeof(WadChunk).GetField("_checksum", BindingFlags.NonPublic | BindingFlags.Instance);

        private Dictionary<ulong, ulong> GetChunkChecksumsInternal(IEnumerable<WadChunk> chunks, CancellationToken cancellationToken, string statusMsg)
        {
            var checksums = new Dictionary<ulong, ulong>();

            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ulong checksum = 0;
                if (_checksumField != null)
                {
                    checksum = (ulong)_checksumField.GetValue(chunk);
                }
                
                checksums[chunk.PathHash] = checksum;

                int completed = Interlocked.Increment(ref _completedChunksGlobal);
                // Reportamos progreso de forma equilibrada para no saturar la UI
                if (completed % 1000 == 0 || completed == _totalChunksGlobal)
                {
                    NotifyComparisonProgressChanged(completed, statusMsg, true, null);
                }
            }

            return checksums;
        }

        private async Task<List<ChunkDiff>> CollectDiffsAsync(string oldWadFile, string newWadFile, string sourceWadFile, CancellationToken cancellationToken, string statusMsg = "")
        {
            return await Task.Run(() => CollectDiffsInternal(oldWadFile, newWadFile, sourceWadFile, cancellationToken), cancellationToken);
        }
    }
}
