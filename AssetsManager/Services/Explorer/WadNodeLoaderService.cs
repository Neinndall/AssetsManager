using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models;
using LeagueToolkit.Core.Wad;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AssetsManager.Utils;
using AssetsManager.Services.Core;

namespace AssetsManager.Services.Explorer
{
    public class WadNodeLoaderService
    {
        private readonly HashResolverService _hashResolverService;
        private readonly LogService _logService;

        public WadNodeLoaderService(HashResolverService hashResolverService, LogService logService)
        {
            _hashResolverService = hashResolverService;
            _logService = logService;
        }

        public async Task<(List<FileSystemNodeModel> Nodes, string NewLolPath, string OldLolPath)> LoadFromBackupAsync(string jsonPath)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };

            string jsonContent = await File.ReadAllTextAsync(jsonPath);
            var comparisonData = JsonSerializer.Deserialize<WadComparisonData>(jsonContent, options);

            var rootNodes = new List<FileSystemNodeModel>();
            if (comparisonData?.Diffs == null || !comparisonData.Diffs.Any())
            {
                return (rootNodes, null, null);
            }

            string backupRoot = Path.GetDirectoryName(jsonPath);
            var diffsByWad = comparisonData.Diffs.GroupBy(d => d.SourceWadFile);

            foreach (var wadGroup in diffsByWad)
            {
                var wadNode = new FileSystemNodeModel($"{wadGroup.Key} ({wadGroup.Count()})", true, wadGroup.Key, wadGroup.Key);

                var newFiles = wadGroup.Where(d => d.Type == ChunkDiffType.New).ToList();
                if (newFiles.Any())
                {
                    var newFilesNode = new FileSystemNodeModel($"[+] New ({newFiles.Count})", true, "New", wadGroup.Key) { Status = DiffStatus.New };
                    foreach (var file in newFiles)
                    {
                        string chunkPath = Path.Combine(backupRoot, "wad_chunks", "new", $"{file.NewPathHash:X16}.chunk");
                        string resolvedPath = _hashResolverService.ResolveHash(file.NewPathHash);
                        var node = AddNodeToVirtualTree(newFilesNode, resolvedPath, wadGroup.Key, file.NewPathHash, DiffStatus.New);
                        node.BackupChunkPath = chunkPath;
                        node.ChunkDiff = file;
                    }
                    wadNode.Children.Add(newFilesNode);
                }

                var modifiedFiles = wadGroup.Where(d => d.Type == ChunkDiffType.Modified).ToList();
                if (modifiedFiles.Any())
                {
                    var modifiedFilesNode = new FileSystemNodeModel($"[~] Modified ({modifiedFiles.Count})", true, "Modified", wadGroup.Key) { Status = DiffStatus.Modified };
                    foreach (var file in modifiedFiles)
                    {
                        string chunkPath = Path.Combine(backupRoot, "wad_chunks", "new", $"{file.NewPathHash:X16}.chunk");
                        string resolvedPath = _hashResolverService.ResolveHash(file.NewPathHash);
                        var node = AddNodeToVirtualTree(modifiedFilesNode, resolvedPath, wadGroup.Key, file.NewPathHash, DiffStatus.Modified);
                        node.BackupChunkPath = chunkPath;
                        node.ChunkDiff = file;
                    }
                    wadNode.Children.Add(modifiedFilesNode);
                }

                var renamedFiles = wadGroup.Where(d => d.Type == ChunkDiffType.Renamed).ToList();
                if (renamedFiles.Any())
                {
                    var renamedFilesNode = new FileSystemNodeModel($"[>] Renamed ({renamedFiles.Count})", true, "Renamed", wadGroup.Key) { Status = DiffStatus.Renamed };
                    foreach (var file in renamedFiles)
                    {
                        string chunkPath = Path.Combine(backupRoot, "wad_chunks", "new", $"{file.NewPathHash:X16}.chunk");
                        string resolvedPath = _hashResolverService.ResolveHash(file.NewPathHash);
                        var node = AddNodeToVirtualTree(renamedFilesNode, resolvedPath, wadGroup.Key, file.NewPathHash, DiffStatus.Renamed);
                        node.BackupChunkPath = chunkPath;
                        node.OldPath = _hashResolverService.ResolveHash(file.OldPathHash);
                        node.ChunkDiff = file;
                    }
                    wadNode.Children.Add(renamedFilesNode);
                }

                var deletedFiles = wadGroup.Where(d => d.Type == ChunkDiffType.Removed).ToList();
                if (deletedFiles.Any())
                {
                    var deletedFilesNode = new FileSystemNodeModel($"[-] Deleted ({deletedFiles.Count})", true, "Deleted", wadGroup.Key) { Status = DiffStatus.Deleted };
                    foreach (var file in deletedFiles)
                    {
                        string chunkPath = Path.Combine(backupRoot, "wad_chunks", "old", $"{file.OldPathHash:X16}.chunk");
                        string resolvedPath = _hashResolverService.ResolveHash(file.OldPathHash);
                        var node = AddNodeToVirtualTree(deletedFilesNode, resolvedPath, wadGroup.Key, file.OldPathHash, DiffStatus.Deleted);
                        node.BackupChunkPath = chunkPath;
                        node.ChunkDiff = file;
                    }
                    wadNode.Children.Add(deletedFilesNode);
                }

                SortChildrenRecursively(wadNode);
                rootNodes.Add(wadNode);
            }

