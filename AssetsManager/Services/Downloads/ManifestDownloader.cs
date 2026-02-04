using System;
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
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Core;
using Microsoft.Win32.SafeHandles;

namespace AssetsManager.Services.Downloads;

public class ManifestDownloader
{
    private readonly HttpClient _httpClient;
    private readonly LogService _logService;
    private readonly string _bundleBaseUrl = "https://lol.dyn.riotcdn.net/channels/public/bundles";
    private readonly HashService _hashService = new HashService();
    
    // Pools de recursos reutilizables
    private readonly ConcurrentStack<Decompressor> _decompressorPool = new ConcurrentStack<Decompressor>();
    private readonly ConcurrentBag<byte[]> _skipBufferPool = new ConcurrentBag<byte[]>();
    private readonly ConcurrentBag<byte[]> _compBufferPool = new ConcurrentBag<byte[]>();

    public event Action<string, string, int, int> ProgressChanged;

    public ManifestDownloader(HttpClient httpClient, LogService logService)
    {
        _httpClient = httpClient;
        _logService = logService;
        
        // Inicializar pools
        for (int i = 0; i < 4; i++) _decompressorPool.Push(new Decompressor());
        for (int i = 0; i < 16; i++) _skipBufferPool.Add(new byte[64 * 1024]);
        for (int i = 0; i < 16; i++) _compBufferPool.Add(new byte[256 * 1024]);
    }

    private class ChunkDownloadTask
    {
        public RmanChunk Chunk { get; set; }
        public ulong FileOffset { get; set; }
        public RmanFile FileInfo { get; set; }
        public string FullPath { get; set; }
    }

    private class FilePatchTask
    {
        public RmanFile FileInfo { get; set; }
        public string FullPath { get; set; }
        public Dictionary<ulong, List<ChunkDownloadTask>> ChunksByBundle { get; set; }
    }

    private class UniqueChunkTask
    {
        public RmanChunk Chunk { get; set; }
        public List<TargetInfo> Targets { get; set; }
    }

    private class TargetInfo
    {
        public string FullPath { get; set; }
        public ulong FileOffset { get; set; }
        public RmanFile FileInfo { get; set; }
    }

    public async Task<int> DownloadManifestAsync(RmanManifest manifest, string outputPath, int maxThreads, string filter = null, List<string> langs = null)
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

        if (!Directory.Exists(outputPath)) Directory.CreateDirectory(outputPath);

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
        var lastProgressTime = DateTime.MinValue;

        _logService.Log($"[Verification] Starting analysis of {totalToVerify} files...");

