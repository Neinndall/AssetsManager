using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Media;
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

        public WadContentProvider(
            LogService logService, 
            WadNodeLoaderService wadNodeLoaderService, 
            DirectoriesCreator directoriesCreator)
        {
            _logService = logService;
            _wadNodeLoaderService = wadNodeLoaderService;
            _directoriesCreator = directoriesCreator;
        }

        /// <summary>
        /// Searches for a specific virtual path within all WAD files in a directory using direct hash comparison.
        /// No dependency on HashResolverService.
        /// </summary>
        public async Task<FileSystemNodeModel> FindNodeByVirtualPathAsync(string virtualPath, string gameDataPath)
        {
            return await Task.Run(() =>
            {
                // WAD paths are hashed in lowercase
                string normalizedPath = virtualPath.Replace('\\', '/').ToLowerInvariant();
                ulong targetHash = LeagueToolkit.Hashing.XxHash64Ext.Hash(normalizedPath);

                var wadFiles = Directory.GetFiles(gameDataPath, "*.wad", SearchOption.AllDirectories)
                                              .Concat(Directory.GetFiles(gameDataPath, "*.wad.client", SearchOption.AllDirectories))
                                              .ToList();

                foreach (var wadPath in wadFiles)
                {
                    try
                    {
                        using (var wadFile = new WadFile(wadPath))
                        {
                            if (wadFile.Chunks.TryGetValue(targetHash, out var chunk))
                            {
                                return new FileSystemNodeModel(Path.GetFileName(normalizedPath), false, normalizedPath, wadPath)
                                {
                                    SourceChunkPathHash = chunk.PathHash,
                                    SourceWadPath = wadPath,
                                    Type = NodeType.VirtualFile
                                };
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logService.LogWarning($"Error processing WAD file {wadPath}: {ex.Message}");
                    }
                }

                return null;
            });
        }

        // Obtiene los bytes descomprimidos de un chunk guardado en el backup.
        public async Task<byte[]> GetBackupChunkBytesAsync(string backupRoot, string sourceWad, ulong hash, WadChunkCompression? compressionType, bool isOld, CancellationToken cancellationToken = default)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(backupRoot) || hash == 0) return null;

                string chunkDir = isOld ? "old" : "new";
                string chunkPath = Path.Combine(backupRoot, "wad_chunks", chunkDir, sourceWad ?? string.Empty, $"{hash:X16}.chunk");
                if (!File.Exists(chunkPath)) return null;

                return await Task.Run(() =>
                {
                    byte[] compressedData = File.ReadAllBytes(chunkPath);
                    return WadChunkUtils.DecompressChunk(compressedData, compressionType ?? WadChunkCompression.None);
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logService.LogError(ex, $"Failed to get backup chunk bytes: {sourceWad}/{hash:X16}");
                return null;
            }
        }

        // Obtiene los bytes descomprimidos de un fichero virtual sin guardarlo, para usar en previsualizaciones.
        public async Task<byte[]> GetVirtualFileBytesAsync(FileSystemNodeModel fileNode, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!string.IsNullOrEmpty(fileNode.BackupChunkPath))
                {
                    _logService.LogDebug($"[DATA DIRECTION] Loading from BACKUP directory: 'wad_chunks/{ (fileNode.BackupChunkPath.Contains("old") ? "old" : "new") }'");
                    return await Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // MODO BACKUP: Carga directa y exclusiva de chunks guardados.
                        byte[] compressedData = File.ReadAllBytes(fileNode.BackupChunkPath);
                        bool useOld = fileNode.BackupChunkPath.Contains(Path.Combine("wad_chunks", "old"));
                        var compressionType = useOld ? fileNode.ChunkDiff.OldCompressionType : fileNode.ChunkDiff.NewCompressionType;
                        return WadChunkUtils.DecompressChunk(compressedData, compressionType);
                    }, cancellationToken);
                }

                _logService.LogDebug($"[DATA DIRECTION] Loading from LOCAL installation: '{Path.GetDirectoryName(fileNode.SourceWadPath)}'");
                // MODO LIVE: Delegamos en el método unificado
                return await DecompressChunkByHashAsync(fileNode.SourceWadPath, fileNode.SourceChunkPathHash, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logService.LogError(ex, $"Failed to get bytes for virtual file: {fileNode.VirtualPath}");
                return null;
            }
        }

        // Obtiene los bytes de un lado específico (Old o New) de un diff, gestionando tanto WADs como Backups.
        public async Task<byte[]> GetDiffSideBytesAsync(SerializableChunkDiff diff, string lolPath, bool isOld, CancellationToken cancellationToken = default)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.IsNullOrEmpty(diff.BackupChunkPath))
                {
                    // MODO BACKUP: Delegamos en el método centralizado
                    string root = GetBackupRoot(diff.BackupChunkPath);
                    ulong hash = isOld ? diff.OldPathHash : diff.NewPathHash;
                    var compressionType = isOld ? diff.OldCompressionType : diff.NewCompressionType;

                    return await GetBackupChunkBytesAsync(root, diff.SourceWadFile, hash, compressionType, isOld, cancellationToken);
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

        // Obtiene los bytes de ambos lados de un diff (Old y New) de forma eficiente.
        public async Task<(string DataType, byte[] OldData, byte[] NewData, string OldPath, string NewPath)> GetFullDiffDataAsync(SerializableChunkDiff diff, string oldLolPath, string newLolPath, CancellationToken cancellationToken = default)
        {
            string extension = Path.GetExtension(diff.Path).ToLowerInvariant();
            string dataType = extension.TrimStart('.');

            var oldDataTask = GetDiffSideBytesAsync(diff, oldLolPath, true, cancellationToken);
            var newDataTask = GetDiffSideBytesAsync(diff, newLolPath, false, cancellationToken);

            await Task.WhenAll(oldDataTask, newDataTask);

            byte[] oldData = await oldDataTask;
            byte[] newData = await newDataTask;

            // Verificación de integridad básica según el tipo de cambio
            if (oldData == null && diff.Type != ChunkDiffType.New) return (null, null, null, null, null);
            if (newData == null && diff.Type != ChunkDiffType.Removed) return (null, null, null, null, null);

            return (dataType, oldData, newData, diff.OldPath, diff.NewPath);
        }

        // Obtiene los bytes de dos archivos locales para comparación.
        public async Task<(string DataType, byte[] OldData, byte[] NewData)> GetFileDiffDataAsync(string oldFilePath, string newFilePath)
        {
            string extension = Path.GetExtension(newFilePath ?? oldFilePath).ToLowerInvariant();
            string dataType = extension.TrimStart('.');

            byte[] oldData = File.Exists(oldFilePath) ? await File.ReadAllBytesAsync(oldFilePath) : null;
            byte[] newData = File.Exists(newFilePath) ? await File.ReadAllBytesAsync(newFilePath) : null;

            return (dataType, oldData, newData);
        }

        private string GetBackupRoot(string chunkPath)
        {
            if (string.IsNullOrEmpty(chunkPath)) return string.Empty;

            int index = chunkPath.IndexOf("wad_chunks", StringComparison.OrdinalIgnoreCase);
            if (index != -1) return chunkPath.Substring(0, index);

            string fallback = Path.GetDirectoryName(Path.GetDirectoryName(chunkPath));
            _logService.LogWarning($"'wad_chunks' not found in path. Using fallback root: {fallback}");
            return fallback;
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
                    _logService.LogDebug($"[DATA DIRECTION] Extracting WEM from BACKUP storage: 'wad_chunks/{ (node.BackupChunkPath.Contains("old") ? "old" : "new") }'");
                    // MODO BACKUP: Carga del contenedor desde el chunk.
                    byte[] compressedData = File.ReadAllBytes(node.BackupChunkPath);
                    var compressionType = node.ChunkDiff?.NewCompressionType ?? WadChunkCompression.None;
                    containerData = WadChunkUtils.DecompressChunk(compressedData, compressionType);
                }
                else
                {
                    _logService.LogDebug($"[DATA DIRECTION] Extracting WEM from LOCAL installation: '{Path.GetDirectoryName(node.SourceWadPath)}'");
                    // MODO LIVE
                    containerData = await DecompressChunkByHashAsync(node.SourceWadPath, node.SourceChunkPathHash, cancellationToken);
                }

                if (containerData == null) return null;

                if (node.AudioSource == AudioSourceType.Bnk)
                {
                    if (node.WemSize == 0) return null;
                    // Retornamos el segmento directamente usando Span para evitar copias intermedias (siendo ToArray la copia final necesaria)
                    return containerData.AsSpan((int)node.WemOffset, (int)node.WemSize).ToArray();
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
                _logService.LogError(ex, $"Failed to extract WEM file '{node.VirtualPath}'.");
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

                    // Usamos LoadChunk del WadFile que ya gestiona el stream interno, evitando aperturas dobles.
                    using var compressedDataOwner = wadFile.LoadChunk(chunk);
                    return WadChunkUtils.DecompressChunk(compressedDataOwner.Span, chunk.Compression);
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
