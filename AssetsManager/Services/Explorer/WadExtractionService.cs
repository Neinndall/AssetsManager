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

namespace AssetsManager.Services.Explorer
{
    public class WadExtractionService
    {
        private readonly LogService _logService;
        private readonly WadNodeLoaderService _wadNodeLoaderService;

        public WadExtractionService(LogService logService, WadNodeLoaderService wadNodeLoaderService)
        {
            _logService = logService;
            _wadNodeLoaderService = wadNodeLoaderService;
        }

        public async Task<int> CalculateTotalAsync(IEnumerable<FileSystemNodeModel> nodes, CancellationToken cancellationToken)
        {
            int count = 0;
            foreach (var node in nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (node.Type == NodeType.VirtualFile || node.Type == NodeType.RealFile || node.Type == NodeType.WemFile || node.Type == NodeType.SoundBank)
                {
                    count++;
                }
                else
                {
                    if ((node.Type == NodeType.VirtualDirectory || node.Type == NodeType.WadFile) && 
                        node.Children.Count == 1 && node.Children[0].Name == "Loading...")
                    {
                        var loadedChildren = await _wadNodeLoaderService.LoadChildrenAsync(node, cancellationToken);
                        node.Children.Clear();
                        foreach (var child in loadedChildren) node.Children.Add(child);
                    }
                    count += await CalculateTotalAsync(node.Children, cancellationToken);
                }
            }
            return count;
        }

        // Dirige el proceso de extracción al método adecuado según el tipo de nodo.
        public async Task ExtractNodeAsync(FileSystemNodeModel node, string destinationPath, CancellationToken cancellationToken, Action<string> onFileExtracted = null)
        {
            switch (node.Type)
            {
                case NodeType.SoundBank:
                case NodeType.VirtualFile:
                    await ExtractVirtualFileAsync(node, destinationPath, cancellationToken);
                    onFileExtracted?.Invoke(node.Name);
                    break;
                case NodeType.VirtualDirectory:
                case NodeType.WadFile:
                    await ExtractVirtualDirectoryAsync(node, destinationPath, cancellationToken, onFileExtracted);
                    break;
                case NodeType.AudioEvent:
                    await ExtractAudioEventDirectoryAsync(node, destinationPath, cancellationToken, onFileExtracted);
                    break;
                case NodeType.WemFile:
                    await ExtractWemFileAsync(node, destinationPath, cancellationToken);
                    onFileExtracted?.Invoke(node.Name);
                    break;
                case NodeType.RealDirectory:
                    await ExtractRealDirectoryAsync(node, destinationPath, cancellationToken, onFileExtracted);
                    break;
            }
        }

        // Extrae el contenido de una carpeta de evento de audio (.wem) a un nuevo directorio.
        private async Task ExtractAudioEventDirectoryAsync(FileSystemNodeModel dirNode, string destinationPath, CancellationToken cancellationToken, Action<string> onFileExtracted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string newDirPath = Path.Combine(destinationPath, PathUtils.SanitizeName(dirNode.Name));
            Directory.CreateDirectory(newDirPath);

            foreach (var childNode in dirNode.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (childNode.Type == NodeType.WemFile)
                {
                    await ExtractWemFileAsync(childNode, newDirPath, cancellationToken);
                    onFileExtracted?.Invoke(childNode.Name);
                }
            }
        }

        // Extrae un único fichero .wem y lo guarda en el disco.
        private async Task ExtractWemFileAsync(FileSystemNodeModel fileNode, string destinationPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wemData = await GetWemFileBytesAsync(fileNode, cancellationToken);
            if (wemData != null)
            {
                string destFilePath = PathUtils.GetUniqueFilePath(destinationPath, fileNode.Name);
                await File.WriteAllBytesAsync(destFilePath, wemData, cancellationToken);
            }
        }

        // Extrae recursivamente un directorio que existe virtualmente dentro de un archivo WAD.
        private async Task ExtractVirtualDirectoryAsync(FileSystemNodeModel dirNode, string destinationPath, CancellationToken cancellationToken, Action<string> onFileExtracted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string newDirPath = Path.Combine(destinationPath, PathUtils.SanitizeName(dirNode.Name));
            Directory.CreateDirectory(newDirPath);

            // If children are not loaded (i.e., it's the dummy node), load them.
            if (dirNode.Children.Count == 1 && dirNode.Children[0].Name == "Loading...")
            {
                var loadedChildren = await _wadNodeLoaderService.LoadChildrenAsync(dirNode, cancellationToken);
                dirNode.Children.Clear(); // Remove dummy node
                foreach (var child in loadedChildren)
                {
                    dirNode.Children.Add(child);
                }
            }

            // Now, recursively call ExtractNodeAsync on the actual children.
            foreach (var childNode in dirNode.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExtractNodeAsync(childNode, newDirPath, cancellationToken, onFileExtracted);
            }
        }

        // Copia recursivamente un directorio que ya existe en el disco físico (modo directorio).
        private async Task ExtractRealDirectoryAsync(FileSystemNodeModel dirNode, string destinationPath, CancellationToken cancellationToken, Action<string> onFileExtracted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string newDirPath = Path.Combine(destinationPath, PathUtils.SanitizeName(dirNode.Name));
            Directory.CreateDirectory(newDirPath);

            // The tree is already fully loaded in memory, so we can just iterate through the children.
            foreach (var childNode in dirNode.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExtractNodeAsync(childNode, newDirPath, cancellationToken, onFileExtracted);
            }
        }

