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

namespace AssetsManager.Services.Downloads;

public class ManifestDownloader
{
    private readonly HttpClient _httpClient;
    private readonly LogService _logService;
    private readonly string _bundleBaseUrl = "https://lol.dyn.riotcdn.net/channels/public/bundles";
    private readonly HashService _hashService = new HashService();

    // Evento compatible con el nuevo sistema de progreso dinámico
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

        // --- PHASE 1: HIGH-PERFORMANCE VERIFICATION ---
        _logService.Log($"[Phase 1] Verifying integrity of {filteredFiles.Count} files...");
        var filesToPatch = new ConcurrentBag<FilePatchTask>();
        int totalToVerify = filteredFiles.Count;
        int currentVerify = 0;
        int alreadyCorrect = 0;
        var verifyStopwatch = System.Diagnostics.Stopwatch.StartNew();

        await Task.Run(async () =>
        {
            var scanSemaphore = new SemaphoreSlim(maxThreads * 2); // More threads for disk scanning
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
                        // If doesn't exist or is smaller, we need all its chunks
                        foreach (var chunkId in file.ChunkIds)
                        {
                            var chunk = manifest.GetChunk(chunkId);
                            if (chunk == null) continue;
                            if (!chunksByBundle.ContainsKey(chunk.BundleId)) chunksByBundle[chunk.BundleId] = new List<ChunkDownloadTask>();
                            chunksByBundle[chunk.BundleId].Add(new ChunkDownloadTask { Chunk = chunk, FileOffset = currentFileOffset, FileInfo = file, FullPath = fullPath });
                            currentFileOffset += chunk.UncompressedSize;
                        }
                    }
                    else
                    {
                        // If size is equal or larger, verify chunks (Smart Fixup)
                        using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 512 * 1024)) // 512KB Buffer
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

                    if (chunksByBundle.Any())
                    {
                        filesToPatch.Add(new FilePatchTask { FileInfo = file, FullPath = fullPath, ChunksByBundle = chunksByBundle });
                    }
                    else
                    {
                        Interlocked.Increment(ref alreadyCorrect);
                    }

