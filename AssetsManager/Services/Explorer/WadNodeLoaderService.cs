using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models;
using LeagueToolkit.Core.Wad;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AssetsManager.Utils;

namespace AssetsManager.Services.Explorer
{
    public class WadNodeLoaderService
    {
        private readonly HashResolverService _hashResolverService;

        public WadNodeLoaderService(HashResolverService hashResolverService)
        {
            _hashResolverService = hashResolverService;
        }

        public async Task<List<FileSystemNodeModel>> LoadFromBackupAsync(string jsonPath)
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
                return rootNodes;
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
                        var node = AddNodeToVirtualTree(newFilesNode, resolvedPath, chunkPath, file.NewPathHash, DiffStatus.New);
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
                        var node = AddNodeToVirtualTree(modifiedFilesNode, resolvedPath, chunkPath, file.NewPathHash, DiffStatus.Modified);
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
                        var node = AddNodeToVirtualTree(renamedFilesNode, resolvedPath, chunkPath, file.NewPathHash, DiffStatus.Renamed);
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
                        var node = AddNodeToVirtualTree(deletedFilesNode, resolvedPath, chunkPath, file.OldPathHash, DiffStatus.Deleted);
                        node.ChunkDiff = file;
                    }
                    wadNode.Children.Add(deletedFilesNode);
                }

                SortChildrenRecursively(wadNode);
                rootNodes.Add(wadNode);
            }

            return rootNodes;
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

            // Post-process to remove expander from redundant VO BNK files
            var voWpks = node.Children.Where(c => c.Name.Contains("_vo_audio.wpk")).Select(c => c.Name).ToHashSet();
            var voBnks = node.Children.Where(c => c.Name.Contains("_vo_audio.bnk")).ToList();

            foreach (var bnkNode in voBnks)
            {
                string correspondingWpk = bnkNode.Name.Replace(".bnk", ".wpk");
                if (voWpks.Contains(correspondingWpk))
                {
                    bnkNode.Children.Clear(); // Remove the dummy node, thus removing the expander
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
                        if (!string.IsNullOrEmpty(virtualPath) && virtualPath != chunk.PathHash.ToString("x16"))
                        {
                            AddNodeToVirtualTree(rootVirtualNode, virtualPath, pathToWad, chunk.PathHash);
                        }
                    }
                }

                SortChildrenRecursively(rootVirtualNode);
                return rootVirtualNode.Children.ToList();
            });

            return childrenToAdd;
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

            // Add a dummy child to all audio banks to make them expandable. Redundant ones will be pruned after sorting.
            if (SupportedFileTypes.AudioBank.Contains(fileNode.Extension) && fileNode.Name.Contains("_audio"))
            {
                fileNode.Children.Add(new FileSystemNodeModel());
            }

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
    }
}