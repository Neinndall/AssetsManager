using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models;
using LeagueToolkit.Core.Wad;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
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

        public async Task<(List<FileSystemNodeModel> Nodes, string NewLolPath, string OldLolPath)> LoadFromBackupAsync(string jsonPath, CancellationToken token)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };

            string jsonContent = await File.ReadAllTextAsync(jsonPath, token);
            token.ThrowIfCancellationRequested();

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
                token.ThrowIfCancellationRequested();
                var wadNode = new FileSystemNodeModel($"{wadGroup.Key} ({wadGroup.Count()})", true, wadGroup.Key, wadGroup.Key);

                foreach (var file in wadGroup)
                {
                    string chunkPath = GetBackupChunkPath(backupRoot, file);
                    file.BackupChunkPath = chunkPath; // Ensure the main diff object has the path
                    var status = GetDiffStatus(file.Type);

                    string statusPrefix = GetStatusPrefix(file.Type);
                    string prefixedPath = $"{statusPrefix}/{file.Path}";

                    var node = AddNodeToVirtualTree(wadNode, prefixedPath, wadGroup.Key, file.NewPathHash, status);
                    node.ChunkDiff = file;
                    node.BackupChunkPath = chunkPath;
                    if (file.Type == ChunkDiffType.Renamed)
                    {
                        node.OldPath = file.OldPath;
                    }

                    if (file.Dependencies != null)
                    {
                        foreach (var dep in file.Dependencies)
                        {
                            var depStatus = GetDiffStatus(ChunkDiffType.Modified); // Treat as modified for node creation
                            var depChunkPath = GetBackupChunkPath(backupRoot, new SerializableChunkDiff { OldPathHash = dep.OldPathHash, NewPathHash = dep.NewPathHash, Type = ChunkDiffType.Modified });

                            string depStatusPrefix = GetStatusPrefix(ChunkDiffType.Modified);
                            string depPrefixedPath = $"{depStatusPrefix}/{dep.Path}";

                            var depNode = AddNodeToVirtualTree(wadNode, depPrefixedPath, wadGroup.Key, dep.NewPathHash, depStatus);
                            depNode.ChunkDiff = new SerializableChunkDiff
                            {
                                Type = ChunkDiffType.Modified, // For consistency
                                OldPath = dep.Path,
                                NewPath = dep.Path,
                                OldPathHash = dep.OldPathHash,
                                NewPathHash = dep.NewPathHash,
                                OldCompressionType = dep.CompressionType,
                                NewCompressionType = dep.CompressionType,
                                BackupChunkPath = depChunkPath
                            };
                            depNode.BackupChunkPath = depChunkPath;
                        }
                    }
                }

                SortChildrenRecursively(wadNode);
                rootNodes.Add(wadNode);
            }

            return (rootNodes, comparisonData.NewLolPath, comparisonData.OldLolPath);
        }

        private string GetStatusPrefix(ChunkDiffType type) => type switch
        {
            ChunkDiffType.New => "[+] New",
            ChunkDiffType.Modified => "[~] Modified",
            ChunkDiffType.Renamed => "[»] Renamed",
            ChunkDiffType.Removed => "[-] Deleted",
            _ => "[?] Unknown"
        };

        private string GetBackupChunkPath(string backupRoot, SerializableChunkDiff diff)
        {
            if (diff.Type == ChunkDiffType.Removed)
            {
                return Path.Combine(backupRoot, "wad_chunks", "old", $"{diff.OldPathHash:X16}.chunk");
            }

            string newPath = Path.Combine(backupRoot, "wad_chunks", "new", $"{diff.NewPathHash:X16}.chunk");
            if (File.Exists(newPath))
            {
                return newPath;
            }

            return Path.Combine(backupRoot, "wad_chunks", "old", $"{diff.OldPathHash:X16}.chunk");
        }

        private DiffStatus GetDiffStatus(ChunkDiffType type)
        {
            return type switch
            {
                ChunkDiffType.New => DiffStatus.New,
                ChunkDiffType.Removed => DiffStatus.Deleted,
                ChunkDiffType.Modified => DiffStatus.Modified,
                ChunkDiffType.Renamed => DiffStatus.Renamed,
                _ => DiffStatus.Unchanged,
            };
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
                string bnkBaseName = Path.GetFileNameWithoutExtension(bnkNode.Name);

                // Case 1: A WPK with the exact same name exists (e.g., common.bnk, common.wpk)
                bool directMatch = wpkFiles.Contains(bnkBaseName);

                // Case 2: An events/music BNK corresponds to an audio WPK (e.g., vo_events.bnk, vo_audio.wpk)
                string correspondingAudioWpkName = bnkBaseName.Replace("_events", "_audio").Replace("_music", "_audio");
                bool audioMatch = wpkFiles.Contains(correspondingAudioWpkName);

                if (directMatch || audioMatch)
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

        public async Task<List<FileSystemNodeModel>> LoadChildrenAsync(FileSystemNodeModel wadNode, CancellationToken token)
        {
            var childrenToAdd = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                string pathToWad = wadNode.Type == NodeType.WadFile ? wadNode.FullPath : wadNode.SourceWadPath;
                var rootVirtualNode = new FileSystemNodeModel(wadNode.Name, true, wadNode.FullPath, pathToWad);
                using (var wadFile = new WadFile(pathToWad))
                {
                    foreach (var chunk in wadFile.Chunks.Values)
                    {
                        token.ThrowIfCancellationRequested();
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
            }, token);

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

        public async Task<List<FileSystemNodeModel>> LoadDirectoryAsync(string rootPath, CancellationToken token)
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var rootNode = new FileSystemNodeModel(rootPath);
                rootNode.Children.Clear(); // Clear dummy node
                AddNodeToRealTree(rootNode, rootPath, token);
                return rootNode.Children.ToList();
            }, token);
        }

        private void AddNodeToRealTree(FileSystemNodeModel parentNode, string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            foreach (var directory in Directory.GetDirectories(path).OrderBy(d => d))
            {
                token.ThrowIfCancellationRequested();
                var dirNode = new FileSystemNodeModel(directory);
                dirNode.Children.Clear(); // Clear dummy node
                parentNode.Children.Add(dirNode);
                AddNodeToRealTree(dirNode, directory, token);
            }

            token.ThrowIfCancellationRequested();
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
