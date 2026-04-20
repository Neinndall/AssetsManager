using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using BCnEncoder.Shared;
using System.Runtime.InteropServices;
using LeagueToolkit.Core.Renderer;
using LeagueToolkit.Core.Wad;
using AssetsManager.Services.Parsers;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Wad;

namespace AssetsManager.Services.Explorer
{
    public class WadContentProvider
    {
        private readonly LogService _logService;
        private readonly WadNodeLoaderService _wadNodeLoaderService;
        private readonly DirectoriesCreator _directoriesCreator;

        public WadContentProvider(LogService logService, WadNodeLoaderService wadNodeLoaderService, DirectoriesCreator directoriesCreator)
        {
            _logService = logService;
            _wadNodeLoaderService = wadNodeLoaderService;
            _directoriesCreator = directoriesCreator;
        }

        // Obtiene los bytes descomprimidos de un fichero virtual sin guardarlo, para usar en previsualizaciones.
        public Task<byte[]> GetVirtualFileBytesAsync(FileSystemNodeModel fileNode, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (!string.IsNullOrEmpty(fileNode.BackupChunkPath))
                    {
                        // MODO BACKUP: Carga directa y exclusiva de chunks guardados.
                        byte[] compressedData = File.ReadAllBytes(fileNode.BackupChunkPath);
                        bool useOld = fileNode.BackupChunkPath.Contains(Path.Combine("wad_chunks", "old"));
                        var compressionType = useOld ? fileNode.ChunkDiff.OldCompressionType : fileNode.ChunkDiff.NewCompressionType;
                        return WadChunkUtils.DecompressChunk(compressedData, compressionType);
                    }

                    // MODO LIVE: Solo si no hay backup (Comparator o Explorer local).
                    using var wadFile = new WadFile(fileNode.SourceWadPath);
                    if (wadFile.Chunks.TryGetValue(fileNode.SourceChunkPathHash, out var chunk))
                    {
                        using var decompressedDataOwner = wadFile.LoadChunkDecompressed(chunk);
                        return decompressedDataOwner.Span.ToArray();
                    }

                    return null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logService.LogError(ex, $"Failed to get bytes for virtual file: {fileNode.FullPath}");
                    return null;
                }
            }, cancellationToken);
        }

        // Obtiene los bytes de un lado específico (Old o New) de un diff, gestionando tanto WADs como Backups.
        public async Task<byte[]> GetDiffSideBytesAsync(SerializableChunkDiff diff, string lolPath, bool isOld, CancellationToken cancellationToken = default)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.IsNullOrEmpty(diff.BackupChunkPath))
                {
                    // MODO BACKUP
                    string root = GetBackupRoot(diff.BackupChunkPath);
                    string chunkDir = isOld ? "old" : "new";
                    ulong hash = isOld ? diff.OldPathHash : diff.NewPathHash;
                    string chunkPath = Path.Combine(root, "wad_chunks", chunkDir, diff.SourceWadFile, $"{hash:X16}.chunk");

                    if (!File.Exists(chunkPath)) return null;

                    return await Task.Run(() =>
                    {
                        byte[] compressedData = File.ReadAllBytes(chunkPath);
                        var compressionType = isOld ? diff.OldCompressionType : diff.NewCompressionType;
                        return WadChunkUtils.DecompressChunk(compressedData, compressionType ?? WadChunkCompression.None);
                    }, cancellationToken);
                }

                // MODO LIVE
                if (!string.IsNullOrEmpty(lolPath))
                {
                    string wadPath = Path.Combine(lolPath, diff.SourceWadFile);
                    ulong hash = isOld ? diff.OldPathHash : diff.NewPathHash;
                    if (hash != 0) return await DecompressChunkByHashAsync(wadPath, hash, cancellationToken);
                }

                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logService.LogError(ex, $"Failed to get bytes for diff: {diff.Path}");
                return null;
            }
        }

        private string GetBackupRoot(string chunkPath)
        {
            if (string.IsNullOrEmpty(chunkPath)) return string.Empty;
            int index = chunkPath.IndexOf("wad_chunks", StringComparison.OrdinalIgnoreCase);
            return index != -1 ? chunkPath.Substring(0, index) : Path.GetDirectoryName(Path.GetDirectoryName(chunkPath));
        }

        // Obtiene los bytes descomprimidos de un diff (lado predominante para previsualización)
        public async Task<byte[]> GetDiffFileBytesAsync(SerializableChunkDiff diff, string oldLolPath, string newLolPath, CancellationToken cancellationToken = default)
        {
            bool useOld = (diff.Type == ChunkDiffType.Removed);
            string basePath = useOld ? oldLolPath : newLolPath;
            return await GetDiffSideBytesAsync(diff, basePath, useOld, cancellationToken);
        }

        // Genera una miniatura para un diff, integrando extracción y procesado de imagen
        public async Task<ImageSource> GetDiffThumbnailAsync(SerializableChunkDiff diff, string oldLolPath, string newLolPath, int maxWidth = 0, CancellationToken cancellationToken = default)
        {
            byte[] data = await GetDiffFileBytesAsync(diff, oldLolPath, newLolPath, cancellationToken);
            if (data == null) return null;

            return await Task.Run(() =>
            {
                try
                {
                    string ext = Path.GetExtension(diff.Path).ToLowerInvariant();
                    using var ms = new MemoryStream(data);
                    int? size = maxWidth > 0 ? maxWidth : null;
                    return TextureUtils.LoadTexture(ms, ext, size, size);
                }
                catch { return null; }
            }, cancellationToken);
        }

        // Orquesta la extracción de datos de audio .wem desde su contenedor (.bnk o .wpk).
        public async Task<byte[]> GetWemFileBytesAsync(FileSystemNodeModel node, CancellationToken cancellationToken = default)
        {
            if (node.Type != NodeType.WemFile) return null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] containerData;

                if (!string.IsNullOrEmpty(node.BackupChunkPath))
                {
                    // MODO BACKUP: Carga del contenedor desde el chunk.
                    byte[] compressedData = File.ReadAllBytes(node.BackupChunkPath);
                    var compressionType = node.ChunkDiff?.NewCompressionType ?? WadChunkCompression.None;
                    containerData = WadChunkUtils.DecompressChunk(compressedData, compressionType);
                }
                else
                {
                    // MODO LIVE
                    containerData = await DecompressChunkByHashAsync(node.SourceWadPath, node.SourceChunkPathHash, cancellationToken);
                }

                if (containerData == null) return null;

                if (node.AudioSource == AudioSourceType.Bnk)
                {
                    if (node.WemSize == 0) return null;
                    byte[] wemData = new byte[node.WemSize];
                    Array.Copy(containerData, (int)node.WemOffset, wemData, 0, (int)node.WemSize);
                    return wemData;
                }
                else // Wpk
                {
                    using var wpkStream = new MemoryStream(containerData);
                    var wpk = WpkParser.Parse(wpkStream, _logService);
                    var wem = wpk.Wems.FirstOrDefault(w => w.Id == node.WemId);
                    if (wem == null) return null;
                    
                    wpkStream.Seek(wem.Offset, SeekOrigin.Begin);
                    return new BinaryReader(wpkStream).ReadBytes((int)wem.Size);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logService.LogError(ex, $"Failed to extract WEM file '{node.FullPath}'.");
                return null;
            }
        }

        // Encuentra un chunk en un WAD por su hash y devuelve su contenido descomprimido.
        private Task<byte[]> DecompressChunkByHashAsync(string wadPath, ulong chunkHash, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using var wadFile = new WadFile(wadPath);
                    if (!wadFile.Chunks.TryGetValue(chunkHash, out var chunk))
                    {
                        _logService.LogWarning($"Chunk with hash {chunkHash:x16} not found in {wadPath}");
                        return null;
                    }
                    using var decompressedDataOwner = wadFile.LoadChunkDecompressed(chunk);
                    return decompressedDataOwner.Span.ToArray();
                }
                catch (OperationCanceledException)
                {
                    _logService.LogWarning($"Decompression of chunk {chunkHash:x16} was cancelled.");
                    throw;
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Failed to get bytes for virtual file with hash: {chunkHash:x16}");
                    return null;
                }
            }, cancellationToken);
        }
    }
}
