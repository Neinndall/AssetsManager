using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using ZstdSharp;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using Microsoft.Win32.SafeHandles;

namespace AssetsManager.Services.Monitor;

public class ManifestDownloader
{
    private readonly HttpClient _httpClient;
    private readonly LogService _logService;
    private readonly DirectoriesCreator _directoriesCreator;
    private readonly HashService _hashService;
    private readonly string _bundleBaseUrl = "https://lol.dyn.riotcdn.net/channels/public/bundles";
    
    // Pools de recursos reutilizables
    private readonly ConcurrentStack<Decompressor> _decompressorPool = new ConcurrentStack<Decompressor>();

    public event Action<string, string, int, int> ProgressChanged;

    public ManifestDownloader(HttpClient httpClient, LogService logService, DirectoriesCreator directoriesCreator, HashService hashService)
    {
        _httpClient = httpClient;
        _logService = logService;
        _directoriesCreator = directoriesCreator;
        _hashService = hashService;
        
        // Inicializar pools
        for (int i = 0; i < 4; i++) _decompressorPool.Push(new Decompressor());
    }

    private class ChunkDownloadTask
    {
        public RmanChunk Chunk { get; set; }
        public ulong FileOffset { get; set; }
        public RmanFile FileInfo { get; set; }
        public string PhysicalPath { get; set; }
    }

    private class FilePatchTask
    {
        public RmanFile FileInfo { get; set; }
        public string PhysicalPath { get; set; }
        public Dictionary<ulong, List<ChunkDownloadTask>> ChunksByBundle { get; set; }
    }

    private class UniqueChunkTask
    {
        public RmanChunk Chunk { get; set; }
        public List<TargetInfo> Targets { get; set; }
    }

    private class TargetInfo
    {
        public string PhysicalPath { get; set; }
        public ulong FileOffset { get; set; }
        public RmanFile FileInfo { get; set; }
    }

