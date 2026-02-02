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

    public event Action<string, string, int, int> ProgressChanged;

    public ManifestDownloader(HttpClient httpClient, LogService logService)
    {
        _httpClient = httpClient;
        _logService = logService;
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
        _logService.Log($"[Phase 1] Verifying {filteredFiles.Count} files...");
        var filesToPatch = new ConcurrentBag<FilePatchTask>();
        int totalFilesToVerify = filteredFiles.Count;
        int currentFilesVerify = 0;
        int alreadyCorrect = 0;
        var verifyStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var lastProgressTime = DateTime.MinValue;

        await Task.Run(async () =>
        {
            var scanSemaphore = new SemaphoreSlim(maxThreads * 2);
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
                                    await fs.ReadExactlyAsync(localData, 0, (int)chunk.UncompressedSize);
                                    if (_hashService.VerifyChunk(localData, chunk.ChunkId, file.HashType)) needsUpdate = false;
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

                    if (chunksByBundle.Any()) filesToPatch.Add(new FilePatchTask { FileInfo = file, FullPath = fullPath, ChunksByBundle = chunksByBundle });
                    else Interlocked.Increment(ref alreadyCorrect);

                    int completed = Interlocked.Increment(ref currentFilesVerify);
                    if ((DateTime.Now - lastProgressTime).TotalMilliseconds > 100 || completed == totalFilesToVerify)
                    {
                        lastProgressTime = DateTime.Now;
                        ProgressChanged?.Invoke("Verifying", file.Name, completed, totalFilesToVerify);
                    }
                }
                finally { scanSemaphore.Release(); }
            });
            await Task.WhenAll(scanTasks);
        });

        _logService.Log($"[Phase 1] Finished in {verifyStopwatch.Elapsed.TotalSeconds:F1}s. {alreadyCorrect} OK, {filesToPatch.Count} need patching.");

        // --- PHASE 2: HIGH-SPEED UPDATING ---
        if (!filesToPatch.Any()) return 0;

        var allChunks = filesToPatch.SelectMany(f => f.ChunksByBundle.Values.SelectMany(l => l)).ToList();
        var bundlesToProcess = allChunks.GroupBy(c => c.Chunk.BundleId).ToDictionary(g => g.Key, g => g.ToList());
        int totalChunks = allChunks.Count;
        int completedChunks = 0;
        long totalDownloaded = 0;
        long usefulBytes = allChunks.Sum(c => (long)c.Chunk.CompressedSize);
        var updateSw = System.Diagnostics.Stopwatch.StartNew();
        var lastUpdateProgressTime = DateTime.MinValue;

        var openHandles = new ConcurrentDictionary<string, SafeFileHandle>();
        var pendingPerFile = new ConcurrentDictionary<string, int>(filesToPatch.ToDictionary(f => f.FullPath, f => f.ChunksByBundle.Values.Sum(l => l.Count)));

        try
        {
            var netSem = new SemaphoreSlim(16); // Red 16
            var cpuSem = new SemaphoreSlim(4);  // CPU 4

            var tasks = bundlesToProcess.Select(async bundleEntry =>
            {
                await netSem.WaitAsync();
                try
                {
                    long bytes = await DownloadAndPatchBundleAsync(bundleEntry.Key, bundleEntry.Value, cpuSem, openHandles, (fileId) =>
                    {
                        int done = Interlocked.Increment(ref completedChunks);
                        if ((DateTime.Now - lastUpdateProgressTime).TotalMilliseconds > 100 || done == totalChunks)
                        {
                            lastUpdateProgressTime = DateTime.Now;
                            ProgressChanged?.Invoke("Updating", Path.GetFileName(fileId), done, totalChunks);
                        }

                        int rem = pendingPerFile.AddOrUpdate(fileId, 0, (k, v) => v - 1);
                        if (rem == 0)
                        {
                            if (openHandles.TryRemove(fileId, out var h)) h.Dispose();
                        }
                    });
                    Interlocked.Add(ref totalDownloaded, bytes);
                }
                catch (Exception ex) { _logService.LogWarning($"Bundle {bundleEntry.Key:X16} failed: {ex.Message}"); }
                finally { netSem.Release(); }
            });

            await Task.WhenAll(tasks);
        }
        finally
        {
            foreach (var h in openHandles.Values) h.Dispose();
        }

        double sec = updateSw.Elapsed.TotalSeconds;
        double eff = totalDownloaded > 0 ? ((double)usefulBytes / totalDownloaded) * 100 : 100;
        _logService.LogSuccess($"[Phase 2] Completed in {sec:F1}s. Efficiency: {eff:F1}% | Speed: {(totalDownloaded/1024.0/1024.0)/sec:F2} MB/s");

        return filesToPatch.Count;
    }

    private async Task<long> DownloadAndPatchBundleAsync(ulong bundleId, List<ChunkDownloadTask> tasks, SemaphoreSlim cpuSem, ConcurrentDictionary<string, SafeFileHandle> openHandles, Action<string> onChunkDone)
    {
        string url = $"{_bundleBaseUrl}/{bundleId:X16}.bundle";
        var sorted = tasks.OrderBy(t => t.Chunk.BundleOffset).ToList();
        long totalDownloaded = 0;

        // Grouping logic (Gap Filling 128KB)
        const uint MaxGap = 128 * 1024;
        var groups = new List<List<ChunkDownloadTask>>();
        if (sorted.Count > 0)
        {
            var curGroup = new List<ChunkDownloadTask> { sorted[0] };
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].Chunk.BundleOffset - (sorted[i-1].Chunk.BundleOffset + sorted[i-1].Chunk.CompressedSize) <= MaxGap) curGroup.Add(sorted[i]);
                else { groups.Add(curGroup); curGroup = new List<ChunkDownloadTask> { sorted[i] }; }
            }
            groups.Add(curGroup);
        }

        foreach (var group in groups)
        {
            long start = (long)group[0].Chunk.BundleOffset;
            long end = (long)(group.Last().Chunk.BundleOffset + group.Last().Chunk.CompressedSize - 1);

            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(start, end);

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var data = await resp.Content.ReadAsByteArrayAsync();
            totalDownloaded += data.Length;

            await cpuSem.WaitAsync(); // Turno de CPU para procesar este bloque bajado
            try
            {
                using var decompressor = new Decompressor();
                foreach (var t in group)
                {
                    int relOffset = (int)(t.Chunk.BundleOffset - (uint)start);
                    var comp = new byte[t.Chunk.CompressedSize];
                    Array.Copy(data, relOffset, comp, 0, (int)t.Chunk.CompressedSize);
                    var uncomp = decompressor.Unwrap(comp).ToArray();

                    var handle = openHandles.GetOrAdd(t.FullPath, (path) =>
                    {
                        var dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.Asynchronous);
                        if ((ulong)fs.Length != t.FileInfo.FileSize) fs.SetLength((long)t.FileInfo.FileSize);
                        return fs.SafeFileHandle;
                    });

                    System.IO.RandomAccess.Write(handle, uncomp, (long)t.FileOffset);
                    onChunkDone(t.FullPath);
                }
            }
            finally { cpuSem.Release(); }
        }
        return totalDownloaded;
    }
}