                    int completed = Interlocked.Increment(ref currentVerify);
                    // Restore file name display for real-time feedback
                    ProgressChanged?.Invoke("Verifying", file.Name, completed, totalToVerify);
                }
                finally { scanSemaphore.Release(); }
            });

            await Task.WhenAll(scanTasks);
        });

        verifyStopwatch.Stop();
        _logService.Log($"[Phase 1] Verification finished in {verifyStopwatch.Elapsed.TotalSeconds:F1}s. " +
                        $"{alreadyCorrect} files OK, {filesToPatch.Count} need patching.");

        // --- PHASE 2: GLOBAL UPDATE (OPTIMIZED) ---
        if (!filesToPatch.Any()) 
        {
            return 0;
        }

        var allChunksByBundle = filesToPatch
            .SelectMany(f => f.ChunksByBundle.Values.SelectMany(list => list))
            .GroupBy(c => c.Chunk.BundleId)
            .ToDictionary(g => g.Key, g => g.ToList());

        _logService.Log($"[Phase 2] Starting patching of {allChunksByBundle.Count} bundles for {filesToPatch.Count} files...");

        var globalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        long totalBytesDownloaded = 0;
        int completedFilesCount = 0;
        int totalFilesToPatch = filesToPatch.Count;

        // Atomic dictionary for thread-safe progress tracking
        var pendingChunksPerFile = new ConcurrentDictionary<string, int>(filesToPatch.ToDictionary(f => f.FullPath, f => f.ChunksByBundle.Values.Sum(l => l.Count)));
        // Pool of SafeFileHandles for maximum speed
        var openHandles = new ConcurrentDictionary<string, Microsoft.Win32.SafeHandles.SafeFileHandle>();

        try
        {
            // Limitamos a maxThreads (8) las peticiones simultáneas de red
            var networkSemaphore = new SemaphoreSlim(maxThreads);
            var bundleTasks = allChunksByBundle.Select(async bundleGroup =>
            {
                await networkSemaphore.WaitAsync();
                try
                {
                    using (var decompressor = new Decompressor())
                    {
                        long bytes = await DownloadAndPatchBundleGroupAsync(bundleGroup.Key, bundleGroup.Value, decompressor, openHandles, (fileId) =>
                        {
                            int remaining = pendingChunksPerFile.AddOrUpdate(fileId, 0, (key, oldVal) => oldVal - 1);
                            if (remaining == 0)
                            {
                                int done = Interlocked.Increment(ref completedFilesCount);
                                ProgressChanged?.Invoke("Updating", Path.GetFileName(fileId), done, totalFilesToPatch);
                                
                                if (openHandles.TryRemove(fileId, out var handle)) handle.Dispose();
                            }
                        });
                        Interlocked.Add(ref totalBytesDownloaded, bytes);
                    }
                }
                catch (Exception ex) { _logService.LogWarning($"Bundle {bundleGroup.Key:X16} error: {ex.Message}"); }
                finally { networkSemaphore.Release(); }
            });

            await Task.WhenAll(bundleTasks);
        }
        finally
        {
            foreach (var h in openHandles.Values) h.Dispose();
            openHandles.Clear();
        }

        globalStopwatch.Stop();
        _logService.LogSuccess($"[Phase 2] Patching completed: {totalBytesDownloaded / 1024 / 1024} MB in {globalStopwatch.Elapsed.TotalSeconds:F1}s " +
                               $"({(totalBytesDownloaded / 1024.0 / 1024.0) / globalStopwatch.Elapsed.TotalSeconds:F2} MB/s)");
        
        return totalFilesToPatch;
    }

    private async Task<long> DownloadAndPatchBundleGroupAsync(ulong bundleId, List<ChunkDownloadTask> tasks, Decompressor decompressor, ConcurrentDictionary<string, Microsoft.Win32.SafeHandles.SafeFileHandle> openHandles, Action<string> onChunkProcessed)
    {
        string bundleUrl = $"{_bundleBaseUrl}/{bundleId:X16}.bundle";
        var sortedTasks = tasks.OrderBy(t => t.Chunk.BundleOffset).ToList();
        long downloadedBytes = 0;

        const uint MaxGapSize = 256 * 1024;
        var groups = new List<List<ChunkDownloadTask>>();
        if (sortedTasks.Count > 0)
        {
            var currentGroup = new List<ChunkDownloadTask> { sortedTasks[0] };
            for (int i = 1; i < sortedTasks.Count; i++)
            {
                var prev = sortedTasks[i - 1].Chunk;
                var curr = sortedTasks[i].Chunk;
                if (curr.BundleOffset - (prev.BundleOffset + prev.CompressedSize) <= MaxGapSize) currentGroup.Add(sortedTasks[i]);
                else { groups.Add(currentGroup); currentGroup = new List<ChunkDownloadTask> { sortedTasks[i] }; }
            }
            groups.Add(currentGroup);
        }

        foreach (var group in groups)
        {
            long start = (long)group.First().Chunk.BundleOffset;
            long end = (long)(group.Last().Chunk.BundleOffset + group.Last().Chunk.CompressedSize - 1);

            var request = new HttpRequestMessage(HttpMethod.Get, bundleUrl);
            request.Headers.Range = new RangeHeaderValue(start, end);

            using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var rangeData = await response.Content.ReadAsByteArrayAsync();
                downloadedBytes += rangeData.Length;

                foreach (var chunkTask in group)
                {
                    int relativeOffset = (int)(chunkTask.Chunk.BundleOffset - (uint)start);
                    var compressed = new byte[chunkTask.Chunk.CompressedSize];
                    Array.Copy(rangeData, relativeOffset, compressed, 0, (int)chunkTask.Chunk.CompressedSize);
                    
                    var uncompressed = decompressor.Unwrap(compressed).ToArray();

                    var handle = openHandles.GetOrAdd(chunkTask.FullPath, (path) =>
                    {
                        var dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.Asynchronous);
                        if ((ulong)fs.Length != chunkTask.FileInfo.FileSize) fs.SetLength((long)chunkTask.FileInfo.FileSize);
                        var h = fs.SafeFileHandle;
                        return h; 
                    });

                    System.IO.RandomAccess.Write(handle, uncompressed, (long)chunkTask.FileOffset);
                    onChunkProcessed(chunkTask.FullPath);
                }
            }
        }
        return downloadedBytes;
    }
}