    public async Task<int> DownloadManifestAsync(RmanManifest manifest, string outputPath, string filter = null, List<string> langs = null, CancellationToken cancellationToken = default)
    {
        var regex = !string.IsNullOrEmpty(filter) ? new Regex(filter, RegexOptions.IgnoreCase) : null;
        var selectedLangIds = new HashSet<byte>();

        if (langs != null && langs.Any())
        {
            foreach (var langName in langs)
            {
                var lang = manifest.Languages.FirstOrDefault(l => l.Name.Equals(langName, StringComparison.OrdinalIgnoreCase));
                if (lang != null) selectedLangIds.Add(lang.LanguageId);
            }
        }

        var filteredFiles = manifest.Files.Where(file => 
        {
            if (regex != null && !regex.IsMatch(file.Name)) return false;
            if (selectedLangIds.Count > 0)
            {
                bool isNeutral = file.LanguageIds.Count == 0;
                bool matchesLang = file.LanguageIds.Any(id => selectedLangIds.Contains(id));
                if (!isNeutral && !matchesLang) return false;
            }
            return true;
        }).ToList();

        _directoriesCreator.CreateDirectory(outputPath);

        // ===========================================================================
        // VERIFICATION PROCESS (SMART SCAN ENGINE)
        // ===========================================================================

        var filesToPatch = new ConcurrentBag<FilePatchTask>();
        long totalChunksToDownloadCount = 0;
        long totalMBToDownload = 0;
        int alreadyCorrect = 0;
        var verifyStopwatch = System.Diagnostics.Stopwatch.StartNew();

        // --- FASE 1: FILTRADO Y SELECCIÓN DE OBJETIVOS ---
        int totalToVerify = filteredFiles.Count;
        int currentVerify = 0;
        int lastReportedVerify = 0;
        var lastProgressTime = DateTime.MinValue;
        var verifyLock = new object();

        _logService.Log($"[Verification] Starting analysis of {totalToVerify} files...");

        try
        {
            await Task.Run(async () =>
            {
                var scanSemaphore = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount, 1, 2));
                var pool = System.Buffers.ArrayPool<byte>.Shared;

                var scanTasks = filteredFiles.Select(async file =>
                {
                    await scanSemaphore.WaitAsync(cancellationToken);
                    try 
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var physicalPath = Path.Combine(outputPath, file.Name.Replace("/", Path.DirectorySeparatorChar.ToString()));
                        var fileInfo = new FileInfo(physicalPath);
                        bool fileExists = fileInfo.Exists;
                        var chunksByBundle = new Dictionary<ulong, List<ChunkDownloadTask>>();
                        ulong currentFileOffset = 0;
                        ulong currentFileLength = fileExists ? (ulong)fileInfo.Length : 0;

                        if (!fileExists)
                        {
                            foreach (var chunkId in file.ChunkIds)
                            {
                                var chunk = manifest.GetChunk(chunkId);
                                if (chunk != null)
                                {
                                    if (!chunksByBundle.ContainsKey(chunk.BundleId)) chunksByBundle[chunk.BundleId] = new List<ChunkDownloadTask>();
                                    chunksByBundle[chunk.BundleId].Add(new ChunkDownloadTask { Chunk = chunk, FileOffset = currentFileOffset, FileInfo = file, PhysicalPath = physicalPath });
                                    currentFileOffset += chunk.UncompressedSize;
                                }
                            }
                        }
                        else
                        {
                            using (var fs = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 256 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous))
                            {
                                foreach (var chunkId in file.ChunkIds)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    var chunk = manifest.GetChunk(chunkId);
                                    if (chunk == null) continue;
                                    bool needsUpdate = true;
                                    if (currentFileLength >= currentFileOffset + chunk.UncompressedSize)
                                    {
                                        byte[] localData = pool.Rent((int)chunk.UncompressedSize);
                                        try
                                        {
                                            int totalRead = 0;
                                            while (totalRead < (int)chunk.UncompressedSize)
                                            {
                                                int read = await fs.ReadAsync(localData, totalRead, (int)chunk.UncompressedSize - totalRead, cancellationToken);
                                                if (read == 0) break;
                                                totalRead += read;
                                            }
                                            if (totalRead == (int)chunk.UncompressedSize && _hashService.VerifyChunk(localData.AsSpan(0, totalRead), chunk.ChunkId, file.HashType)) 
                                                needsUpdate = false;
                                        }
                                        finally { pool.Return(localData); }
                                    }
                                    if (needsUpdate)
                                    {
                                        if (!chunksByBundle.ContainsKey(chunk.BundleId)) chunksByBundle[chunk.BundleId] = new List<ChunkDownloadTask>();
                                        chunksByBundle[chunk.BundleId].Add(new ChunkDownloadTask { Chunk = chunk, FileOffset = currentFileOffset, FileInfo = file, PhysicalPath = physicalPath });
                                    }
                                    currentFileOffset += chunk.UncompressedSize;
                                }
                            }
                        }

                        if (chunksByBundle.Any()) 
                        {
                            filesToPatch.Add(new FilePatchTask { FileInfo = file, PhysicalPath = physicalPath, ChunksByBundle = chunksByBundle });
                            Interlocked.Add(ref totalChunksToDownloadCount, chunksByBundle.Values.Sum(l => l.Count));
                            Interlocked.Add(ref totalMBToDownload, chunksByBundle.Values.SelectMany(l => l).Sum(c => (long)c.Chunk.CompressedSize));
                        }
                        else Interlocked.Increment(ref alreadyCorrect);

                        int completed = Interlocked.Increment(ref currentVerify);
                        var now = DateTime.Now;
                        lock (verifyLock)
                        {
                            if (completed > lastReportedVerify)
                            {
                                if ((now - lastProgressTime).TotalMilliseconds >= 100 || completed == totalToVerify)
                                {
                                    lastProgressTime = now;
                                    lastReportedVerify = completed;
                                    ProgressChanged?.Invoke("Verifying", $"{completed} of {totalToVerify} files: {file.Name}", completed, totalToVerify);
                                }
                            }
                        }
                    }
                    finally { scanSemaphore.Release(); }
                });
                await Task.WhenAll(scanTasks);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logService.LogWarning("Verification process was cancelled.");
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();

        verifyStopwatch.Stop();

        double verifyMB = totalMBToDownload / 1024.0 / 1024.0;
        _logService.Log($"[Verification] Finished in {verifyStopwatch.Elapsed.TotalSeconds:F1}s.");
        _logService.Log($"  • Files OK: {alreadyCorrect}");
        _logService.Log($"  • Files to patch: {filesToPatch.Count}");
        _logService.Log($"  • Chunks to download: {totalChunksToDownloadCount:N0}");
        _logService.Log($"  • Estimated download: {verifyMB:F2} MB (compressed)");

        if (System.Windows.Application.Current != null)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.SystemIdle);
        }

        // Wait 500ms so the user can see the 100% verification progress bar and the final file count
        await Task.Delay(500, cancellationToken);

        if (!filesToPatch.Any()) return 0;

        // Sequential UI Reporting: Sort files alphabetically to follow manifest/folder order
        var filesToPatchList = filesToPatch.OrderBy(f => f.FileInfo.Name).ToList();
        var initialChunksPerFile = filesToPatchList.ToDictionary(f => f.PhysicalPath, f => f.ChunksByBundle.Values.Sum(l => l.Count));
        var pathToIndex = filesToPatchList.Select((f, i) => new { f.PhysicalPath, i }).ToDictionary(x => x.PhysicalPath, x => x.i);

        // ===========================================================================
        // PHASE 2: GLOBAL UPDATE (STREAMING & DEDUPLICATED)
        // ===========================================================================
        var allTasks = filesToPatchList.SelectMany(f => f.ChunksByBundle.Values.SelectMany(l => l)).ToList();
        int totalChunks = allTasks.Count;

        // Reset progress bar instantly for the start of the Updating phase (0%)
        ProgressChanged?.Invoke("Updating", $"0 of {filesToPatchList.Count} files: Initializing...", 0, totalChunks);

        if (System.Windows.Application.Current != null)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.SystemIdle);
        }

        // Wait 200ms to allow the UI thread to paint the 0% "Initializing..." frame before chunk downloads start
        await Task.Delay(200, cancellationToken);

        var uniqueChunks = allTasks.GroupBy(t => t.Chunk.ChunkId)
                                   .Select(g => new UniqueChunkTask { 
                                       Chunk = g.First().Chunk, 
                                       Targets = g.Select(t => new TargetInfo { PhysicalPath = t.PhysicalPath, FileOffset = t.FileOffset, FileInfo = t.FileInfo }).ToList() 
                                   }).ToList();

        // Priority Download: Order bundles by the first file they complete in our sorted list
        var bundlesToProcess = uniqueChunks
            .GroupBy(c => c.Chunk.BundleId)
            .Select(g => new { 
                Id = g.Key, 
                Chunks = g.ToList(), 
                Priority = g.Min(c => c.Targets.Min(t => pathToIndex[t.PhysicalPath])) 
            })
            .OrderBy(x => x.Priority)
            .ToDictionary(x => x.Id, x => x.Chunks);

        int completedChunks = 0;
        int totalFilesToPatch = filesToPatchList.Count;
        int visualFileIndex = 0;
        int lastReportedChunks = -1;
        var uiLock = new object();

        long totalDownloaded = 0;
        long wastedBytes = 0;
        int totalRequests = 0;
        long usefulBytes = allTasks.Sum(c => (long)c.Chunk.CompressedSize);
        long totalDecompressedBytes = 0;

        var updateSw = System.Diagnostics.Stopwatch.StartNew();
        var openHandles = new ConcurrentDictionary<string, Lazy<SafeFileHandle>>();
        var pendingPerFile = new ConcurrentDictionary<string, int>(initialChunksPerFile);

        try
        {
            var netSem = new SemaphoreSlim(10); 
            var cpuSem = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount, 1, 4));

            var tasks = bundlesToProcess.Select(async bundleEntry =>
            {
                await netSem.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string url = $"{_bundleBaseUrl}/{bundleEntry.Key:X16}.bundle";
                    var sorted = bundleEntry.Value.OrderBy(t => t.Chunk.BundleOffset).ToList();

                    const uint MaxGap = 256 * 1024;
                    var groups = new List<List<UniqueChunkTask>>();
                    if (sorted.Count > 0)
                    {
                        var currentGroup = new List<UniqueChunkTask> { sorted[0] };
                        for (int i = 1; i < sorted.Count; i++)
                        {
                            uint gap = (uint)(sorted[i].Chunk.BundleOffset - (sorted[i - 1].Chunk.BundleOffset + sorted[i - 1].Chunk.CompressedSize));
                            if (gap <= MaxGap) currentGroup.Add(sorted[i]);
                            else { groups.Add(currentGroup); currentGroup = new List<UniqueChunkTask> { sorted[i] }; }
                        }
                        groups.Add(currentGroup);
                    }

                    Interlocked.Add(ref totalRequests, groups.Count);
                    foreach (var group in groups)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        long start = (long)group[0].Chunk.BundleOffset;
                        long end = (long)(group.Last().Chunk.BundleOffset + group.Last().Chunk.CompressedSize - 1);

                        var req = new HttpRequestMessage(HttpMethod.Get, url);
                        req.Headers.Range = new RangeHeaderValue(start, end);

                        using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                        resp.EnsureSuccessStatusCode();

                        using var responseStream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                        long currentStreamPos = start;

                        foreach (var t in group)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            long gap = (long)t.Chunk.ChunkId == 0 ? 0 : (long)t.Chunk.BundleOffset - currentStreamPos; 
                            if (gap > 0)
                            {
                                int skipSize = 64 * 1024;
                                byte[] skipBuf = ArrayPool<byte>.Shared.Rent(skipSize);
                                try
                                {
                                    long skipped = 0;
                                    while (skipped < gap)
                                    {
                                        int read = await responseStream.ReadAsync(skipBuf, 0, (int)Math.Min(gap - skipped, skipSize), cancellationToken);
                                        if (read == 0) break;
                                        skipped += read;
                                    }
                                    Interlocked.Add(ref wastedBytes, skipped);
                                }
                                finally
                                {
                                    ArrayPool<byte>.Shared.Return(skipBuf);
                                }
                            }

                            byte[] comp = ArrayPool<byte>.Shared.Rent((int)t.Chunk.CompressedSize);
                            int tRead = 0;
                            try
                            {
                                while (tRead < (int)t.Chunk.CompressedSize)
                                {
                                    int r = await responseStream.ReadAsync(comp, tRead, (int)t.Chunk.CompressedSize - tRead, cancellationToken);
                                    if (r == 0) break;
                                    tRead += r;
                                }
                                Interlocked.Add(ref totalDownloaded, tRead + (gap > 0 ? gap : 0));
                                currentStreamPos = (long)t.Chunk.BundleOffset + tRead;

                                await cpuSem.WaitAsync(cancellationToken);
                                try {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    if (!_decompressorPool.TryPop(out var decompressor)) decompressor = new Decompressor();
                                    try {
                                        byte[] decompBuffer = ArrayPool<byte>.Shared.Rent((int)t.Chunk.UncompressedSize);
                                        try {
                                            int decompressedBytes = decompressor.Unwrap(comp.AsSpan(0, tRead), decompBuffer.AsSpan(0, (int)t.Chunk.UncompressedSize));
                                            if (decompressedBytes != (int)t.Chunk.UncompressedSize)
                                                throw new Exception($"Chunk decompression size mismatch. Expected {t.Chunk.UncompressedSize}, got {decompressedBytes}");

                                            Interlocked.Add(ref totalDecompressedBytes, (long)decompressedBytes);
                                            ReadOnlySpan<byte> uncomp = decompBuffer.AsSpan(0, decompressedBytes);

                                            foreach (var target in t.Targets)
                                            {
                                                var lazyHandle = openHandles.GetOrAdd(target.PhysicalPath, (path) => new Lazy<SafeFileHandle>(() => {
                                                    var dir = Path.GetDirectoryName(path);
                                                    if (!string.IsNullOrEmpty(dir)) _directoriesCreator.CreateDirectory(dir);
                                                    var h = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.Asynchronous);
                                                    if ((ulong)RandomAccess.GetLength(h) != target.FileInfo.FileSize) RandomAccess.SetLength(h, (long)target.FileInfo.FileSize);
                                                    return h;
                                                }, LazyThreadSafetyMode.ExecutionAndPublication));

                                                var handle = lazyHandle.Value;
                                                RandomAccess.Write(handle, uncomp, (long)target.FileOffset);
                                                
                                                int currentDoneChunks = Interlocked.Increment(ref completedChunks);
                                                int rem = pendingPerFile.AddOrUpdate(target.PhysicalPath, 0, (k, v) => v - 1);

                                                if (rem == 0) {
                                                    if (openHandles.TryRemove(target.PhysicalPath, out var lazyHnd)) {
                                                        if (lazyHnd.IsValueCreated) lazyHnd.Value.Dispose();
                                                    }
                                                }

                                                // UI Coordination: Thread-safe sequential focus with detailed progress
                                                lock (uiLock)
                                                {
                                                    while (visualFileIndex < filesToPatchList.Count && 
                                                           pendingPerFile.TryGetValue(filesToPatchList[visualFileIndex].PhysicalPath, out int p) && p == 0)
                                                    {
                                                        visualFileIndex++;
                                                    }

                                                    if (currentDoneChunks > lastReportedChunks)
                                                    {
                                                        lastReportedChunks = currentDoneChunks;
                                                        int reportIndex = Math.Min(visualFileIndex, totalFilesToPatch - 1);
                                                        var reportFile = filesToPatchList[reportIndex];

                                                        pendingPerFile.TryGetValue(reportFile.PhysicalPath, out int pending);
                                                        int totalForFile = initialChunksPerFile[reportFile.PhysicalPath];
                                                        int doneForFile = totalForFile - pending;

                                                        string message = $"{Math.Min(visualFileIndex + 1, totalFilesToPatch)} of {totalFilesToPatch} files: {reportFile.FileInfo.Name}|{doneForFile}/{totalForFile}";
                                                        ProgressChanged?.Invoke("Updating", message, currentDoneChunks, totalChunks);
                                                    }
                                                }
                                            }
                                        } finally { ArrayPool<byte>.Shared.Return(decompBuffer); }
                                    } finally { _decompressorPool.Push(decompressor); }
                                } finally { cpuSem.Release(); }
                            } finally { ArrayPool<byte>.Shared.Return(comp); }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _logService.LogError(ex, $"Bundle {bundleEntry.Key:X16} processing error"); }
                finally { netSem.Release(); }
            });
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            _logService.LogWarning("Updating process was cancelled.");
            throw;
        }
        finally { foreach (var lazyHnd in openHandles.Values) { if (lazyHnd.IsValueCreated) lazyHnd.Value.Dispose(); } }

        double sec = updateSw.Elapsed.TotalSeconds;
        double efficiency = (double)usefulBytes / (totalDownloaded > 0 ? totalDownloaded : 1) * 100;
        double wastedMB = wastedBytes / 1024.0 / 1024.0;
        double usefulSpeed = (usefulBytes / 1024.0 / 1024.0) / (sec > 0 ? sec : 1);
        double actualSpeed = (totalDownloaded / 1024.0 / 1024.0) / (sec > 0 ? sec : 1);
        double decompressedGB = totalDecompressedBytes / 1024.0 / 1024.0 / 1024.0;
        double compressionRatio = (double)totalDecompressedBytes / (usefulBytes > 0 ? usefulBytes : 1);
        
        _logService.LogSuccess($"[Updating] Completed in {sec:F1}s");
        _logService.LogDebug($"  • HTTP Requests: {totalRequests}");
        _logService.LogDebug($"  • Useful data: {usefulBytes / 1024.0 / 1024.0:F2} MB");
        _logService.LogDebug($"  • Wasted (gaps): {wastedMB:F2} MB ({((double)wastedBytes / (totalDownloaded > 0 ? totalDownloaded : 1)) * 100:F1}%)");
        _logService.LogDebug($"  • Efficiency: {efficiency:F1}% (256KB gap strategy)");
        _logService.LogDebug($"  • Useful Speed: {usefulSpeed:F2} MB/s");
        _logService.LogDebug($"  • Actual Speed: {actualSpeed:F2} MB/s");
        _logService.LogDebug($"  • Decompressed: {decompressedGB:F2} GB (ratio {compressionRatio:F2}x)");
        
        return totalFilesToPatch;
    }
}