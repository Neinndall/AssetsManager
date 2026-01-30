using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers.Binary;
using ZstdSharp;
using AssetsManager.Services.Core;

namespace AssetsManager.Services.Parsers
{
    public class RmanParser
    {
        private readonly HttpClient _httpClient;
        private readonly LogService _logService;
        private const string DefaultBundleUrl = "https://lol.dyn.riotcdn.net/channels/public/bundles";

        #region Models
        public class RmanManifest
        {
            public ulong ManifestId { get; set; }
            public List<RmanFile> Files { get; set; } = new();
            public Dictionary<ulong, RmanChunk> Chunks { get; set; } = new();
            public Dictionary<ulong, RmanBundle> Bundles { get; set; } = new();
        }

        public class RmanFile
        {
            public string Name { get; set; }
            public ulong Size { get; set; }
            public List<string> Languages { get; set; } = new();
            public List<RmanChunk> Chunks { get; set; } = new();
        }

        public class RmanBundle
        {
            public ulong BundleId { get; set; }
            public List<RmanChunk> Chunks { get; set; } = new();
        }

        public class RmanChunk
        {
            public ulong ChunkId { get; set; }
            public ulong BundleId { get; set; }
            public uint CompressedSize { get; set; }
            public uint UncompressedSize { get; set; }
            public ulong BundleOffset { get; set; }
            public ulong FileOffset { get; set; }
        }
        #endregion

        public RmanParser(HttpClient httpClient, LogService logService)
        {
            _httpClient = httpClient;
            _logService = logService;
        }

        public async Task<RmanManifest> LoadManifestAsync(string urlOrPath)
        {
            byte[] data;
            if (Uri.IsWellFormedUriString(urlOrPath, UriKind.Absolute))
                data = await _httpClient.GetByteArrayAsync(urlOrPath);
            else
                data = await File.ReadAllBytesAsync(urlOrPath);

            return ParseManifest(data);
        }

        private RmanManifest ParseManifest(byte[] data)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x4E414D52)
                throw new Exception("Invalid RMAN magic");

