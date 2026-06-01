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
            Func<FileSystemNodeModel, Task> loadChildrenFunc,
            FileSystemNodeModel activeNode = null)
        {
            var token = PrepareCancellationToken();

            try
            {
                if (string.IsNullOrEmpty(searchText))
                {
                    await FilterTreeAsync(rootNodes, string.Empty, token, activeNode);
                    return null;
                }

                if (searchText.Contains("/"))
                {
                    // No need to filter if we are navigating to a specific path
                    // But we might want to clear any existing search highlights/filters
                    await FilterTreeAsync(rootNodes, string.Empty, token, activeNode);
                    var targetNode = await ExpandToPathAsync(searchText, rootNodes);
                    return targetNode;
                }
                else
                {
                    await FilterTreeAsync(rootNodes, searchText, token, activeNode);
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

        public async Task FilterTreeAsync(ObservableRangeCollection<FileSystemNodeModel> nodes, string searchText, CancellationToken cancellationToken, FileSystemNodeModel activeNode = null)
        {
            try
            {
                // Identification and Flattening (Always fast)
                var allNodes = new List<FileSystemNodeModel>();
                await Task.Run(() => Flatten(nodes, allNodes, cancellationToken), cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                bool isSearchEmpty = string.IsNullOrWhiteSpace(searchText);

                if (isSearchEmpty)
                {
                    // 1. Identify prioritized branch (active node path and its siblings)
                    var prioritizedNodes = new HashSet<FileSystemNodeModel>();
                    if (activeNode != null)
                    {
                        var current = activeNode;
                        while (current != null)
                        {
                            prioritizedNodes.Add(current);
                            if (current.Parent != null)
                            {
                                // Add all siblings of the active path
                                foreach (var sibling in current.Parent.Children) prioritizedNodes.Add(sibling);
                            }
                            current = current.Parent;
                        }
                        
                        // Add root nodes too
                        foreach (var root in nodes) prioritizedNodes.Add(root);

                        // Apply priority updates immediately (chunked to ensure fluidity)
                        int pCount = 0;
                        foreach (var node in prioritizedNodes)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            ResetNodeState(node);
                            pCount++;
                            if (pCount % 400 == 0) await Task.Yield();
                        }
                    }

                    // 2. START BACKGROUND RESET for the rest and RETURN immediately
                    // This allows the search clearing operation to finish so navigation can begin.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Wait a bit to let the prioritized branch render first
                            await Task.Delay(100, cancellationToken);

                            int updatedCount = 0;
                            foreach (var node in allNodes)
                            {
                                if (cancellationToken.IsCancellationRequested) break;
                                if (prioritizedNodes.Contains(node)) continue;

                                if (!node.IsVisible || node.HasMatch)
                                {
                                    ResetNodeState(node);
                                    updatedCount++;

                                    // Yield frequently with a real delay to ensure UI thread is never saturated
                                    if (updatedCount % 500 == 0) await Task.Delay(5, cancellationToken);
                                }
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception) { }
                    }, cancellationToken);

                    return;
                }

                // Normal Search Logic
                await Task.Run(async () =>
                {
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
                    int uiUpdateCount = 0;
                    foreach (var node in allNodes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        bool shouldBeVisible = toShow.Contains(node);
                        bool hasMatch = matchIndices.TryGetValue(node, out int matchIndex);

                        bool changed = false;

                        // Update match highlighting data
                        if (node.HasMatch != hasMatch)
                        {
                            node.HasMatch = hasMatch;
                            changed = true;
                        }
                        
                        if (hasMatch)
                        {
                            int length = searchText.Length;
                            string pre = node.Name.Substring(0, matchIndex);
                            string match = node.Name.Substring(matchIndex, length);
                            string post = node.Name.Substring(matchIndex + length);

                            if (node.PreMatch != pre) { node.PreMatch = pre; changed = true; }
                            if (node.Match != match) { node.Match = match; changed = true; }
                            if (node.PostMatch != post) { node.PostMatch = post; changed = true; }
                        }
                        else if (node.PreMatch != null)
                        {
                            node.PreMatch = null;
                            node.Match = null;
                            node.PostMatch = null;
                            changed = true;
                        }

                        // CRITICAL: Only update IsVisible if it's different.
                        if (node.IsVisible != shouldBeVisible)
                        {
                            node.IsVisible = shouldBeVisible;
                            changed = true;
                        }

                        if (changed)
                        {
                            uiUpdateCount++;
                            if (uiUpdateCount % 500 == 0) await Task.Yield();
                        }
                    }
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is InvalidOperationException)
            {
                // Silently swallow cancellation or collection modification
            }
        }

        private void ResetNodeState(FileSystemNodeModel node)
        {
            if (!node.IsVisible) node.IsVisible = true;
            if (node.HasMatch)
            {
                node.HasMatch = false;
                node.PreMatch = null;
                node.Match = null;
                node.PostMatch = null;
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