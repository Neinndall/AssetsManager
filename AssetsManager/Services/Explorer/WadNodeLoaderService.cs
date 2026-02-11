using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LeagueToolkit.Core.Wad;
using AssetsManager.Utils;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Wad;

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

        /// <summary>
        /// Loads a virtual file tree from a saved comparison backup JSON file.
        /// </summary>
        public async Task<(ObservableRangeCollection<FileSystemNodeModel> Nodes, string NewLolPath, string OldLolPath)> LoadFromBackupAsync(string jsonPath, bool isSortingEnabled, CancellationToken cancellationToken)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };

            string jsonContent = await File.ReadAllTextAsync(jsonPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var comparisonData = JsonSerializer.Deserialize<WadComparisonData>(jsonContent, options);

            var rootNodes = new ObservableRangeCollection<FileSystemNodeModel>();
            if (comparisonData?.Diffs == null || !comparisonData.Diffs.Any())
            {
                return (rootNodes, null, null);
            }

            string backupRoot = Path.GetDirectoryName(jsonPath);
            var diffsByWad = comparisonData.Diffs.GroupBy(d => d.SourceWadFile);

            foreach (var wadGroup in diffsByWad)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var file in wadGroup)
                {
                    if (file.OldPathHash != 0)
                        file.OldPath = RestoreExtension(file.OldPath, _hashResolverService.ResolveHash(file.OldPathHash), file.OldPathHash);

                    if (file.NewPathHash != 0)
                        file.NewPath = RestoreExtension(file.NewPath, _hashResolverService.ResolveHash(file.NewPathHash), file.NewPathHash);

                    if (file.Dependencies != null)
                    {
                        foreach (var dep in file.Dependencies)
                        {
                            ulong depHash = dep.NewPathHash != 0 ? dep.NewPathHash : dep.OldPathHash;
                            if (depHash != 0)
                                dep.Path = RestoreExtension(dep.Path, _hashResolverService.ResolveHash(depHash), depHash);
                        }
                    }
                }

                var wadNode = new FileSystemNodeModel($"{wadGroup.Key} ({wadGroup.Count()})", true, wadGroup.Key, wadGroup.Key);

                if (isSortingEnabled)
                {
                    foreach (var file in wadGroup)
                    {
                        string chunkPath = GetBackupChunkPath(backupRoot, file);
                        file.BackupChunkPath = chunkPath; 
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
                                if (dep.WasTopLevelDiff && dep.Type.HasValue)
                                {
                                    var depType = dep.Type.Value;
                                    var depStatus = GetDiffStatus(depType);
                                    string depStatusPrefix = GetStatusPrefix(depType);
                                    string depPrefixedPath = $"{depStatusPrefix}/{dep.Path}";

                                    var depNode = AddNodeToVirtualTree(wadNode, depPrefixedPath, dep.SourceWad, dep.NewPathHash, depStatus);
                                    
                                    depNode.ChunkDiff = new SerializableChunkDiff
                                    {
                                        Type = depType,
                                        OldPath = dep.Path,
                                        NewPath = dep.Path,
                                        SourceWadFile = dep.SourceWad,
                                        OldPathHash = dep.OldPathHash,
                                        NewPathHash = dep.NewPathHash,
                                        OldCompressionType = dep.CompressionType,
                                        NewCompressionType = dep.CompressionType,
                                        BackupChunkPath = GetBackupChunkPath(backupRoot, new SerializableChunkDiff { OldPathHash = dep.OldPathHash, NewPathHash = dep.NewPathHash, Type = depType, SourceWadFile = dep.SourceWad })
                                    };
                                    depNode.BackupChunkPath = depNode.ChunkDiff.BackupChunkPath;
                                }
                            }
                        }
                    }

                    SortChildrenRecursively(wadNode);
                    PostProcessAudioNodes(wadNode);
                }
                else
                {
                    var statusGroups = wadGroup.GroupBy(d => d.Type).ToDictionary(g => g.Key, g => g.ToList());
                    var statusOrder = new[] { ChunkDiffType.New, ChunkDiffType.Modified, ChunkDiffType.Renamed, ChunkDiffType.Removed };

                    foreach (var statusType in statusOrder)
                    {
                        if (statusGroups.TryGetValue(statusType, out var filesInStatus))
                        {
                            var statusNode = new FileSystemNodeModel(GetStatusPrefix(statusType), true, GetStatusPrefix(statusType), wadGroup.Key);
                            statusNode.Status = GetDiffStatus(statusType);
                            wadNode.Children.Add(statusNode);

                            var nodesToAdd = new List<FileSystemNodeModel>();
                            foreach (var file in filesInStatus.OrderBy(f => f.Path))
                            {
                                string chunkPath = GetBackupChunkPath(backupRoot, file);
                                file.BackupChunkPath = chunkPath;
                                var status = GetDiffStatus(file.Type);

                                var node = new FileSystemNodeModel(Path.GetFileName(file.Path), false, file.Path, wadGroup.Key)
                                {
                                    SourceChunkPathHash = file.NewPathHash,
                                    Status = status,
                                    ChunkDiff = file,
                                    BackupChunkPath = chunkPath,
                                    OldPath = file.Type == ChunkDiffType.Renamed ? file.OldPath : null
                                };

                                if (file.Dependencies != null)
                                {
                                    var depNodesToAdd = new List<FileSystemNodeModel>();
                                    foreach (var dep in file.Dependencies)
                                    {
                                        if (dep.WasTopLevelDiff && dep.Type.HasValue)
                                        {
                                            string depStatusPrefix = GetStatusPrefix(dep.Type.Value);
                                            string depFileName = Path.GetFileName(dep.Path);
                                            string prefixedFullPath = $"{depStatusPrefix}/{dep.Path}";

                                            var depNode = new FileSystemNodeModel($"{depStatusPrefix} {depFileName}", false, prefixedFullPath, wadGroup.Key)
                                            {
                                                SourceChunkPathHash = dep.NewPathHash,
                                                Status = GetDiffStatus(dep.Type.Value),
                                                ChunkDiff = new SerializableChunkDiff
                                                {
                                                    Type = dep.Type.Value,
                                                    OldPath = dep.Path,
                                                    NewPath = dep.Path,
                                                    SourceWadFile = dep.SourceWad,
                                                    OldPathHash = dep.OldPathHash,
                                                    NewPathHash = dep.NewPathHash,
                                                    OldCompressionType = dep.CompressionType,
                                                    NewCompressionType = dep.CompressionType,
                                                    BackupChunkPath = GetBackupChunkPath(backupRoot, new SerializableChunkDiff { OldPathHash = dep.OldPathHash, NewPathHash = dep.NewPathHash, Type = dep.Type.Value, SourceWadFile = dep.SourceWad })
                                                },
                                                BackupChunkPath = GetBackupChunkPath(backupRoot, new SerializableChunkDiff { OldPathHash = dep.OldPathHash, NewPathHash = dep.NewPathHash, Type = dep.Type.Value, SourceWadFile = dep.SourceWad })
                                            };
                                            depNodesToAdd.Add(depNode);
                                        }
                                        else
                                        {
                                            string depFileName = Path.GetFileName(dep.Path);
                                            var depNode = new FileSystemNodeModel(depFileName, false, dep.Path, wadGroup.Key)
                                            {
                                                SourceChunkPathHash = dep.NewPathHash,
                                                Status = DiffStatus.Unchanged,
                                                ChunkDiff = new SerializableChunkDiff
                                                {
                                                    Type = ChunkDiffType.Dependency,
                                                    OldPath = dep.Path,
                                                    NewPath = dep.Path,
                                                    SourceWadFile = dep.SourceWad,
                                                    OldPathHash = dep.OldPathHash,
                                                    NewPathHash = dep.NewPathHash,
                                                    OldCompressionType = dep.CompressionType,
                                                    NewCompressionType = dep.CompressionType,
                                                    BackupChunkPath = GetBackupChunkPath(backupRoot, new SerializableChunkDiff { OldPathHash = dep.OldPathHash, NewPathHash = dep.NewPathHash, Type = ChunkDiffType.Modified, SourceWadFile = dep.SourceWad })
                                                },
                                                BackupChunkPath = GetBackupChunkPath(backupRoot, new SerializableChunkDiff { OldPathHash = dep.OldPathHash, NewPathHash = dep.NewPathHash, Type = ChunkDiffType.Modified, SourceWadFile = dep.SourceWad })
                                            };
                                            depNodesToAdd.Add(depNode);
                                        }
                                    }
                                    node.Children.AddRange(depNodesToAdd);
                                }
                                nodesToAdd.Add(node);
                            }
                            statusNode.Children.AddRange(nodesToAdd);
                        }
                    }
                }
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
            ChunkDiffType.Dependency => "[=] Dependency",
            _ => "[?] Unknown"
        };

        private string RestoreExtension(string original, string resolved, ulong hash)
        {
            if (Path.HasExtension(resolved)) return resolved;
            if (!string.IsNullOrEmpty(original) && Path.HasExtension(original))
            {
                if (resolved == hash.ToString("x16")) return original;
                return resolved + Path.GetExtension(original);
            }
            return resolved;
        }

        private string GetBackupChunkPath(string backupRoot, SerializableChunkDiff diff)
        {
            if (diff.Type == ChunkDiffType.Removed)
            {
                return Path.Combine(backupRoot, "wad_chunks", "old", diff.SourceWadFile, $"{diff.OldPathHash:X16}.chunk");
            }

            string newPath = Path.Combine(backupRoot, "wad_chunks", "new", diff.SourceWadFile, $"{diff.NewPathHash:X16}.chunk");
            if (File.Exists(newPath))
            {
                return newPath;
            }

            return Path.Combine(backupRoot, "wad_chunks", "old", diff.SourceWadFile, $"{diff.OldPathHash:X16}.chunk");
        }

        private DiffStatus GetDiffStatus(ChunkDiffType type)
        {
            return type switch
            {
                ChunkDiffType.New => DiffStatus.New,
                ChunkDiffType.Removed => DiffStatus.Deleted,
                ChunkDiffType.Modified => DiffStatus.Modified,
                ChunkDiffType.Renamed => DiffStatus.Renamed,
                ChunkDiffType.Dependency => DiffStatus.Unchanged,
                _ => DiffStatus.Unchanged,
            };
        }

        /// <summary>
        /// Recursively sorts tree nodes and performs post-processing cleanup.
        /// </summary>
        private void SortChildrenRecursively(FileSystemNodeModel node)
        {
            if (node.Type != NodeType.VirtualDirectory && node.Type != NodeType.WadFile) return;

            var sortedChildren = node.Children
                .OrderBy(c => c.Type == NodeType.VirtualDirectory ? 0 : 1)
                .ThenBy(c => c.Name)
                .ToList();

            foreach (var child in sortedChildren)
            {
                SortChildrenRecursively(child);
            }

            node.Children.ReplaceRange(sortedChildren);
        }

        /// <summary>
        /// Removes expanders from redundant audio nodes (like event banks or redundant format containers).
        /// </summary>
        private void PostProcessAudioNodes(FileSystemNodeModel wadRoot)
        {
            var allNodes = new List<FileSystemNodeModel>();
            FlattenTree(wadRoot, allNodes);

            var audioNodes = allNodes.Where(n => n.Type == NodeType.SoundBank).ToList();
            if (!audioNodes.Any()) return;

            // 1. Create a map of all SoundBank names for fast lookup
            var nodeNames = audioNodes.Select(n => n.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var node in audioNodes)
            {
                string name = node.Name;
                string baseName = Path.GetFileNameWithoutExtension(name);

                // CASE A: It's an events bank (e.g., ashe_base_vo_events.bnk)
                if (name.EndsWith("_events.bnk", StringComparison.OrdinalIgnoreCase))
                {
                    string prefix = name.Replace("_events.bnk", "");
                    // Redundant if there's a corresponding audio container (_audio.wpk or _audio.bnk)
                    if (nodeNames.Contains(prefix + "_audio.wpk") || nodeNames.Contains(prefix + "_audio.bnk"))
                    {
                        node.Children.Clear();
                    }
                }
                // CASE B: It's a BNK that might have a corresponding WPK (e.g., ashe_base_vo_audio.bnk vs ashe_base_vo_audio.wpk)
                else if (name.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
                {
                    // Redundant if there is a .wpk with the exact same base name
                    if (nodeNames.Contains(baseName + ".wpk"))
                    {
                        node.Children.Clear();
                    }
                }
            }
        }

        private void FlattenTree(FileSystemNodeModel parent, List<FileSystemNodeModel> result)
        {
            if (parent.Children == null) return;
            
            foreach (var child in parent.Children)
            {
                result.Add(child);
                FlattenTree(child, result);
            }
        }

        /// <summary>
        /// Ensures all children of a node are loaded, primarily used for lazy loading WAD files.
        /// </summary>
        public async Task EnsureAllChildrenLoadedAsync(FileSystemNodeModel node, string currentRootPath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (node.Children.Count == 1 && node.Children[0].Name == "Loading...")
            {
                node.Children.Clear();
            }

            if (node.Type == NodeType.WadFile)
            {
                var children = await LoadChildrenAsync(node, cancellationToken);
                node.Children.AddRange(children);
                return;
            }

            if (node.Type == NodeType.RealDirectory)
            {
                try
                {
                    var directories = Directory.GetDirectories(node.FullPath);
                    var childDirs = directories.OrderBy(d => d).Select(dir => new FileSystemNodeModel(dir)).ToList();
                    node.Children.AddRange(childDirs);
                    
                    foreach(var childNode in childDirs)
                    {
                        await EnsureAllChildrenLoadedAsync(childNode, currentRootPath, cancellationToken);
                    }

                    var files = Directory.GetFiles(node.FullPath);
                    var childFiles = new List<FileSystemNodeModel>();
                    foreach (var file in files.OrderBy(f => f))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string lowerFile = file.ToLowerInvariant();

                        bool keepFile = false;
                        if (lowerFile.EndsWith(".wad.client"))
                        {
                            if (node.FullPath.StartsWith(Path.Combine(currentRootPath, "Game")))
                                keepFile = true;
                        }
                        else if (lowerFile.EndsWith(".wad"))
                        {
                            if (node.FullPath.StartsWith(Path.Combine(currentRootPath, "Plugins")))
                                keepFile = true;
                        }

                        if (keepFile)
                        {
                            var childNode = new FileSystemNodeModel(file);
                            childFiles.Add(childNode);
                        }
                    }
                    
                    node.Children.AddRange(childFiles);
                    foreach(var childNode in childFiles)
                    {
                        await EnsureAllChildrenLoadedAsync(childNode, currentRootPath, cancellationToken);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    _logService.LogWarning($"Access denied to: {node.FullPath}");
                }
            }
        }

        /// <summary>
        /// Loads the contents of a WAD file into a collection of tree nodes.
        /// </summary>
        public async Task<ObservableRangeCollection<FileSystemNodeModel>> LoadChildrenAsync(FileSystemNodeModel wadNode, CancellationToken cancellationToken)
        {
            var childrenToAdd = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                string pathToWad = wadNode.Type == NodeType.WadFile ? wadNode.FullPath : wadNode.SourceWadPath;
                var rootVirtualNode = new FileSystemNodeModel(wadNode.Name, true, wadNode.FullPath, pathToWad);
                
                try
                {
                    using (var wadFile = new WadFile(pathToWad))
                    {
                        foreach (var chunk in wadFile.Chunks.Values)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
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
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logService.LogError($"Failed to load WAD file at '{pathToWad}': {ex.Message}");
                }

                SortChildrenRecursively(rootVirtualNode);
                PostProcessAudioNodes(rootVirtualNode);
                return rootVirtualNode.Children;
            }, cancellationToken);

            return childrenToAdd;
        }

        /// <summary>
        /// Quick load of WAD file names without deep analysis.
        /// </summary>
        public async Task<ObservableRangeCollection<FileSystemNodeModel>> LoadWadContentAsync(string wadPath)
        {
            var nodes = await Task.Run(() =>
            {
                var fileNodes = new ObservableRangeCollection<FileSystemNodeModel>();
                if (!File.Exists(wadPath))
                {
                    return fileNodes;
                }

                try
                {
                    using (var wadFile = new WadFile(wadPath))
                    {
                        foreach (var chunk in wadFile.Chunks.Values)
                        {
                            string virtualPath = _hashResolverService.ResolveHash(chunk.PathHash);
                            if (string.IsNullOrEmpty(virtualPath) || virtualPath == chunk.PathHash.ToString("x16"))
                            {
                                virtualPath = chunk.PathHash.ToString("x16");
                            }

                            var fileNode = new FileSystemNodeModel(Path.GetFileName(virtualPath), false, virtualPath, wadPath)
                            {
                                SourceChunkPathHash = chunk.PathHash
                            };
                            fileNodes.Add(fileNode);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogError($"Failed to load WAD content at '{wadPath}': {ex.Message}");
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

            var existingNode = currentNode.Children.FirstOrDefault(c => c.Name.Equals(fileNode.Name, StringComparison.OrdinalIgnoreCase));
            if (existingNode != null)
            {
                return existingNode;
            }

            currentNode.Children.Add(fileNode);
            return fileNode;
        }

        /// <summary>
        /// Loads a physical directory into the tree structure.
        /// </summary>
        public async Task<ObservableRangeCollection<FileSystemNodeModel>> LoadDirectoryAsync(string rootPath, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rootNode = new FileSystemNodeModel(rootPath);
                rootNode.Children.Clear();
                AddNodeToRealTree(rootNode, rootPath, cancellationToken);
                return rootNode.Children;
            }, cancellationToken);
        }

        private void AddNodeToRealTree(FileSystemNodeModel parentNode, string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var directory in Directory.EnumerateDirectories(path).OrderBy(d => d))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dirNode = new FileSystemNodeModel(directory);
                dirNode.Children.Clear();
                parentNode.Children.Add(dirNode);
                AddNodeToRealTree(dirNode, directory, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in Directory.EnumerateFiles(path).OrderBy(f => f))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileNode = new FileSystemNodeModel(file);
                parentNode.Children.Add(fileNode);
            }
        }

        /// <summary>
        /// Searches for a specific virtual path within all WAD files in a directory.
        /// </summary>
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
