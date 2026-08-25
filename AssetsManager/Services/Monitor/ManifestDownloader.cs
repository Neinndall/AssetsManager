using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using ZstdSharp;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using Microsoft.Win32.SafeHandles;

namespace AssetsManager.Services.Monitor;

public sealed class ManifestDownloader : IDisposable
{
    private const int ScanConcurrency = 2;
    private const int NetworkConcurrency = 12;
    private const int ProcessingConcurrency = 4;
    private const int ProgressIntervalMilliseconds = 100;
    private const long MaxAdjacentRangeGapBytes = 64 * 1024;
    private const long MaxAccumulatedRangeGapBytes = 256 * 1024;
    private const long MaxDownloadRangeBytes = 8 * 1024 * 1024;
    private const int GapBufferSize = 64 * 1024;
    private const string BundleBaseUrl = "https://lol.dyn.riotcdn.net/channels/public/bundles";

    private readonly HttpClient _httpClient;
    private readonly LogService _logService;
    private readonly DirectoriesCreator _directoriesCreator;
    private readonly HashService _hashService;
    private readonly ConcurrentStack<Decompressor> _decompressorPool = new();
    private bool _disposed;

    public event Action<string, int, int, string> ProgressChanged;

    public ManifestDownloader(HttpClient httpClient, LogService logService, DirectoriesCreator directoriesCreator, HashService hashService)
    {
        _httpClient = httpClient;
        _logService = logService;
        _directoriesCreator = directoriesCreator;
        _hashService = hashService;

        for (int i = 0; i < ProcessingConcurrency; i++) _decompressorPool.Push(new Decompressor());
    }

    private sealed class ChunkDownloadTask
    {
        public RmanChunk Chunk { get; set; }
        public ulong FileOffset { get; set; }
    }

    private sealed class FilePatchTask
    {
        public RmanFile FileInfo { get; set; }
        public string PhysicalPath { get; set; }
        public List<ChunkDownloadTask> Chunks { get; set; }
    }

    private sealed class UniqueChunkTask
    {
        public RmanChunk Chunk { get; set; }
        public List<TargetInfo> Targets { get; set; }
    }

    private sealed class TargetInfo
    {
        public string PhysicalPath { get; set; }
        public ulong FileOffset { get; set; }
        public RmanFile FileInfo { get; set; }
    }

    public Task<int> DownloadManifestAsync(
        RmanManifest manifest,
        string outputPath,
        string filter = null,
        List<string> langs = null,
        CancellationToken cancellationToken = default)
        => RunManifestAsync(manifest, outputPath, filter, langs, verificationOnly: false, cancellationToken);

    public Task<int> VerifyManifestAsync(
        RmanManifest manifest,
        string outputPath,
        string filter = null,
        List<string> langs = null,
        CancellationToken cancellationToken = default)
        => RunManifestAsync(manifest, outputPath, filter, langs, verificationOnly: true, cancellationToken);

    private Task<int> RunManifestAsync(
        RmanManifest manifest,
        string outputPath,
        string filter,
        List<string> langs,
        bool verificationOnly,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(manifest);

        // Run the RMAN pipeline on the thread pool so filtering, lookup construction,
        // range planning, hashing, and patch bookkeeping never execute on the WPF dispatcher.
        return Task.Run(
            () => DownloadManifestCoreAsync(manifest, outputPath, filter, langs, verificationOnly, cancellationToken),
            cancellationToken);
    }

