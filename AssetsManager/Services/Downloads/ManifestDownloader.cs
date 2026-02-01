using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Views.Models.Monitor;
using ZstdSharp;

namespace AssetsManager.Services.Downloads;

using AssetsManager.Services.Hashes;

public class ManifestDownloader
{
    private readonly HttpClient _httpClient;
    private readonly string _bundleBaseUrl = "https://lol.dyn.riotcdn.net/channels/public/bundles";
    private readonly HashService _hashService = new HashService();

    public event Action<string, int, int> ProgressChanged;

    public ManifestDownloader(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    private class ChunkDownloadTask
    {
        public RmanChunk Chunk { get; set; }
        public ulong FileOffset { get; set; }
        public RmanFile FileInfo { get; set; }
        public string FullPath { get; set; }
    }

    public async Task DownloadManifestAsync(RmanManifest manifest, string outputPath, int maxThreads, string filter = null, List<string> langs = null)
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

        int totalFiles = filteredFiles.Count;
        int completedFiles = 0;

        foreach (var file in filteredFiles)
        {
            var fullPath = Path.Combine(outputPath, file.Name.Replace("/", Path.DirectorySeparatorChar.ToString()));
            var dirName = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dirName) && !Directory.Exists(dirName)) Directory.CreateDirectory(dirName);

            if (!File.Exists(fullPath) || (ulong)new FileInfo(fullPath).Length != file.FileSize)
            {
                using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                {
                    fs.SetLength((long)file.FileSize);
                }
            }

            var chunksByBundle = new Dictionary<ulong, List<ChunkDownloadTask>>();
            ulong currentFileOffset = 0;
            
            // Smart Scan
            if (File.Exists(fullPath))
            {
                using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    foreach (var chunkId in file.ChunkIds)
                    {
                        var chunk = manifest.GetChunk(chunkId);
                        if (chunk == null) continue;

                        bool needsDownload = true;
                        if ((ulong)fs.Length >= currentFileOffset + chunk.UncompressedSize)
                        {
                            var localData = new byte[chunk.UncompressedSize];
                            fs.Position = (long)currentFileOffset;
                            await fs.ReadExactlyAsync(localData, 0, (int)chunk.UncompressedSize);
                            if (_hashService.VerifyChunk(localData, chunk.ChunkId, file.HashType)) needsDownload = false;
                        }

                        if (needsDownload)
                        {
                            if (!chunksByBundle.ContainsKey(chunk.BundleId)) chunksByBundle[chunk.BundleId] = new List<ChunkDownloadTask>();
                            chunksByBundle[chunk.BundleId].Add(new ChunkDownloadTask 
                            { 
                                Chunk = chunk, FileOffset = currentFileOffset, FileInfo = file, FullPath = fullPath 
                            });
                        }
                        currentFileOffset += chunk.UncompressedSize;
                    }
                }
            }

            if (chunksByBundle.Any())
            {
                var semaphore = new SemaphoreSlim(maxThreads);
                var downloadTasks = chunksByBundle.Select(async pair =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        using (var decompressor = new Decompressor())
                        {
                            await DownloadAndPatchChunksAsync(pair.Key, pair.Value, decompressor);
                        }
                    }
                    finally { semaphore.Release(); }
                });
                await Task.WhenAll(downloadTasks);
            }

            completedFiles++;
            ProgressChanged?.Invoke(file.Name, completedFiles, totalFiles);
        }
    }

    private async Task DownloadAndPatchChunksAsync(ulong bundleId, List<ChunkDownloadTask> tasks, Decompressor decompressor)
    {
        string bundleUrl = $"{_bundleBaseUrl}/{bundleId:X16}.bundle";
        var sortedTasks = tasks.OrderBy(t => t.Chunk.BundleOffset).ToList();

        int currentIndex = 0;
        while (currentIndex < sortedTasks.Count)
        {
            var group = new List<ChunkDownloadTask> { sortedTasks[currentIndex] };
            int nextIndex = currentIndex + 1;
            while (nextIndex < sortedTasks.Count)
            {
                var prev = sortedTasks[nextIndex - 1].Chunk;
                var curr = sortedTasks[nextIndex].Chunk;
                if (prev.BundleOffset + prev.CompressedSize == curr.BundleOffset)
                {
                    group.Add(sortedTasks[nextIndex]);
                    nextIndex++;
                }
                else break;
            }

            long start = (long)group.First().Chunk.BundleOffset;
            long end = (long)(group.Last().Chunk.BundleOffset + group.Last().Chunk.CompressedSize - 1);

            try 
            {
                var request = new HttpRequestMessage(HttpMethod.Get, bundleUrl);
                request.Headers.Range = new RangeHeaderValue(start, end);

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    var rangeData = await response.Content.ReadAsByteArrayAsync();

                    int internalOffset = 0;
                    foreach (var task in group)
                    {
                        var compressed = new byte[task.Chunk.CompressedSize];
                        Array.Copy(rangeData, internalOffset, compressed, 0, (int)task.Chunk.CompressedSize);
                        var uncompressed = decompressor.Unwrap(compressed).ToArray();

                        if (!_hashService.VerifyChunk(uncompressed, task.Chunk.ChunkId, task.FileInfo.HashType))
                            throw new Exception("Integrity failure");

                        await WriteChunkToFileAsync(task.FullPath, (long)task.FileOffset, uncompressed);
                        internalOffset += (int)task.Chunk.CompressedSize;
                    }
                }
            }
            catch { }
            currentIndex = nextIndex;
        }
    }

    private async Task WriteChunkToFileAsync(string path, long offset, byte[] data)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Position = offset;
            await fs.WriteAsync(data, 0, data.Length);
        }
    }
}
