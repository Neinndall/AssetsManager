using System;
using System.IO;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Explorer;

namespace AssetsManager.Services.Explorer
{
    /// <summary>
    /// Defines how the assets should be exported to the disk.
    /// </summary>
    public enum WadExportMode
    {
        /// <summary>
        /// Preserves the original format (raw bytes from the WAD/Chunk).
        /// </summary>
        Original,

        /// <summary>
        /// Performs smart conversions (e.g., textures to PNG, audio to MP3/OGG).
        /// </summary>
        Smart
    }

    /// <summary>
    /// Unified service for raw extraction of assets to the disk.
    /// Handles only raw extraction (Original Mode) to preserve data integrity.
    /// </summary>
    public class WadExportService
    {
        private readonly LogService _logService;
        private readonly WadContentProvider _wadContentProvider;
        private readonly WadNodeLoaderService _wadNodeLoaderService;
        private readonly DirectoriesCreator _directoriesCreator;

        public WadExportService(
            LogService logService,
            WadContentProvider wadContentProvider,
            WadNodeLoaderService wadNodeLoaderService,
            DirectoriesCreator directoriesCreator)
        {
            _logService = logService;
            _wadContentProvider = wadContentProvider;
            _wadNodeLoaderService = wadNodeLoaderService;
            _directoriesCreator = directoriesCreator;
        }

        #region Traversal & Total Calculation

        public async Task<int> CalculateTotalAsync(
            IEnumerable<FileSystemNodeModel> nodes,
            ObservableRangeCollection<FileSystemNodeModel> rootNodes,
            string currentRootPath,
            CancellationToken cancellationToken)
        {
            int count = 0;
            foreach (var node in nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (node.Type == NodeType.VirtualFile || node.Type == NodeType.RealFile || node.Type == NodeType.WemFile || node.Type == NodeType.SoundBank)
                {
                    count++;
                }
                else if (node.Type == NodeType.AudioEvent || node.Type == NodeType.VirtualDirectory || node.Type == NodeType.RealDirectory || node.Type == NodeType.WadFile)
                {
                    if ((node.Type == NodeType.VirtualDirectory || node.Type == NodeType.WadFile) &&
                        node.Children.Count == 1 && node.Children[0].Name == "Loading...")
                    {
                        var loadedChildren = await _wadNodeLoaderService.LoadChildrenAsync(node, cancellationToken);
                        node.Children.ReplaceRange(loadedChildren);
                    }
                    count += await CalculateTotalAsync(node.Children, rootNodes, currentRootPath, cancellationToken);
                }
            }
            return count;
        }

        #endregion

        #region Export Orchestration

        public async Task<int> ExportNodesAsync(
            List<FileSystemNodeModel> nodes,
            string destinationPath,
            ObservableRangeCollection<FileSystemNodeModel> rootNodes,
            string currentRootPath,
            CancellationToken cancellationToken,
            Action<int, int, string> onProgress = null,
            Action<string> onFileSaved = null)
        {
            int totalFiles = await CalculateTotalAsync(nodes, rootNodes, currentRootPath, cancellationToken);
            onProgress?.Invoke(0, totalFiles, null);

            int processedCount = 0;
            foreach (var node in nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await ExportAsync(node, destinationPath, cancellationToken,
                    (path) =>
                    {
                        processedCount++;
                        string fileName = Path.GetFileName(path);
                        onProgress?.Invoke(processedCount, totalFiles, fileName);
                        onFileSaved?.Invoke(path);
                    });
            }

            return processedCount;
        }

        public async Task ExportAsync(
            FileSystemNodeModel node,
            string destinationPath,
            CancellationToken cancellationToken,
            Action<string> onFileSavedCallback = null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. Directory Traversal
            if (node.Type == NodeType.WadFile || node.Type == NodeType.VirtualDirectory || node.Type == NodeType.RealDirectory)
            {
                string cleanName = PathUtils.GetLogName(node.Name);
                string currentDestinationPath = Path.Combine(destinationPath, PathUtils.SanitizeName(cleanName));
                _directoriesCreator.CreateDirectory(currentDestinationPath);

                // Ensure children are loaded
                if (node.Children.Count == 1 && node.Children[0].Name == "Loading...")
                {
                    var loadedChildren = await _wadNodeLoaderService.LoadChildrenAsync(node, cancellationToken);
                    node.Children.ReplaceRange(loadedChildren);
                }

                foreach (var child in node.Children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ExportAsync(child, currentDestinationPath, cancellationToken, onFileSavedCallback);
                }
                return;
            }

            // 2. Single Raw File Handling
            await HandleRawFileExtractionAsync(node, destinationPath, cancellationToken, onFileSavedCallback);
        }

        private async Task HandleRawFileExtractionAsync(
            FileSystemNodeModel node,
            string destinationPath,
            CancellationToken cancellationToken,
            Action<string> onFileSavedCallback)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] fileBytes;
            if (node.Type == NodeType.RealFile ||
                (node.Type == NodeType.SoundBank && File.Exists(node.VirtualPath)))
                fileBytes = await File.ReadAllBytesAsync(node.VirtualPath, cancellationToken);
            else if (node.Type == NodeType.WemFile)
                fileBytes = await _wadContentProvider.GetWemFileBytesAsync(node, cancellationToken);
            else
                fileBytes = await _wadContentProvider.GetVirtualFileBytesAsync(node, cancellationToken);

            if (fileBytes == null) return;

            string fileName = node.Name;
            string filePath = PathUtils.GetUniqueFilePath(destinationPath, fileName);
            await File.WriteAllBytesAsync(filePath, fileBytes, cancellationToken);
            onFileSavedCallback?.Invoke(filePath);
        }

        #endregion
    }
}