            return (rootNodes, comparisonData.NewLolPath, comparisonData.OldLolPath);
        }

        private void SortChildrenRecursively(FileSystemNodeModel node)
        {
            if (node.Type != NodeType.VirtualDirectory && node.Type != NodeType.WadFile) return;

            var sortedChildren = node.Children
                .OrderBy(c => c.Type == NodeType.VirtualDirectory ? 0 : 1)
                .ThenBy(c => c.Name)
                .ToList();

            node.Children.Clear();
            foreach (var child in sortedChildren)
            {
                node.Children.Add(child);
                SortChildrenRecursively(child);
            }

            // Post-process to remove expander from redundant BNK files when a WPK exists
            var wpkFiles = node.Children.Where(c => c.Type == NodeType.SoundBank && c.Name.EndsWith(".wpk")).Select(c => Path.GetFileNameWithoutExtension(c.Name)).ToHashSet();
            var bnkFiles = node.Children.Where(c => c.Type == NodeType.SoundBank && c.Name.EndsWith(".bnk")).ToList();

            foreach (var bnkNode in bnkFiles)
            {
                string baseName = Path.GetFileNameWithoutExtension(bnkNode.Name);
                if (wpkFiles.Contains(baseName))
                {
                    bnkNode.Children.Clear(); // Remove the dummy node, thus removing the expander
                }
            }

            // Post-process to remove expander from redundant _events.bnk files when a _audio.bnk exists
            var audioBnks = node.Children.Where(c => c.Type == NodeType.SoundBank && c.Name.EndsWith("_audio.bnk")).Select(c => c.Name).ToHashSet();
            var eventsBnks = node.Children.Where(c => c.Type == NodeType.SoundBank && c.Name.EndsWith("_events.bnk")).ToList();

            foreach (var eventsBnkNode in eventsBnks)
            {
                string correspondingAudioBnk = eventsBnkNode.Name.Replace("_events.bnk", "_audio.bnk");
                if (audioBnks.Contains(correspondingAudioBnk))
                {
                    eventsBnkNode.Children.Clear(); // Remove the dummy node, thus removing the expander
                }
            }
        }

        public async Task<List<FileSystemNodeModel>> LoadChildrenAsync(FileSystemNodeModel wadNode)
        {
            var childrenToAdd = await Task.Run(() =>
            {
                string pathToWad = wadNode.Type == NodeType.WadFile ? wadNode.FullPath : wadNode.SourceWadPath;
                var rootVirtualNode = new FileSystemNodeModel(wadNode.Name, true, wadNode.FullPath, pathToWad);
                using (var wadFile = new WadFile(pathToWad))
                {
                    foreach (var chunk in wadFile.Chunks.Values)
                    {
                        string virtualPath = _hashResolverService.ResolveHash(chunk.PathHash);

                        bool isUnresolved = virtualPath == chunk.PathHash.ToString("x16");
                        bool noExtension = !Path.HasExtension(virtualPath);

                        if (isUnresolved || noExtension)
                        {
                            using (var stream = wadFile.OpenChunk(chunk))
                            {
                                var buffer = new byte[256];
                                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                                var data = new Span<byte>(buffer, 0, bytesRead);
                                string extension = FileTypeDetector.GuessExtension(data);
                                if (!string.IsNullOrEmpty(extension))
                                {
                                    virtualPath = virtualPath + "." + extension;
                                }
                            }
                        }

                        AddNodeToVirtualTree(rootVirtualNode, virtualPath, pathToWad, chunk.PathHash);
                    }
                }

                SortChildrenRecursively(rootVirtualNode);
                return rootVirtualNode.Children.ToList();
            });

            return childrenToAdd;
        }

        public async Task<List<FileSystemNodeModel>> LoadWadContentAsync(string wadPath)
        {
            var nodes = await Task.Run(() =>
            {
                var fileNodes = new List<FileSystemNodeModel>();
                if (!File.Exists(wadPath))
                {
                    return fileNodes; // Return empty list if WAD file doesn't exist
                }

                using (var wadFile = new WadFile(wadPath))
                {
                    foreach (var chunk in wadFile.Chunks.Values)
                    {
                        string virtualPath = _hashResolverService.ResolveHash(chunk.PathHash);
                        if (string.IsNullOrEmpty(virtualPath) || virtualPath == chunk.PathHash.ToString("x16"))
                        {
                            virtualPath = chunk.PathHash.ToString("x16"); // Use hash as name if not resolved
                        }

                        var fileNode = new FileSystemNodeModel(Path.GetFileName(virtualPath), false, virtualPath, wadPath)
                        {
                            SourceChunkPathHash = chunk.PathHash
                        };
                        fileNodes.Add(fileNode);
                    }
                }
                return fileNodes;
            });

            return nodes;
        }

        private FileSystemNodeModel AddNodeToVirtualTree(FileSystemNodeModel root, string virtualPath, string wadPath, ulong chunkHash, DiffStatus status = DiffStatus.Unchanged)
        {
            string[] parts = virtualPath.Replace('\\', '/').Split('/');
            var currentNode = root;

            for (int i = 0; i < parts.Length - 1; i++)
            {
                var dirName = parts[i];
                var childDir = currentNode.Children.FirstOrDefault(c => c.Name.Equals(dirName, System.StringComparison.OrdinalIgnoreCase) && c.Type == NodeType.VirtualDirectory);
                if (childDir == null)
                {
                    var newVirtualPath = string.Join("/", parts.Take(i + 1));
                    childDir = new FileSystemNodeModel(dirName, true, newVirtualPath, wadPath)
                    {
                        Status = status
                    };
                    currentNode.Children.Add(childDir);
                }
                currentNode = childDir;
            }

            var fileNode = new FileSystemNodeModel(parts.Last(), false, virtualPath, wadPath)
            {
                SourceChunkPathHash = chunkHash,
                Status = status
            };



            currentNode.Children.Add(fileNode);
            return fileNode;
        }

        public async Task<List<FileSystemNodeModel>> LoadDirectoryAsync(string rootPath)
        {
            return await Task.Run(() =>
            {
                var rootNode = new FileSystemNodeModel(rootPath);
                rootNode.Children.Clear(); // Clear dummy node
                AddNodeToRealTree(rootNode, rootPath);
                return rootNode.Children.ToList();
            });
        }

        private void AddNodeToRealTree(FileSystemNodeModel parentNode, string path)
        {
            foreach (var directory in Directory.GetDirectories(path).OrderBy(d => d))
            {
                var dirNode = new FileSystemNodeModel(directory);
                dirNode.Children.Clear(); // Clear dummy node
                parentNode.Children.Add(dirNode);
                AddNodeToRealTree(dirNode, directory);
            }

            foreach (var file in Directory.GetFiles(path).OrderBy(f => f))
            {
                var fileNode = new FileSystemNodeModel(file);
                parentNode.Children.Add(fileNode);
            }
        }

        public async Task<FileSystemNodeModel> FindNodeByVirtualPathAsync(string virtualPath, string gameDataPath)
        {
            return await Task.Run(() =>
            {
                string normalizedVirtualPath = virtualPath.Replace('\\', '/').ToUpperInvariant();

                var wadFiles = Directory.GetFiles(gameDataPath, "*.wad", SearchOption.AllDirectories)
                                        .Concat(Directory.GetFiles(gameDataPath, "*.wad.client", SearchOption.AllDirectories))
                                        .ToList();

                foreach (var wadPath in wadFiles)
                {
                    try
                    {
                        using (var wadFile = new WadFile(wadPath))
                        {
                            foreach (var chunk in wadFile.Chunks.Values)
                            {
                                string resolvedChunkPath = _hashResolverService.ResolveHash(chunk.PathHash);
                                string normalizedResolvedChunkPath = resolvedChunkPath.Replace('\\', '/').ToUpperInvariant();

                                if (normalizedResolvedChunkPath.Equals(normalizedVirtualPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    return new FileSystemNodeModel(Path.GetFileName(resolvedChunkPath), false, resolvedChunkPath, wadPath)
                                    {
                                        SourceChunkPathHash = chunk.PathHash,
                                        SourceWadPath = wadPath,
                                        Type = NodeType.VirtualFile
                                    };
                                }
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
    }
}