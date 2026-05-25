using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AssetsManager.Services.Explorer
{
    public class WadSearchBoxService
    {
        private CancellationTokenSource _searchCts;
        private readonly object _searchLock = new object();

        private CancellationToken PrepareCancellationToken()
        {
            lock (_searchLock)
            {
                _searchCts?.Cancel();
                _searchCts?.Dispose();
                _searchCts = new CancellationTokenSource();
                return _searchCts.Token;
            }
        }

        public async Task<FileSystemNodeModel> PerformSearchAsync(
            string searchText,
            ObservableRangeCollection<FileSystemNodeModel> rootNodes,
            Func<FileSystemNodeModel, Task> loadChildrenFunc)
        {
            var token = PrepareCancellationToken();

            try
            {
                if (string.IsNullOrEmpty(searchText))
                {
                    await FilterTreeAsync(rootNodes, string.Empty, token);
                    return null;
                }

                if (searchText.Contains("/"))
                {
                    await FilterTreeAsync(rootNodes, string.Empty, token);
                    var targetNode = await ExpandToPathAsync(searchText, rootNodes);
                    return targetNode;
                }
                else
                {
                    await FilterTreeAsync(rootNodes, searchText, token);
                    return null;
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        public async Task<FileSystemNodeModel> NavigateToPathAsync(
            string path,
            ObservableRangeCollection<FileSystemNodeModel> rootNodes,
            Func<FileSystemNodeModel, Task> loadChildrenFunc)
        {
            if (string.IsNullOrEmpty(path)) return null;

            var token = PrepareCancellationToken();

            // Reset any previous search filtering before navigating
            await FilterTreeAsync(rootNodes, string.Empty, token);

            return await ExpandToPathAsync(path, rootNodes);
        }

        public async Task FilterTreeAsync(ObservableRangeCollection<FileSystemNodeModel> nodes, string searchText, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // 1. Flatten the tree for O(1) traversal with cancellation support
                    var allNodes = new List<FileSystemNodeModel>();
                    Flatten(nodes, allNodes, cancellationToken);

                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(searchText))
                    {
                        // Fast reset: Only update if necessary to avoid UI churn
                        foreach (var node in allNodes)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (!node.IsVisible) node.IsVisible = true;
                            if (node.HasMatch) node.HasMatch = false;
                            node.PreMatch = null;
                            node.Match = null;
                            node.PostMatch = null;
                        }
                        return;
                    }

                    // 2. High Performance & Anti-Flicker Filtering
                    // Pass 1: Identification (who should be visible?)
                    var toShow = new HashSet<FileSystemNodeModel>();
                    var matchIndices = new Dictionary<FileSystemNodeModel, int>();

                    foreach (var node in allNodes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (node.Name == "Loading...") continue;

                        int index = node.Name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
                        if (index >= 0)
                        {
                            matchIndices[node] = index;
                            toShow.Add(node);

                            // Propagate visibility upwards instantly in the set
                            var parent = node.Parent;
                            while (parent != null && !toShow.Contains(parent))
                            {
                                toShow.Add(parent);
                                parent = parent.Parent;
                            }
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    // Pass 2: Surgical Application (Only notify UI if state changes)
                    foreach (var node in allNodes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        bool shouldBeVisible = toShow.Contains(node);
                        bool hasMatch = matchIndices.TryGetValue(node, out int matchIndex);

                        // Update match highlighting data
                        if (node.HasMatch != hasMatch) node.HasMatch = hasMatch;
                        
                        if (hasMatch)
                        {
                            int length = searchText.Length;
                            node.PreMatch = node.Name.Substring(0, matchIndex);
                            node.Match = node.Name.Substring(matchIndex, length);
                            node.PostMatch = node.Name.Substring(matchIndex + length);
                        }
                        else
                        {
                            node.PreMatch = null;
                            node.Match = null;
                            node.PostMatch = null;
                        }

                        // CRITICAL: Only update IsVisible if it's different.
                        // This prevents the flickering of parent containers.
                        if (node.IsVisible != shouldBeVisible)
                        {
                            node.IsVisible = shouldBeVisible;
                        }
                    }
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is InvalidOperationException)
            {
                // Silently swallow cancellation or collection modification (we will retry on next keystroke)
            }
        }

        private void Flatten(IEnumerable<FileSystemNodeModel> roots, List<FileSystemNodeModel> result, CancellationToken ct)
        {
            if (roots == null) return;

            var stack = new Stack<FileSystemNodeModel>();
            foreach (var root in roots.Reverse()) stack.Push(root);

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var node = stack.Pop();
                result.Add(node);
                
                var children = node.Children;
                if (children != null)
                {
                    for (int i = children.Count - 1; i >= 0; i--)
                    {
                        stack.Push(children[i]);
                    }
                }
            }
        }

        private async Task<FileSystemNodeModel> ExpandToPathAsync(
            string path,
            ObservableRangeCollection<FileSystemNodeModel> rootNodes)
        {
            path = path.Replace("\\", "/").Trim('/');
            if (string.IsNullOrEmpty(path)) return null;

            string[] pathComponents = path.Split('/');
            var targetNode = await FindNodeByPathSuffixAsync(rootNodes, pathComponents);

            return targetNode;
        }

        private async Task<FileSystemNodeModel> FindNodeByPathSuffixAsync(
            IEnumerable<FileSystemNodeModel> nodes,
            string[] pathSuffix)
        {
            if (nodes == null) return null;

            foreach (var node in nodes)
            {
                bool isMatch = node.Name.Equals(pathSuffix[0], StringComparison.OrdinalIgnoreCase);
                
                // If it's a single component search, we can be more lenient (StartsWith)
                if (pathSuffix.Length == 1 && node.Name.StartsWith(pathSuffix[0], StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }

                if (isMatch && pathSuffix.Length > 1 && node.Children != null)
                {
                    // Match found for this segment, try to match the rest of the suffix in children
                    string[] remainingSuffix = pathSuffix.Skip(1).ToArray();
                    var foundInDescendant = await FindNodeByPathSuffixAsync(node.Children, remainingSuffix);
                    if (foundInDescendant != null) return foundInDescendant;
                }

                // Even if this node didn't match the start of the suffix, the suffix might exist deeper in this branch
                // (e.g., searching for /skins.json starting from the root)
                if (node.Children != null && node.Children.Any())
                {
                    var foundDeeper = await FindNodeByPathSuffixAsync(node.Children, pathSuffix);
                    if (foundDeeper != null) return foundDeeper;
                }
            }
            return null;
        }
    }
}