    private async Task<int> DownloadManifestCoreAsync(
        RmanManifest manifest,
        string outputPath,
        string filter,
        List<string> langs,
        bool verificationOnly,
        CancellationToken cancellationToken)
    {

        // Phase 1: Filter the manifest and prepare the target file set.
        string outputRoot = Path.GetFullPath(outputPath);
        string outputRootPrefix = Path.EndsInDirectorySeparator(outputRoot)
            ? outputRoot
            : outputRoot + Path.DirectorySeparatorChar;
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

        if (!verificationOnly)
            _directoriesCreator.CreateDirectory(outputRoot);

        // Phase 2: Scan local files with bounded disk concurrency and verify every physical chunk.
        var filesToPatch = new ConcurrentBag<FilePatchTask>();
        long totalChunksToDownloadCount = 0;
        long totalCompressedBytes = 0;
        int alreadyCorrect = 0;
        var verifyStopwatch = System.Diagnostics.Stopwatch.StartNew();

        int totalToVerify = filteredFiles.Count;
        int currentVerify = 0;
        int lastReportedVerify = 0;
        var lastProgressTime = DateTime.MinValue;
        var verifyLock = new object();

        _logService.Log($"[Verification] Starting analysis of {totalToVerify} files...");

        try
        {
            await Parallel.ForEachAsync(
                    filteredFiles,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = ScanConcurrency,
                        CancellationToken = cancellationToken
                    },
                    async (file, token) =>
                    {
                        string physicalPath = GetPhysicalPath(outputRoot, outputRootPrefix, file.Name);
                        var fileInfo = new FileInfo(physicalPath);
                        bool fileExists = fileInfo.Exists;
                        ulong currentFileLength = fileExists ? (ulong)fileInfo.Length : 0;
                        bool requiresResize = !fileExists || currentFileLength != file.FileSize;
                        var chunks = new List<ChunkDownloadTask>();
                        ulong currentFileOffset = 0;

                        if (fileExists)
                        {
                            byte[] verificationBuffer = null;
                            try
                            {
                                await using var stream = new FileStream(
                                    physicalPath,
                                    FileMode.Open,
                                    FileAccess.Read,
                                    FileShare.ReadWrite,
                                    256 * 1024,
                                    FileOptions.SequentialScan | FileOptions.Asynchronous);

                                foreach (ulong chunkId in file.ChunkIds)
                                {
                                    token.ThrowIfCancellationRequested();
                                    RmanChunk chunk = GetRequiredChunk(manifest, chunkId, file.Name);
                                    int chunkSize = GetBufferSize(chunk.UncompressedSize, "uncompressed");
                                    bool needsUpdate = true;

                                    if (currentFileLength >= currentFileOffset + chunk.UncompressedSize)
                                    {
                                        if (verificationBuffer == null || verificationBuffer.Length < chunkSize)
                                        {
                                            byte[] previousBuffer = verificationBuffer;
                                            verificationBuffer = ArrayPool<byte>.Shared.Rent(chunkSize);
                                            if (previousBuffer != null)
                                                ArrayPool<byte>.Shared.Return(previousBuffer);
                                        }

                                        int totalRead = await ReadExactlyOrToEndAsync(
                                            stream,
                                            verificationBuffer.AsMemory(0, chunkSize),
                                            token);
                                        if (totalRead == chunkSize)
                                        {
                                            needsUpdate = !_hashService.VerifyChunk(
                                                verificationBuffer.AsSpan(0, totalRead),
                                                chunk.ChunkId,
                                                file.HashType);
                                        }
                                    }

                                    if (needsUpdate)
                                    {
                                        chunks.Add(new ChunkDownloadTask { Chunk = chunk, FileOffset = currentFileOffset });
                                    }

                                    currentFileOffset += chunk.UncompressedSize;
                                }
                            }
                            finally
                            {
                                if (verificationBuffer != null)
                                    ArrayPool<byte>.Shared.Return(verificationBuffer);
                            }
                        }
                        else
                        {
                            foreach (ulong chunkId in file.ChunkIds)
                            {
                                RmanChunk chunk = GetRequiredChunk(manifest, chunkId, file.Name);
                                chunks.Add(new ChunkDownloadTask { Chunk = chunk, FileOffset = currentFileOffset });
                                currentFileOffset += chunk.UncompressedSize;
                            }
                        }

                        if (currentFileOffset != file.FileSize)
                        {
                            throw new InvalidDataException($"Manifest file size mismatch for '{file.Name}'.");
                        }

                        if (chunks.Count > 0 || requiresResize)
                        {
                            filesToPatch.Add(new FilePatchTask
                            {
                                FileInfo = file,
                                PhysicalPath = physicalPath,
                                Chunks = chunks
                            });
                            Interlocked.Add(ref totalChunksToDownloadCount, chunks.Count);
                            Interlocked.Add(ref totalCompressedBytes, chunks.Sum(c => (long)c.Chunk.CompressedSize));
                        }
                        else
                        {
                            Interlocked.Increment(ref alreadyCorrect);
                        }

                        int completed = Interlocked.Increment(ref currentVerify);
                        DateTime now = DateTime.UtcNow;
                        lock (verifyLock)
                        {
                            if (completed > lastReportedVerify
                                && ((now - lastProgressTime).TotalMilliseconds >= ProgressIntervalMilliseconds
                                    || completed == totalToVerify))
                            {
                                lastProgressTime = now;
                                lastReportedVerify = completed;
                                ProgressChanged?.Invoke("Verifying", completed, totalToVerify, $"{completed} of {totalToVerify} files: {file.Name}");
                            }
                        }
                    });
        }
        catch (OperationCanceledException)
        {
            _logService.LogWarning("Verification process was cancelled.");
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();

        verifyStopwatch.Stop();

        double verifyMB = totalCompressedBytes / 1024.0 / 1024.0;
        _logService.Log($"[Verification] Finished in {verifyStopwatch.Elapsed.TotalSeconds:F1}s.");
        _logService.Log($"  • Files OK: {alreadyCorrect}");
        _logService.Log($"  • Files to patch: {filesToPatch.Count}");
        _logService.Log($"  • Chunks to download: {totalChunksToDownloadCount:N0}");
        _logService.Log($"  • Estimated download: {verifyMB:F2} MB (compressed)");

        if (verificationOnly) return filesToPatch.Count;
        if (!filesToPatch.Any()) return 0;

        // Preserve the completed verification frame before the update phase replaces it.
        await Task.Delay(200, cancellationToken);

        // Phase 3: Deduplicate missing chunks, download bundle ranges, verify, and patch by file offset.
        var filesToPatchList = filesToPatch.OrderBy(f => f.FileInfo.Name).ToList();
        var initialChunksPerFile = filesToPatchList.ToDictionary(
            f => f.PhysicalPath,
            f => Math.Max(f.Chunks.Count, 1));
        var pathToIndex = filesToPatchList.Select((f, i) => new { f.PhysicalPath, i }).ToDictionary(x => x.PhysicalPath, x => x.i);

        var uniqueChunkMap = new Dictionary<ulong, UniqueChunkTask>();
        int totalChunkTargets = 0;
        foreach (FilePatchTask filePatch in filesToPatchList)
        {
            foreach (ChunkDownloadTask chunkTask in filePatch.Chunks)
            {
                if (!uniqueChunkMap.TryGetValue(chunkTask.Chunk.ChunkId, out UniqueChunkTask uniqueChunk))
                {
                    uniqueChunk = new UniqueChunkTask
                    {
                        Chunk = chunkTask.Chunk,
                        Targets = new List<TargetInfo>()
                    };
                    uniqueChunkMap.Add(chunkTask.Chunk.ChunkId, uniqueChunk);
                }

                uniqueChunk.Targets.Add(new TargetInfo
                {
                    PhysicalPath = filePatch.PhysicalPath,
                    FileOffset = chunkTask.FileOffset,
                    FileInfo = filePatch.FileInfo
                });
                totalChunkTargets++;
            }
        }

        List<FilePatchTask> resizeOnlyFiles = filesToPatchList.Where(f => f.Chunks.Count == 0).ToList();
        int totalChunks = totalChunkTargets + resizeOnlyFiles.Count;

        // Reset progress bar instantly for the start of the Updating phase (0%).
        ProgressChanged?.Invoke("Updating", 0, totalChunks, $"0 of {filesToPatchList.Count} files: Initializing...");

        // Force the 0% Updating frame to paint before downloads start.
        if (System.Windows.Application.Current != null)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        }