        // Extrae un único fichero virtual (desde un WAD o un backup) y lo guarda en el disco.
        private Task ExtractVirtualFileAsync(FileSystemNodeModel fileNode, string destinationPath, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    byte[] decompressedData;

                    if (!string.IsNullOrEmpty(fileNode.BackupChunkPath))
                    {
                        byte[] compressedData = File.ReadAllBytes(fileNode.BackupChunkPath);
                        bool useOld = fileNode.BackupChunkPath.Contains(Path.Combine("wad_chunks", "old"));
                        var compressionType = useOld ? fileNode.ChunkDiff.OldCompressionType : fileNode.ChunkDiff.NewCompressionType;
                        decompressedData = WadChunkUtils.DecompressChunk(compressedData, compressionType);
                    }
                    else
                    {
                        using var wadFile = new WadFile(fileNode.SourceWadPath);
                        if (!wadFile.Chunks.TryGetValue(fileNode.SourceChunkPathHash, out var chunk))
                        {
                            _logService.LogWarning($"Chunk with hash {fileNode.SourceChunkPathHash:x16} not found in {fileNode.SourceWadPath}");
                            return;
                        }
                        using var decompressedDataOwner = wadFile.LoadChunkDecompressed(chunk);
                        decompressedData = decompressedDataOwner.Span.ToArray();
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    string destFilePath = PathUtils.GetUniqueFilePath(destinationPath, fileNode.Name);
                    File.WriteAllBytes(destFilePath, decompressedData);
                }
                catch (OperationCanceledException)
                {
                    _logService.LogWarning("Extraction of virtual file was cancelled.");
                    throw;
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Failed to extract virtual file: {fileNode.FullPath}");
                }
            }, cancellationToken);
        }

        // Obtiene los bytes descomprimidos de un fichero virtual sin guardarlo, para usar en previsualizaciones.
        public Task<byte[]> GetVirtualFileBytesAsync(FileSystemNodeModel fileNode, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (fileNode.Type != NodeType.VirtualFile && fileNode.Type != NodeType.SoundBank)
                    {
                        _logService.LogWarning($"Attempted to get bytes from a non-virtual file: {fileNode.Name}");
                        return null;
                    }

                    byte[] decompressedData;

                    if (!string.IsNullOrEmpty(fileNode.BackupChunkPath))
                    {
                        byte[] compressedData = File.ReadAllBytes(fileNode.BackupChunkPath);
                        bool useOld = fileNode.BackupChunkPath.Contains(Path.Combine("wad_chunks", "old"));
                        var compressionType = useOld ? fileNode.ChunkDiff.OldCompressionType : fileNode.ChunkDiff.NewCompressionType;
                        decompressedData = WadChunkUtils.DecompressChunk(compressedData, compressionType);
                    }
                    else
                    {
                        using var wadFile = new WadFile(fileNode.SourceWadPath);
                        if (!wadFile.Chunks.TryGetValue(fileNode.SourceChunkPathHash, out var chunk))
                        {
                            _logService.LogWarning($"Chunk with hash {fileNode.SourceChunkPathHash:x16} not found in {fileNode.SourceWadPath}");
                            return null;
                        }
                        using var decompressedDataOwner = wadFile.LoadChunkDecompressed(chunk);
                        decompressedData = decompressedDataOwner.Span.ToArray();
                    }

                    return decompressedData;
                }
                catch (OperationCanceledException)
                {
                    _logService.LogWarning("Get virtual file bytes was cancelled.");
                    throw;
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Failed to get bytes for virtual file: {fileNode.FullPath}");
                    return null;
                }
            }, cancellationToken);
        }

        // Orquesta la extracción de datos de audio .wem desde su contenedor (.bnk o .wpk).
        public async Task<byte[]> GetWemFileBytesAsync(FileSystemNodeModel node, CancellationToken cancellationToken = default)
        {
            if (node.Type != NodeType.WemFile)
            {
                _logService.LogWarning($"Attempted to get WEM bytes from a non-WEM file: {node.Name}");
                return null;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] containerData = await DecompressChunkByHashAsync(node.SourceWadPath, node.SourceChunkPathHash, cancellationToken);
                if (containerData == null)
                {
                    _logService.LogWarning($"Could not extract container for WEM file {node.Name}");
                    return null;
                }

                if (node.AudioSource == AudioSourceType.Bnk)
                {
                    if (node.WemSize == 0) return null;

                    byte[] wemData = new byte[node.WemSize];
                    Array.Copy(containerData, node.WemOffset, wemData, 0, node.WemSize);
                    return wemData;
                }
                else // Wpk
                {
                    if (node.WemId == 0) return null;

                    return await Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        using var wpkStream = new MemoryStream(containerData);
                        var wpk = WpkParser.Parse(wpkStream, _logService);
                        var wem = wpk.Wems.FirstOrDefault(w => w.Id == node.WemId);
                        if (wem != null)
                        {
                            using var reader = new BinaryReader(wpkStream);
                            wpkStream.Seek(wem.Offset, SeekOrigin.Begin);
                            return reader.ReadBytes((int)wem.Size);
                        }
                        _logService.LogWarning($"WEM file with ID {node.WemId} not found inside its parent WPK.");
                        return null;
                    }, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                _logService.LogWarning($"WEM file extraction was cancelled: {node.FullPath}");
                throw;
            }
            catch (Exception ex)
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