            var manifest = new RmanManifest();
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
            uint compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
            manifest.ManifestId = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(16));
            uint uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(24));

            byte[] bodyCompressed = new byte[compressedSize];
            Array.Copy(data, (int)offset, bodyCompressed, 0, (int)compressedSize);

            using var decompressor = new Decompressor();
            byte[] body = decompressor.Unwrap(bodyCompressed).ToArray();

            ParseBody(manifest, body);
            return manifest;
        }

        private void ParseBody(RmanManifest manifest, byte[] body)
        {
            int rootOffset = BinaryPrimitives.ReadInt32LittleEndian(body);
            int vtableOffset = rootOffset - BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(rootOffset));
            ushort vtableSize = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(vtableOffset));

            int GetFieldOffset(int index) { int entryOffset = 4 + (index * 2); if (entryOffset >= vtableSize) return 0; return BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(vtableOffset + entryOffset)); }

            void ReadVector(int fieldIndex, Action<int> readElement)
            {
                int offset = GetFieldOffset(fieldIndex);
                if (offset == 0) return;
                int vectorPos = rootOffset + offset + BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(rootOffset + offset));
                int length = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(vectorPos));
                for (int i = 0; i < length; i++)
                    readElement(vectorPos + 4 + (i * 4));
            }

            ReadVector(0, pos => {
                int bundlePos = pos + BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(pos));
                var bundle = new RmanBundle { BundleId = ReadUInt64(bundlePos, 0) };
                int chunksVectorPos = GetVectorPos(bundlePos, 1);
                int chunksCount = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(chunksVectorPos));
                uint currentOffset = 0;
                for (int i = 0; i < chunksCount; i++) {
                    int chunkPos = chunksVectorPos + 4 + (i * 4);
                    chunkPos += BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(chunkPos));
                    var chunk = new RmanChunk {
                        ChunkId = ReadUInt64(chunkPos, 0), CompressedSize = ReadUInt32(chunkPos, 1),
                        UncompressedSize = ReadUInt32(chunkPos, 2), BundleId = bundle.BundleId,
                        BundleOffset = currentOffset
                    };
                    bundle.Chunks.Add(chunk);
                    manifest.Chunks[chunk.ChunkId] = chunk;
                    currentOffset += chunk.CompressedSize;
                }
                manifest.Bundles[bundle.BundleId] = bundle;
            });

            var languages = new Dictionary<byte, string>();
            ReadVector(1, pos => {
                int langPos = pos + BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(pos));
                languages[ReadByte(langPos, 0)] = ReadString(langPos, 1);
            });

            var dirs = new Dictionary<ulong, (ulong parentId, string name)>();
            ReadVector(3, pos => {
                int dirPos = pos + BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(pos));
                dirs[ReadUInt64(dirPos, 0)] = (ReadUInt64(dirPos, 1), ReadString(dirPos, 2));
            });

            ReadVector(2, pos => {
                int filePos = pos + BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(pos));
                var file = new RmanFile { Size = ReadUInt64(filePos, 2), Name = ReadString(filePos, 3) };
                ulong langMask = ReadUInt64(filePos, 4);
                for (int i = 0; i < 64; i++)
                    if ((langMask & (1UL << i)) != 0 && languages.TryGetValue((byte)(i + 1), out var langName))
                        file.Languages.Add(langName);

                int chunksIdsVectorPos = GetVectorPos(filePos, 7);
                int chunksIdsCount = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(chunksIdsVectorPos));
                ulong fileOffset = 0;
                for (int i = 0; i < chunksIdsCount; i++) {
                    ulong chunkId = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(chunksIdsVectorPos + 4 + (i * 8)));
                    if (manifest.Chunks.TryGetValue(chunkId, out var chunk)) {
                        file.Chunks.Add(new RmanChunk {
                            ChunkId = chunk.ChunkId, BundleId = chunk.BundleId,
                            CompressedSize = chunk.CompressedSize, UncompressedSize = chunk.UncompressedSize,
                            BundleOffset = chunk.BundleOffset, FileOffset = fileOffset
                        });
                        fileOffset += chunk.UncompressedSize;
                    }
                }

                ulong dirId = ReadUInt64(filePos, 1);
                while (dirId != 0 && dirs.TryGetValue(dirId, out var dirInfo)) {
                    file.Name = $"{dirInfo.name}/{file.Name}";
                    dirId = dirInfo.parentId;
                }
                manifest.Files.Add(file);
            });

            #region Helpers
            int GetFieldOffsetLocal(int tablePos, int index) { int vtablePosLocal = tablePos - BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(tablePos)); ushort vSize = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(vtablePosLocal)); int fieldOff = 4 + (index * 2); return fieldOff >= vSize ? 0 : BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(vtablePosLocal + fieldOff)); }
            ulong ReadUInt64(int tablePos, int index) { int off = GetFieldOffsetLocal(tablePos, index); return off == 0 ? 0 : BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(tablePos + off)); }
            uint ReadUInt32(int tablePos, int index) { int off = GetFieldOffsetLocal(tablePos, index); return off == 0 ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(tablePos + off)); }
            byte ReadByte(int tablePos, int index) { int off = GetFieldOffsetLocal(tablePos, index); return off == 0 ? (byte)0 : body[tablePos + off]; }
            string ReadString(int tablePos, int index) { int off = GetFieldOffsetLocal(tablePos, index); if (off == 0) return ""; int stringPos = tablePos + off + BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(tablePos + off)); int length = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(stringPos)); return System.Text.Encoding.UTF8.GetString(body.AsSpan(stringPos + 4, length)); }
            int GetVectorPos(int tablePos, int index) { int off = GetFieldOffsetLocal(tablePos, index); return off == 0 ? 0 : tablePos + off + BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(tablePos + off)); }
            #endregion
        }

        public List<RmanFile> GetFilesToUpdate(
            RmanManifest manifest, 
            string outputDir, 
            string filter = null, 
            List<string> locales = null, 
            bool includeNeutral = true)
        {
            var regex = filter != null ? new Regex(filter, RegexOptions.IgnoreCase) : null;
            bool hasRequestedLocales = locales != null && locales.Any();

            // 1. Filtrar candidatos según criterios de Riot
            var candidates = manifest.Files.Where(f => {
                if (regex != null && !regex.IsMatch(f.Name)) return false;
                bool fileHasLanguages = f.Languages.Any();
                if (hasRequestedLocales) {
                    if (fileHasLanguages) {
                        if (!f.Languages.Any(l => locales.Contains(l, StringComparer.OrdinalIgnoreCase))) return false;
                    }
                    else if (!includeNeutral) return false;
                } else {
                    if (fileHasLanguages || !includeNeutral) return false;
                }
                return true;
            }).ToList();

            // 2. Verificar contra disco (Fase silenciosa de escaneo)
            var toUpdate = new List<RmanFile>();
            foreach (var file in candidates) {
                string targetPath = file.Name;
                if (targetPath.StartsWith("Game/", StringComparison.OrdinalIgnoreCase) && outputDir.EndsWith("Game", StringComparison.OrdinalIgnoreCase))
                    targetPath = targetPath.Substring(5);

                string fullPath = Path.Combine(outputDir, targetPath);
                if (!File.Exists(fullPath) || (ulong)new FileInfo(fullPath).Length != file.Size)
                    toUpdate.Add(file);
            }
            return toUpdate;
        }

        public async Task<bool> DownloadAssetsAsync(
            List<RmanFile> filesToUpdate,
            string outputDir,
            int maxThreads = 4,
            CancellationToken ct = default,
            Action<string, int, int> progressCallback = null)
        {
            if (filesToUpdate.Count == 0) return false;

            int completedCount = 0;
            int total = filesToUpdate.Count;

            using var semaphore = new SemaphoreSlim(maxThreads);
            var tasks = filesToUpdate.Select(async file => {
                await semaphore.WaitAsync(ct);
                try {
                    string targetPath = file.Name;
                    if (targetPath.StartsWith("Game/", StringComparison.OrdinalIgnoreCase) && outputDir.EndsWith("Game", StringComparison.OrdinalIgnoreCase))
                        targetPath = targetPath.Substring(5);

                    await DownloadSingleFileAsync(file, outputDir, targetPath, ct);
                }
                finally {
                    int current = Interlocked.Increment(ref completedCount);
                    progressCallback?.Invoke(file.Name, current, total);
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            return true;
        }

        private async Task DownloadSingleFileAsync(RmanFile file, string outputDir, string targetPath, CancellationToken ct)
        {
            string fullPath = Path.Combine(outputDir, targetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

            using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, true);
            fs.SetLength((long)file.Size);

            var bundleGroups = file.Chunks.GroupBy(c => c.BundleId);
            using var decompressor = new Decompressor();

            foreach (var bundleGroup in bundleGroups) {
                string bundleUrl = $"{DefaultBundleUrl}/{bundleGroup.Key:X16}.bundle";
                var chunks = bundleGroup.OrderBy(c => c.BundleOffset).ToList();

                int i = 0;
                while (i < chunks.Count) {
                    ct.ThrowIfCancellationRequested();
                    int startIdx = i;
                    ulong totalCompressedSize = chunks[startIdx].CompressedSize;
                    while (i + 1 < chunks.Count && chunks[i + 1].BundleOffset == (chunks[i].BundleOffset + chunks[i].CompressedSize)) {
                        totalCompressedSize += chunks[i + 1].CompressedSize;
                        i++;
                    }

                    byte[] compressedBlock = await DownloadBundleRangeWithRetryAsync(bundleUrl, chunks[startIdx].BundleOffset, totalCompressedSize, ct);
                    int blockOffset = 0;
                    for (int j = startIdx; j <= i; j++) {
                        var decompressed = decompressor.Unwrap(compressedBlock.AsSpan(blockOffset, (int)chunks[j].CompressedSize)).ToArray();
                        fs.Seek((long)chunks[j].FileOffset, SeekOrigin.Begin);
                        await fs.WriteAsync(decompressed, ct);
                        blockOffset += (int)chunks[j].CompressedSize;
                    }
                    i++;
                }
            }
            await fs.FlushAsync(ct);
        }

        private async Task<byte[]> DownloadBundleRangeWithRetryAsync(string url, ulong offset, ulong length, CancellationToken ct)
        {
            int retries = 3;
            while (true) {
                try {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue((long)offset, (long)(offset + length - 1));
                    using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsByteArrayAsync(ct);
                }
                catch (Exception) when (retries > 0) { retries--; await Task.Delay(1000, ct); }
                catch { throw; }
            }
        }
    }
}
