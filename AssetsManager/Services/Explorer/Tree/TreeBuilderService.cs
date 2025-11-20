using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models.Explorer;

using System.Threading;

namespace AssetsManager.Services.Explorer.Tree
{
    public class TreeBuilderService
    {
        private readonly WadNodeLoaderService _wadNodeLoaderService;
        private readonly HashResolverService _hashResolverService;
        private readonly LogService _logService;

        public TreeBuilderService(WadNodeLoaderService wadNodeLoaderService, HashResolverService hashResolverService, LogService logService)
        {
            _wadNodeLoaderService = wadNodeLoaderService;
            _hashResolverService = hashResolverService;
            _logService = logService;
        }

        public async Task<ObservableCollection<FileSystemNodeModel>> BuildWadTreeAsync(string rootPath, CancellationToken cancellationToken)
        {
            var rootNodes = new ObservableCollection<FileSystemNodeModel>();

            string gamePath = Path.Combine(rootPath, "Game");
            if (Directory.Exists(gamePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var gameNode = new FileSystemNodeModel(gamePath);
                rootNodes.Add(gameNode);
                await LoadAllChildren(gameNode, rootPath, cancellationToken);
            }

            string pluginsPath = Path.Combine(rootPath, "Plugins");
            if (Directory.Exists(pluginsPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pluginsNode = new FileSystemNodeModel(pluginsPath);
                rootNodes.Add(pluginsNode);
                await LoadAllChildren(pluginsNode, rootPath, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            for (int i = rootNodes.Count - 1; i >= 0; i--)
            {
                if (!PruneEmptyDirectories(rootNodes[i]))
                {
                    rootNodes.RemoveAt(i);
                }
            }

            return rootNodes;
        }

        public async Task<ObservableCollection<FileSystemNodeModel>> BuildDirectoryTreeAsync(string rootPath, CancellationToken cancellationToken)
        {
            var nodes = await _wadNodeLoaderService.LoadDirectoryAsync(rootPath, cancellationToken);
            return new ObservableCollection<FileSystemNodeModel>(nodes);
        }

        public async Task<(ObservableCollection<FileSystemNodeModel> Nodes, string NewLolPath, string OldLolPath)> BuildTreeFromBackupAsync(string jsonPath, CancellationToken cancellationToken)
        {
            var (nodes, newLolPath, oldLolPath) = await _wadNodeLoaderService.LoadFromBackupAsync(jsonPath, cancellationToken);
            return (new ObservableCollection<FileSystemNodeModel>(nodes), newLolPath, oldLolPath);
        }

        public async Task LoadAllChildren(FileSystemNodeModel node, string currentRootPath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (node.Children.Count == 1 && node.Children[0].Name == "Loading...")
            {
                node.Children.Clear();
            }

            if (node.Type == NodeType.WadFile)
            {
                var children = await _wadNodeLoaderService.LoadChildrenAsync(node, cancellationToken);
                foreach (var child in children)
                {
                    node.Children.Add(child);
                }
                return;
            }

            if (node.Type == NodeType.RealDirectory)
            {
                try
                {
                    var directories = Directory.GetDirectories(node.FullPath);
                    foreach (var dir in directories.OrderBy(d => d))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var childNode = new FileSystemNodeModel(dir);
                        node.Children.Add(childNode);
                        await LoadAllChildren(childNode, currentRootPath, cancellationToken);
                    }

                    var files = Directory.GetFiles(node.FullPath);
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
                            node.Children.Add(childNode);
                            await LoadAllChildren(childNode, currentRootPath, cancellationToken); // Eager load WAD content
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    _logService.LogWarning($"Access denied to: {node.FullPath}");
                }
            }
        }

        private bool PruneEmptyDirectories(FileSystemNodeModel node)
        {
            if (node.Type != NodeType.RealDirectory)
            {
                return true; // Keep files
            }

            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                if (!PruneEmptyDirectories(node.Children[i]))
                {
                    node.Children.RemoveAt(i);
                }
            }

            return node.Children.Any();
        }
    }
}
