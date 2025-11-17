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
using AssetsManager.Views.Models;

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

        // Dirige el proceso de extracción al método adecuado según el tipo de nodo.
        public async Task ExtractNodeAsync(FileSystemNodeModel node, string destinationPath)
        {
            switch (node.Type)
            {
                case NodeType.SoundBank:
                case NodeType.VirtualFile:
                    await ExtractVirtualFileAsync(node, destinationPath);
                    break;
                case NodeType.VirtualDirectory:
                case NodeType.WadFile:
                    await ExtractVirtualDirectoryAsync(node, destinationPath);
                    break;
                case NodeType.RealDirectory:
                    await ExtractRealDirectoryAsync(node, destinationPath);
                    break;
            }
        }

        // Extrae recursivamente un directorio que existe virtualmente dentro de un archivo WAD.
        private async Task ExtractVirtualDirectoryAsync(FileSystemNodeModel dirNode, string destinationPath)
        {
            string newDirPath = Path.Combine(destinationPath, SanitizeName(dirNode.Name));
            Directory.CreateDirectory(newDirPath);

            // If children are not loaded (i.e., it's the dummy node), load them.
            if (dirNode.Children.Count == 1 && dirNode.Children[0].Name == "Loading...")
            {
                var loadedChildren = await _wadNodeLoaderService.LoadChildrenAsync(dirNode, CancellationToken.None);
                dirNode.Children.Clear(); // Remove dummy node
                foreach (var child in loadedChildren)
                {
                    dirNode.Children.Add(child);
                }
            }

            // Now, recursively call ExtractNodeAsync on the actual children.
            foreach (var childNode in dirNode.Children)
            {
                await ExtractNodeAsync(childNode, newDirPath);
            }
        }

        // Copia recursivamente un directorio que ya existe en el disco físico (modo directorio).
        private async Task ExtractRealDirectoryAsync(FileSystemNodeModel dirNode, string destinationPath)
        {
            string newDirPath = Path.Combine(destinationPath, SanitizeName(dirNode.Name));
            Directory.CreateDirectory(newDirPath);

            // The tree is already fully loaded in memory, so we can just iterate through the children.
            foreach (var childNode in dirNode.Children)
            {
                await ExtractNodeAsync(childNode, newDirPath);
            }
        }

        // Extrae un único fichero virtual (desde un WAD o un backup) y lo guarda en el disco.
        private Task ExtractVirtualFileAsync(FileSystemNodeModel fileNode, string destinationPath)
        {
            return Task.Run(() =>
            {
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

                    string destFilePath = Path.Combine(destinationPath, SanitizeName(fileNode.Name));
                    File.WriteAllBytes(destFilePath, decompressedData);
                }
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Failed to extract virtual file: {fileNode.FullPath}");
                }
            });
        }

        // Obtiene los bytes descomprimidos de un fichero virtual sin guardarlo, para usar en previsualizaciones.
        public Task<byte[]> GetVirtualFileBytesAsync(FileSystemNodeModel fileNode)
        {
            return Task.Run(() =>
            {
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
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Failed to get bytes for virtual file: {fileNode.FullPath}");
                    return null;
                }
            });
        }

        // Orquesta la extracción de datos de audio .wem desde su contenedor (.bnk o .wpk).
        public async Task<byte[]> GetWemFileBytesAsync(FileSystemNodeModel node)
        {
            if (node.Type != NodeType.WemFile)
            {
                _logService.LogWarning($"Attempted to get WEM bytes from a non-WEM file: {node.Name}");
                return null;
            }

            try
            {
                byte[] containerData = await DecompressChunkByHashAsync(node.SourceWadPath, node.SourceChunkPathHash);
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
                    });
                }
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, $"Failed to extract WEM file '{node.FullPath}'.");
                return null;
            }
        }

        // Encuentra un chunk en un WAD por su hash y devuelve su contenido descomprimido.
        private Task<byte[]> DecompressChunkByHashAsync(string wadPath, ulong chunkHash)
        {
            return Task.Run(() =>
            {
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
                catch (Exception ex)
                {
                    _logService.LogError(ex, $"Failed to get bytes for virtual file with hash: {chunkHash:x16}");
                    return null;
                }
            });
        }

        // Limpia y sanea nombres de fichero para que sean compatibles con el sistema de archivos.
        public string SanitizeName(string name)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(name.Where(c => !invalidChars.Contains(c)).ToArray()).Trim();

            const int MaxLength = 240; // A bit less than 255 to be safe.
            if (sanitized.Length > MaxLength)
            {
                var extension = Path.GetExtension(sanitized);
                var newLength = MaxLength - extension.Length;
                sanitized = sanitized.Substring(0, newLength) + extension;
            }
            return sanitized;
        }
    }
}