        List<UniqueChunkTask> uniqueChunks = uniqueChunkMap.Values.ToList();

        // Prioritize bundles that can complete the earliest file in the stable UI order.
        var bundlesToProcess = uniqueChunks
            .GroupBy(c => c.Chunk.BundleId)
            .Select(g => new
            {
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
        long lastUpdateProgressTimestamp = 0;
        var uiLock = new object();

        long totalDownloaded = 0;
        long wastedBytes = 0;
        int totalRequests = 0;
        long usefulBytes = uniqueChunks.Sum(c => (long)c.Chunk.CompressedSize);
        long totalDecompressedBytes = 0;

        var updateSw = System.Diagnostics.Stopwatch.StartNew();
        var openHandles = new ConcurrentDictionary<string, Lazy<SafeFileHandle>>();
        var pendingPerFile = new ConcurrentDictionary<string, int>(initialChunksPerFile);

        void ReportUpdateProgress(int currentDoneChunks)
        {
            lock (uiLock)
            {
                while (visualFileIndex < filesToPatchList.Count
                       && pendingPerFile.TryGetValue(filesToPatchList[visualFileIndex].PhysicalPath, out int remaining)
                       && remaining == 0)
                {
                    visualFileIndex++;
                }

                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                bool intervalElapsed = lastUpdateProgressTimestamp == 0
                    || System.Diagnostics.Stopwatch.GetElapsedTime(lastUpdateProgressTimestamp, now).TotalMilliseconds >= ProgressIntervalMilliseconds;
                if (currentDoneChunks <= lastReportedChunks
                    || (!intervalElapsed && currentDoneChunks != totalChunks))
                {
                    return;
                }

                lastReportedChunks = currentDoneChunks;
                lastUpdateProgressTimestamp = now;
                int reportIndex = Math.Min(visualFileIndex, totalFilesToPatch - 1);
                FilePatchTask reportFile = filesToPatchList[reportIndex];
                pendingPerFile.TryGetValue(reportFile.PhysicalPath, out int pending);
                int totalForFile = initialChunksPerFile[reportFile.PhysicalPath];
                int doneForFile = totalForFile - pending;
                string message = $"{Math.Min(visualFileIndex + 1, totalFilesToPatch)} of {totalFilesToPatch} files: {reportFile.FileInfo.Name}|{doneForFile}/{totalForFile}";
                ProgressChanged?.Invoke("Updating", currentDoneChunks, totalChunks, message);
            }
        }

        try
        {
            foreach (FilePatchTask resizeOnlyFile in resizeOnlyFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string directory = Path.GetDirectoryName(resizeOnlyFile.PhysicalPath);
                if (!string.IsNullOrEmpty(directory)) _directoriesCreator.CreateDirectory(directory);
                using SafeFileHandle handle = File.OpenHandle(
                    resizeOnlyFile.PhysicalPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite);
                RandomAccess.SetLength(handle, (long)resizeOnlyFile.FileInfo.FileSize);
                pendingPerFile[resizeOnlyFile.PhysicalPath] = 0;
                ReportUpdateProgress(Interlocked.Increment(ref completedChunks));
            }

            CancellationToken updateToken = cancellationToken;
            using var cpuSem = new SemaphoreSlim(Math.Min(Environment.ProcessorCount, ProcessingConcurrency));

            await Parallel.ForEachAsync(
                bundlesToProcess,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = NetworkConcurrency,
                    CancellationToken = updateToken
                },
                async (bundleEntry, _) =>
                {
                    try
                    {
                        updateToken.ThrowIfCancellationRequested();
                        string url = $"{BundleBaseUrl}/{bundleEntry.Key:X16}.bundle";
                        var sorted = bundleEntry.Value.OrderBy(t => t.Chunk.BundleOffset).ToList();

                        // Coalesce nearby pending chunks to reduce CDN round-trips. Per-gap, cumulative-waste,
                        // and total-range caps prevent a chain of small gaps from expanding into a whole bundle.
                        var ranges = new List<DownloadRange>();
                        if (sorted.Count > 0)
                        {
                            var currentRange = new DownloadRange(sorted[0]);
                            for (int i = 1; i < sorted.Count; i++)
                            {
                                long gap = (long)sorted[i].Chunk.BundleOffset - currentRange.EndExclusive;
                                if (gap < 0) throw new InvalidDataException($"Bundle {bundleEntry.Key:X16} contains overlapping chunks.");
                                if (currentRange.CanAppend(sorted[i], gap))
                                {
                                    currentRange.Append(sorted[i], gap);
                                }
                                else
                                {
                                    ranges.Add(currentRange);
                                    currentRange = new DownloadRange(sorted[i]);
                                }
                            }
                            ranges.Add(currentRange);
                        }

                        Interlocked.Add(ref totalRequests, ranges.Count);
                        foreach (DownloadRange range in ranges)
                        {
                            updateToken.ThrowIfCancellationRequested();
                            long start = range.Start;
                            long end = range.EndExclusive - 1;

                            using var req = new HttpRequestMessage(HttpMethod.Get, url);
                            req.Headers.Range = new RangeHeaderValue(start, end);

                            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, updateToken);
                            resp.EnsureSuccessStatusCode();
                            if (resp.StatusCode != HttpStatusCode.PartialContent
                                || resp.Content.Headers.ContentRange?.From != start
                                || resp.Content.Headers.ContentRange?.To != end)
                            {
                                throw new InvalidDataException($"Bundle server returned an invalid byte range for {bundleEntry.Key:X16}.");
                            }

                            await using var responseStream = await resp.Content.ReadAsStreamAsync(updateToken);
                            long currentStreamPos = start;
                            byte[] gapBuffer = range.GapBytes > 0
                                ? ArrayPool<byte>.Shared.Rent(GapBufferSize)
                                : null;

                            try
                            {
                                foreach (UniqueChunkTask t in range.Chunks)
                                {
                                    updateToken.ThrowIfCancellationRequested();
                                    long gap = (long)t.Chunk.BundleOffset - currentStreamPos;
                                    if (gap < 0)
                                        throw new InvalidDataException($"Bundle range for {bundleEntry.Key:X16} overlaps a previous chunk.");

                                    if (gap > 0)
                                    {
                                        await SkipExactlyAsync(responseStream, gap, gapBuffer, updateToken);
                                        Interlocked.Add(ref totalDownloaded, gap);
                                        Interlocked.Add(ref wastedBytes, gap);
                                        currentStreamPos += gap;
                                    }

                                    int compressedSize = GetBufferSize(t.Chunk.CompressedSize, "compressed");
                                    byte[] comp = ArrayPool<byte>.Shared.Rent(compressedSize);
                                    try
                                    {
                                        await ReadExactlyAsync(responseStream, comp.AsMemory(0, compressedSize), updateToken);
                                        Interlocked.Add(ref totalDownloaded, compressedSize);
                                        currentStreamPos = (long)t.Chunk.BundleOffset + compressedSize;

                                        await cpuSem.WaitAsync(updateToken);
                                        try
                                        {
                                            updateToken.ThrowIfCancellationRequested();
                                            if (!_decompressorPool.TryPop(out var decompressor)) decompressor = new Decompressor();
                                            try
                                            {
                                                int uncompressedSize = GetBufferSize(t.Chunk.UncompressedSize, "uncompressed");
                                                byte[] decompBuffer = ArrayPool<byte>.Shared.Rent(uncompressedSize);
                                                try
                                                {
                                                    int decompressedBytes = decompressor.Unwrap(comp.AsSpan(0, compressedSize), decompBuffer.AsSpan(0, uncompressedSize));
                                                    if (decompressedBytes != uncompressedSize)
                                                        throw new Exception($"Chunk decompression size mismatch. Expected {t.Chunk.UncompressedSize}, got {decompressedBytes}");

                                                    Interlocked.Add(ref totalDecompressedBytes, (long)decompressedBytes);
                                                    ReadOnlyMemory<byte> uncomp = decompBuffer.AsMemory(0, decompressedBytes);

                                                    // Never write server data until its RMAN chunk identifier is verified.
                                                    HashType hashType = t.Targets[0].FileInfo.HashType;
                                                    if (!_hashService.VerifyChunk(uncomp.Span, t.Chunk.ChunkId, hashType))
                                                    {
                                                        throw new InvalidDataException($"Chunk {t.Chunk.ChunkId:X16} failed integrity verification.");
                                                    }

                                                    foreach (var target in t.Targets)
                                                    {
                                                        if (target.FileInfo.HashType != hashType
                                                            && !_hashService.VerifyChunk(uncomp.Span, t.Chunk.ChunkId, target.FileInfo.HashType))
                                                        {
                                                            throw new InvalidDataException($"Chunk {t.Chunk.ChunkId:X16} has inconsistent hash metadata.");
                                                        }

                                                        var lazyHandle = openHandles.GetOrAdd(target.PhysicalPath, (path) => new Lazy<SafeFileHandle>(() =>
                                                        {
                                                            var dir = Path.GetDirectoryName(path);
                                                            if (!string.IsNullOrEmpty(dir)) _directoriesCreator.CreateDirectory(dir);
                                                            var h = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.Asynchronous);
                                                            if ((ulong)RandomAccess.GetLength(h) != target.FileInfo.FileSize) RandomAccess.SetLength(h, (long)target.FileInfo.FileSize);
                                                            return h;
                                                        }, LazyThreadSafetyMode.ExecutionAndPublication));

                                                        var handle = lazyHandle.Value;
                                                        await RandomAccess.WriteAsync(handle, uncomp, (long)target.FileOffset, updateToken);

                                                        int currentDoneChunks = Interlocked.Increment(ref completedChunks);
                                                        int rem = pendingPerFile.AddOrUpdate(target.PhysicalPath, 0, (k, v) => v - 1);

                                                        if (rem == 0)
                                                        {
                                                            if (openHandles.TryRemove(target.PhysicalPath, out var lazyHnd))
                                                            {
                                                                if (lazyHnd.IsValueCreated) lazyHnd.Value.Dispose();
                                                            }
                                                        }

                                                        ReportUpdateProgress(currentDoneChunks);
                                                    }
                                                }
                                                finally { ArrayPool<byte>.Shared.Return(decompBuffer); }
                                            }
                                            finally { _decompressorPool.Push(decompressor); }
                                        }
                                        finally { cpuSem.Release(); }
                                    }
                                    finally { ArrayPool<byte>.Shared.Return(comp); }
                                }
                            }
                            finally
                            {
                                if (gapBuffer != null) ArrayPool<byte>.Shared.Return(gapBuffer);
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logService.LogError(ex, $"Bundle {bundleEntry.Key:X16} processing error");
                        throw;
                    }
                });
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
        _logService.LogDebug($"  • Wasted range gaps: {wastedMB:F2} MB");
        _logService.LogDebug($"  • Efficiency: {efficiency:F1}% (bounded adaptive ranges)");
        _logService.LogDebug($"  • Useful Speed: {usefulSpeed:F2} MB/s");
        _logService.LogDebug($"  • Actual Speed: {actualSpeed:F2} MB/s");
        _logService.LogDebug($"  • Decompressed: {decompressedGB:F2} GB (ratio {compressionRatio:F2}x)");

        return totalFilesToPatch;
    }

    private sealed class DownloadRange
    {
        public DownloadRange(UniqueChunkTask first)
        {
            Chunks.Add(first);
            Start = first.Chunk.BundleOffset;
            EndExclusive = Start + first.Chunk.CompressedSize;
        }

        public List<UniqueChunkTask> Chunks { get; } = new();
        public long Start { get; }
        public long EndExclusive { get; private set; }
        public long GapBytes { get; private set; }

        public bool CanAppend(UniqueChunkTask next, long gap)
        {
            long nextEnd = (long)next.Chunk.BundleOffset + next.Chunk.CompressedSize;
            return gap <= MaxAdjacentRangeGapBytes
                   && GapBytes + gap <= MaxAccumulatedRangeGapBytes
                   && nextEnd - Start <= MaxDownloadRangeBytes;
        }

        public void Append(UniqueChunkTask next, long gap)
        {
            Chunks.Add(next);
            GapBytes += gap;
            EndExclusive = (long)next.Chunk.BundleOffset + next.Chunk.CompressedSize;
        }
    }

    private static string GetPhysicalPath(string outputRoot, string outputRootPrefix, string manifestPath)
    {
        string relativePath = manifestPath.Replace('/', Path.DirectorySeparatorChar);
        string physicalPath = Path.GetFullPath(Path.Combine(outputRoot, relativePath));
        if (!physicalPath.StartsWith(outputRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Manifest path escapes the output directory: '{manifestPath}'.");
        }

        return physicalPath;
    }

    private static RmanChunk GetRequiredChunk(RmanManifest manifest, ulong chunkId, string fileName)
        => manifest.GetChunk(chunkId)
           ?? throw new InvalidDataException($"Manifest file '{fileName}' references missing chunk {chunkId:X16}.");

    private static int GetBufferSize(uint size, string kind)
    {
        if (size == 0 || size > int.MaxValue)
        {
            throw new InvalidDataException($"Invalid {kind} chunk size: {size}.");
        }

        return (int)size;
    }

    private static async ValueTask<int> ReadExactlyOrToEndAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int read = await stream.ReadAsync(destination[totalRead..], cancellationToken);
            if (read == 0) break;
            totalRead += read;
        }

        return totalRead;
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        int totalRead = await ReadExactlyOrToEndAsync(stream, destination, cancellationToken);
        if (totalRead != destination.Length)
        {
            throw new EndOfStreamException($"Bundle range ended early. Expected {destination.Length} bytes, got {totalRead}.");
        }
    }

    private static async ValueTask SkipExactlyAsync(
        Stream stream,
        long byteCount,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));

        long skipped = 0;
        while (skipped < byteCount)
        {
            int readSize = (int)Math.Min(byteCount - skipped, buffer.Length);
            int read = await stream.ReadAsync(buffer.AsMemory(0, readSize), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException($"Bundle range ended while skipping a {byteCount}-byte gap.");

            skipped += read;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        while (_decompressorPool.TryPop(out Decompressor decompressor))
        {
            decompressor.Dispose();
        }
    }
}
