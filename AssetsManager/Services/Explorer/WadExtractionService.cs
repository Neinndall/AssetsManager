using LeagueToolkit.Core.Wad;
using AssetsManager.Services.Comparator;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using BCnEncoder.Shared;
using System.Runtime.InteropServices;
using LeagueToolkit.Core.Renderer;

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

        public async Task ExtractNodeAsync(FileSystemNodeModel node, string destinationPath)
        {
            switch (node.Type)
            {
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

        private async Task ExtractVirtualDirectoryAsync(FileSystemNodeModel dirNode, string destinationPath)
        {
            string newDirPath = Path.Combine(destinationPath, SanitizeName(dirNode.Name));
            Directory.CreateDirectory(newDirPath);

            // If children are not loaded (i.e., it's the dummy node), load them.
            if (dirNode.Children.Count == 1 && dirNode.Children[0].Name == "Loading...")
            {
                var loadedChildren = await _wadNodeLoaderService.LoadChildrenAsync(dirNode);
                dirNode.Children.Clear(); // Remove dummy node
                foreach(var child in loadedChildren)
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

        private Task ExtractVirtualFileAsync(FileSystemNodeModel fileNode, string destinationPath)
        {
            return Task.Run(() =>
            {
                try
                {
                    byte[] decompressedData;

                    if (fileNode.SourceWadPath.EndsWith(".chunk"))
                    {
                        byte[] compressedData = File.ReadAllBytes(fileNode.SourceWadPath);
                        var compressionType = fileNode.ChunkDiff.Type == ChunkDiffType.Removed ? fileNode.ChunkDiff.OldCompressionType : fileNode.ChunkDiff.NewCompressionType;
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

        private string SanitizeName(string name)
        {
            const int MaxLength = 240; // A bit less than 255 to be safe.
            if (name.Length > MaxLength)
            {
                var extension = Path.GetExtension(name);
                var newLength = MaxLength - extension.Length;
                var sanitizedName = name.Substring(0, newLength) + extension;
                return sanitizedName;
            }
            return name;
        }
    }
}