        await Task.Run(async () =>
        {
            var scanSemaphore = new SemaphoreSlim(2);
            var pool = System.Buffers.ArrayPool<byte>.Shared;

            var scanTasks = filteredFiles.Select(async file =>
            {
                await scanSemaphore.WaitAsync();
                try 
                {
                    var fullPath = Path.Combine(outputPath, file.Name.Replace("/", Path.DirectorySeparatorChar.ToString()));
                    var fileInfo = new FileInfo(fullPath);
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
                                chunksByBundle[chunk.BundleId].Add(new ChunkDownloadTask { Chunk = chunk, FileOffset = currentFileOffset, FileInfo = file, FullPath = fullPath });
                                currentFileOffset += chunk.UncompressedSize;
                            }
                        }
                    }
                    else
                    {
                        using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 256 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous))
                        {
                            foreach (var chunkId in file.ChunkIds)
                            {
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
                                            int read = await fs.ReadAsync(localData, totalRead, (int)chunk.UncompressedSize - totalRead);
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
                                    chunksByBundle[chunk.BundleId].Add(new ChunkDownloadTask { Chunk = chunk, FileOffset = currentFileOffset, FileInfo = file, FullPath = fullPath });
                                }
                                currentFileOffset += chunk.UncompressedSize;
                            }
                        }
                    }

                    if (chunksByBundle.Any()) 
                    {
                        filesToPatch.Add(new FilePatchTask { FileInfo = file, FullPath = fullPath, ChunksByBundle = chunksByBundle });
                        Interlocked.Add(ref totalChunksToDownloadCount, chunksByBundle.Values.Sum(l => l.Count));
                        Interlocked.Add(ref totalMBToDownload, chunksByBundle.Values.SelectMany(l => l).Sum(c => (long)c.Chunk.CompressedSize));
                    }
                    else Interlocked.Increment(ref alreadyCorrect);

                    int completed = Interlocked.Increment(ref currentVerify);
                    var now = DateTime.Now;
                    if ((now - lastProgressTime).TotalMilliseconds >= 100 || completed == totalToVerify)
                    {
                        lastProgressTime = now;
                        // Use the professional format "File X of Y: filename" to enable dual-progress view
                        ProgressChanged?.Invoke("Verifying", $"{completed} of {totalToVerify} files: {file.Name}", completed, totalToVerify);
                    }
                }
                finally { scanSemaphore.Release(); }
            });
            await Task.WhenAll(scanTasks);
        });

        verifyStopwatch.Stop();
        double verifyMB = totalMBToDownload / 1024.0 / 1024.0;
        _logService.Log($"[Verification] Finished in {verifyStopwatch.Elapsed.TotalSeconds:F1}s.");
        _logService.Log($"  • Files OK: {alreadyCorrect}");
        _logService.Log($"  • Files to patch: {filesToPatch.Count}");
        _logService.Log($"  • Chunks to download: {totalChunksToDownloadCount:N0}");
        _logService.Log($"  • Estimated download: {verifyMB:F2} MB (compressed)");

        if (!filesToPatch.Any()) return 0;

        // ===========================================================================
        // PHASE 2: GLOBAL UPDATE (STREAMING & DEDUPLICATED)
        // ===========================================================================
        var allTasks = filesToPatch.SelectMany(f => f.ChunksByBundle.Values.SelectMany(l => l)).ToList();
        var uniqueChunks = allTasks.GroupBy(t => t.Chunk.ChunkId)
                                   .Select(g => new UniqueChunkTask { 
                                       Chunk = g.First().Chunk, 
                                       Targets = g.Select(t => new TargetInfo { FullPath = t.FullPath, FileOffset = t.FileOffset, FileInfo = t.FileInfo }).ToList() 
                                   }).ToList();

        var bundlesToProcess = uniqueChunks.GroupBy(c => c.Chunk.BundleId).ToDictionary(g => g.Key, g => g.ToList());
        
        int totalChunks = allTasks.Count;
        int completedChunks = 0;
        int completedFilesCount = 0;
        int totalFilesToPatch = filesToPatch.Count;
        long totalDownloaded = 0;
        long wastedBytes = 0;
        int totalRequests = 0;
        long usefulBytes = allTasks.Sum(c => (long)c.Chunk.CompressedSize);
        long totalDecompressedBytes = 0;
        
        var updateSw = System.Diagnostics.Stopwatch.StartNew();
        var openHandles = new ConcurrentDictionary<string, SafeFileHandle>();
        var pendingPerFile = new ConcurrentDictionary<string, int>(filesToPatch.ToDictionary(f => f.FullPath, f => f.ChunksByBundle.Values.Sum(l => l.Count)));

        try
        {
            var netSem = new SemaphoreSlim(8); 
            var cpuSem = new SemaphoreSlim(Environment.ProcessorCount);

            var tasks = bundlesToProcess.Select(async bundleEntry =>
            {
                await netSem.WaitAsync();
                try
                {
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
                        long start = (long)group[0].Chunk.BundleOffset;
                        long end = (long)(group.Last().Chunk.BundleOffset + group.Last().Chunk.CompressedSize - 1);

                        var req = new HttpRequestMessage(HttpMethod.Get, url);
                        req.Headers.Range = new RangeHeaderValue(start, end);
                        
                        using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                        resp.EnsureSuccessStatusCode();
                        using var responseStream = await resp.Content.ReadAsStreamAsync();
                        long currentStreamPos = start;

                        foreach (var t in group)
                        {
                            long gap = (long)t.Chunk.ChunkId == 0 ? 0 : (long)t.Chunk.BundleOffset - currentStreamPos; // Placeholder fix for gap logic
                            if (gap > 0)
                            {
                                byte[] skipBuf = _skipBufferPool.TryTake(out var b) ? b : new byte[64 * 1024];
                                long skipped = 0;
                                while (skipped < gap)
                                {
                                    int read = await responseStream.ReadAsync(skipBuf, 0, (int)Math.Min(gap - skipped, skipBuf.Length));
                                    if (read == 0) break;
                                    skipped += read;
                                }
                                Interlocked.Add(ref wastedBytes, skipped);
                                _skipBufferPool.Add(skipBuf);
                            }

                            byte[] comp = new byte[t.Chunk.CompressedSize];
                            int tRead = 0;
                            while (tRead < (int)t.Chunk.CompressedSize)
                            {
                                int r = await responseStream.ReadAsync(comp, tRead, (int)t.Chunk.CompressedSize - tRead);
                                if (r == 0) break;
                                tRead += r;
                            }
                            Interlocked.Add(ref totalDownloaded, tRead + (gap > 0 ? gap : 0));
                            currentStreamPos = (long)t.Chunk.BundleOffset + tRead;

                            await cpuSem.WaitAsync();
                            try {
                                if (!_decompressorPool.TryPop(out var decompressor)) decompressor = new Decompressor();
                                try {
                                    var uncomp = decompressor.Unwrap(comp.AsSpan(0, tRead)).ToArray();
                                    Interlocked.Add(ref totalDecompressedBytes, (long)uncomp.Length);

                                    foreach (var target in t.Targets)
                                    {
                                        var handle = openHandles.GetOrAdd(target.FullPath, (path) => {
                                            var dir = Path.GetDirectoryName(path);
                                            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                                            var h = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.Asynchronous);
                                            if ((ulong)RandomAccess.GetLength(h) != target.FileInfo.FileSize) RandomAccess.SetLength(h, (long)target.FileInfo.FileSize);
                                            return h;
                                        });

                                        RandomAccess.Write(handle, uncomp, (long)target.FileOffset);
                                        int doneChunks = Interlocked.Increment(ref completedChunks);
                                        int rem = pendingPerFile.AddOrUpdate(target.FullPath, 0, (k, v) => v - 1);
                                        if (rem == 0) {
                                            Interlocked.Increment(ref completedFilesCount);
                                            if (openHandles.TryRemove(target.FullPath, out var hnd)) hnd.Dispose();
                                        }
                                        // Use the professional format "File X of Y: filename"
                                        ProgressChanged?.Invoke("Updating", $"{completedFilesCount} of {totalFilesToPatch} files: {Path.GetFileName(target.FullPath)}", doneChunks, totalChunks);
                                    }
                                } finally { _decompressorPool.Push(decompressor); }
                            } finally { cpuSem.Release(); }
                        }
                    }
                }
                catch (Exception ex) { _logService.LogWarning($"Bundle {bundleEntry.Key:X16} error: {ex.Message}"); }
                finally { netSem.Release(); }
            });
            await Task.WhenAll(tasks);
        }
        finally { foreach (var h in openHandles.Values) h.Dispose(); }

        double sec = updateSw.Elapsed.TotalSeconds;
        double efficiency = (double)usefulBytes / (totalDownloaded > 0 ? totalDownloaded : 1) * 100;
        double wastedMB = wastedBytes / 1024.0 / 1024.0;
        double usefulSpeed = (usefulBytes / 1024.0 / 1024.0) / (sec > 0 ? sec : 1);
        double actualSpeed = (totalDownloaded / 1024.0 / 1024.0) / (sec > 0 ? sec : 1);
        double decompressedGB = totalDecompressedBytes / 1024.0 / 1024.0 / 1024.0;
        double compressionRatio = (double)totalDecompressedBytes / (usefulBytes > 0 ? usefulBytes : 1);
        
        _logService.LogSuccess($"[Phase 2] Completed in {sec:F1}s");
        _logService.Log($"  • HTTP Requests: {totalRequests}");
        _logService.Log($"  • Useful data: {usefulBytes / 1024.0 / 1024.0:F2} MB");
        _logService.Log($"  • Wasted (gaps): {wastedMB:F2} MB ({((double)wastedBytes / (totalDownloaded > 0 ? totalDownloaded : 1)) * 100:F1}%)");
        _logService.Log($"  • Efficiency: {efficiency:F1}% (256KB gap strategy)");
        _logService.Log($"  • Useful Speed: {usefulSpeed:F2} MB/s");
        _logService.Log($"  • Actual Speed: {actualSpeed:F2} MB/s");
        _logService.Log($"  • Decompressed: {decompressedGB:F2} GB (ratio {compressionRatio:F2}x)");
        
        return totalFilesToPatch;
    }
}