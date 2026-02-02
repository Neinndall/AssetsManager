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

        // --- PHASE 1: VERIFICATION ---
        var filesToPatch = new ConcurrentBag<FilePatchTask>();
        int totalToVerify = filteredFiles.Count;
        int currentVerify = 0;
        int alreadyCorrect = 0;
        long totalChunksToDownload = 0;
        long totalMBToDownload = 0;
        var verifyStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var lastProgressTime = DateTime.MinValue;

        await Task.Run(async () =>
        {
            // Hilos usados para verificacion: 2 (óptimo para I/O de disco)
            var scanSemaphore = new SemaphoreSlim(2);
            var scanTasks = filteredFiles.Select(async file =>
            {
                await scanSemaphore.WaitAsync();
                try 
                {
                    var fullPath = Path.Combine(outputPath, file.Name.Replace("/", Path.DirectorySeparatorChar.ToString()));
                    bool fileExists = File.Exists(fullPath);
                    var chunksByBundle = new Dictionary<ulong, List<ChunkDownloadTask>>();
                    ulong currentFileOffset = 0;

                    if (!fileExists || (ulong)new FileInfo(fullPath).Length < file.FileSize)
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
                        using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 256 * 1024))
                        {
                            foreach (var chunkId in file.ChunkIds)
                            {
                                var chunk = manifest.GetChunk(chunkId);
                                if (chunk == null) continue;
                                bool needsUpdate = true;
                                if ((ulong)fs.Length >= currentFileOffset + chunk.UncompressedSize)
                                {
                                    var localData = new byte[chunk.UncompressedSize];
                                    fs.Position = (long)currentFileOffset;
                                    
                                    int totalRead = 0;
                                    while (totalRead < (int)chunk.UncompressedSize)
                                    {
                                        int read = await fs.ReadAsync(localData, totalRead, (int)chunk.UncompressedSize - totalRead);
                                        if (read == 0) break;
                                        totalRead += read;
                                    }
                                    
                                    if (totalRead == (int)chunk.UncompressedSize && _hashService.VerifyChunk(localData, chunk.ChunkId, file.HashType)) 
                                        needsUpdate = false;
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
                        
                        int chunksInFile = chunksByBundle.Values.Sum(l => l.Count);
                        long mbInFile = chunksByBundle.Values.SelectMany(l => l).Sum(c => (long)c.Chunk.CompressedSize);
                        Interlocked.Add(ref totalChunksToDownload, chunksInFile);
                        Interlocked.Add(ref totalMBToDownload, mbInFile);
                    }
                    else Interlocked.Increment(ref alreadyCorrect);

                    int completed = Interlocked.Increment(ref currentVerify);
                    var now = DateTime.Now;
                    if ((now - lastProgressTime).TotalMilliseconds >= 100 || completed == totalToVerify)
                    {
                        lastProgressTime = now;
                        ProgressChanged?.Invoke("Verifying", file.Name, completed, totalToVerify);
                    }
                }
                finally { scanSemaphore.Release(); }
            });
            await Task.WhenAll(scanTasks);
        });

        verifyStopwatch.Stop();
        double verifyMB = totalMBToDownload / 1024.0 / 1024.0;
        _logService.Log($"[Phase 1] Verification finished in {verifyStopwatch.Elapsed.TotalSeconds:F1}s.");
        _logService.Log($"  • Files OK: {alreadyCorrect}");
        _logService.Log($"  • Files to patch: {filesToPatch.Count}");
        _logService.Log($"  • Chunks to download: {totalChunksToDownload:N0}");
        _logService.Log($"  • Estimated download: {verifyMB:F2} MB (compressed)");
        
        if (verifyMB > 1024)
            _logService.Log($"  • That's ~{verifyMB / 1024.0:F2} GB - grab a coffee! ☕");
        else if (verifyMB > 100)
            _logService.Log($"  • Medium patch - should be quick! ⚡");

        if (!filesToPatch.Any()) return 0;

        // --- PHASE 2: GLOBAL UPDATE (STREAMING) ---
        var allChunks = filesToPatch.SelectMany(f => f.ChunksByBundle.Values.SelectMany(l => l)).ToList();
        var bundlesToProcess = allChunks.GroupBy(c => c.Chunk.BundleId).ToDictionary(g => g.Key, g => g.ToList());
        
        int totalChunks = allChunks.Count;
        int completedChunks = 0;
        int completedFilesCount = 0;
        int totalFilesToPatch = filesToPatch.Count;
        long totalDownloaded = 0;
        long wastedBytes = 0;
        int totalRequests = 0;
        long usefulBytes = allChunks.Sum(c => (long)c.Chunk.CompressedSize);
        
        long totalDecompressedBytes = 0;
        
        var updateSw = System.Diagnostics.Stopwatch.StartNew();

        var openHandles = new ConcurrentDictionary<string, SafeFileHandle>();
        var pendingPerFile = new ConcurrentDictionary<string, int>(filesToPatch.ToDictionary(f => f.FullPath, f => f.ChunksByBundle.Values.Sum(l => l.Count)));
        double lastUIProgress = 0;

        try
        {
            var netSem = new SemaphoreSlim(16);
            var cpuSem = new SemaphoreSlim(4);

            var tasks = bundlesToProcess.Select(async bundleEntry =>
            {
                await netSem.WaitAsync();
                
                try
                {
                    string url = $"{_bundleBaseUrl}/{bundleEntry.Key:X16}.bundle";
                    var sorted = bundleEntry.Value.OrderBy(t => t.Chunk.BundleOffset).ToList();

                    const uint MaxGap = 128 * 1024;
                    var groups = new List<List<ChunkDownloadTask>>();
                    if (sorted.Count > 0)
                    {
                        var currentGroup = new List<ChunkDownloadTask> { sorted[0] };
                        for (int i = 1; i < sorted.Count; i++)
                        {
                            uint gap = (uint)(sorted[i].Chunk.BundleOffset - (sorted[i - 1].Chunk.BundleOffset + sorted[i - 1].Chunk.CompressedSize));
                            if (gap <= MaxGap) 
                            {
                                currentGroup.Add(sorted[i]);
                            }
                            else 
                            { 
                                groups.Add(currentGroup); 
                                currentGroup = new List<ChunkDownloadTask> { sorted[i] }; 
                            }
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
                        req.Headers.ConnectionClose = false;

                        using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                        resp.EnsureSuccessStatusCode();
                                              
                        using var responseStream = await resp.Content.ReadAsStreamAsync();
                        
                        long currentStreamPos = start;

                        foreach (var t in group)
                        {
                            long gap = (long)t.Chunk.BundleOffset - currentStreamPos;
                            if (gap > 0)
                            {
                                if (!_skipBufferPool.TryTake(out var skipBuf))
                                    skipBuf = new byte[64 * 1024];
                                
                                try
                                {
                                    long skipped = 0;
                                    while (skipped < gap)
                                    {
                                        int read = await responseStream.ReadAsync(skipBuf, 0, (int)Math.Min(gap - skipped, skipBuf.Length));
                                        if (read == 0) break;
                                        skipped += read;
                                    }
                                    Interlocked.Add(ref wastedBytes, skipped);
                                }
                                finally
                                {
                                    if (skipBuf.Length <= 64 * 1024) _skipBufferPool.Add(skipBuf);
                                }
                            }

                            byte[] comp;
                            bool usedPoolBuffer = false;
                            
                            if (_compBufferPool.TryTake(out var poolBuf) && poolBuf.Length >= (int)t.Chunk.CompressedSize)
                            {
                                comp = poolBuf;
                                usedPoolBuffer = true;
                            }
                            else
                            {
                                comp = new byte[t.Chunk.CompressedSize];
                            }

                            try
                            {
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
                                
                                try
                                {
                                    if (!_decompressorPool.TryPop(out var decompressor)) decompressor = new Decompressor();
                                    
                                    try
                                    {
                                        var compSpan = comp.AsSpan(0, tRead);
                                        var uncomp = decompressor.Unwrap(compSpan).ToArray();
                                        
                                        Interlocked.Add(ref totalDecompressedBytes, uncomp.Length);

                                        var handle = openHandles.GetOrAdd(t.FullPath, (path) => {
                                            var dir = Path.GetDirectoryName(path);
                                            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                                            
                                            var h = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.Asynchronous);
                                            
                                            if ((ulong)RandomAccess.GetLength(h) != t.FileInfo.FileSize)
                                            {
                                                RandomAccess.SetLength(h, (long)t.FileInfo.FileSize);
                                            }
                                            return h;
                                        });

                                        System.IO.RandomAccess.Write(handle, uncomp, (long)t.FileOffset);
                                        
                                        int doneChunks = Interlocked.Increment(ref completedChunks);
                                        int rem = pendingPerFile.AddOrUpdate(t.FullPath, 0, (k, v) => v - 1);
                                        
                                        double chunkProgress = (double)doneChunks / totalChunks;
                                        if (chunkProgress - lastUIProgress >= 0.005 || rem == 0)
                                        {
                                            lastUIProgress = chunkProgress;
                                            int virtualProgress = (int)(chunkProgress * totalFilesToPatch);
                                            ProgressChanged?.Invoke("Updating", Path.GetFileName(t.FullPath), virtualProgress, totalFilesToPatch);
                                        }
                                        
                                        if (rem == 0)
                                        {
                                            Interlocked.Increment(ref completedFilesCount);
                                            if (openHandles.TryRemove(t.FullPath, out var h)) h.Dispose();
                                        }
                                    }
                                    finally
                                    {
                                        _decompressorPool.Push(decompressor);
                                    }
                                }
                                finally 
                                { 
                                    cpuSem.Release();
                                }
                            }
                            finally
                            {
                                if (usedPoolBuffer && comp.Length <= 10 * 1024 * 1024)
                                    _compBufferPool.Add(comp);
                            }
                        }
                    }
                }
                catch (Exception ex) { _logService.LogWarning($"Bundle {bundleEntry.Key:X16} error: {ex.Message}"); }
                finally 
                { 
                    netSem.Release();
                }
            });
            await Task.WhenAll(tasks);
        }
        finally { foreach (var h in openHandles.Values) h.Dispose(); }

        double sec = updateSw.Elapsed.TotalSeconds;
        double efficiency = (double)usefulBytes / totalDownloaded * 100;
        double wastedMB = wastedBytes / 1024.0 / 1024.0;
        double usefulSpeed = (usefulBytes / 1024.0 / 1024.0) / sec;
        double actualSpeed = (totalDownloaded / 1024.0 / 1024.0) / sec;
        double decompressedGB = totalDecompressedBytes / 1024.0 / 1024.0 / 1024.0;
        double compressionRatio = (double)totalDecompressedBytes / usefulBytes;
        
        _logService.LogSuccess($"[Phase 2] Completed in {sec:F1}s");
        _logService.Log($"  • HTTP Requests: {totalRequests}");
        _logService.Log($"  • Useful data: {usefulBytes / 1024.0 / 1024.0:F2} MB");
        _logService.Log($"  • Wasted (gaps): {wastedMB:F2} MB ({(double)wastedBytes / totalDownloaded * 100:F1}%)");
        _logService.Log($"  • Efficiency: {efficiency:F1}% (128KB gap strategy)");
        _logService.Log($"  • Useful Speed: {usefulSpeed:F2} MB/s");
        _logService.Log($"  • Actual Speed: {actualSpeed:F2} MB/s");
        _logService.Log($"  • Decompressed: {decompressedGB:F2} GB (ratio {compressionRatio:F2}x)");
        
        return totalFilesToPatch;
    }
}