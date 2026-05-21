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

            if (string.IsNullOrEmpty(searchText))
            {
                await FilterTreeAsync(rootNodes, string.Empty, token);
                return null;
            }

            if (searchText.Contains("/"))
            {
                await FilterTreeAsync(rootNodes, string.Empty, token);
                var targetNode = await ExpandToPathAsync(searchText, rootNodes, loadChildrenFunc);
                return targetNode;
            }
            else
            {
                await FilterTreeAsync(rootNodes, searchText, token);
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

            return await ExpandToPathAsync(path, rootNodes, loadChildrenFunc);
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
            ObservableRangeCollection<FileSystemNodeModel> rootNodes,
            Func<FileSystemNodeModel, Task> loadChildrenFunc)
        {
            path = path.Replace("\\", "/").Trim('/');
            if (string.IsNullOrEmpty(path)) return null;

            string[] pathComponents = path.Split('/');
            var targetNode = await FindNodeByPathSuffixAsync(rootNodes, pathComponents, loadChildrenFunc);

            return targetNode;
        }

        private async Task<FileSystemNodeModel> FindNodeByPathSuffixAsync(
            IEnumerable<FileSystemNodeModel> nodes,
            string[] pathSuffix,
            Func<FileSystemNodeModel, Task> loadChildrenFunc)
        {
            foreach (var node in nodes)
            {
                bool isFirstComponentLast = pathSuffix.Length == 1;
                bool isMatch = isFirstComponentLast
                    ? node.Name.StartsWith(pathSuffix[0], StringComparison.OrdinalIgnoreCase)
                    : node.Name.Equals(pathSuffix[0], StringComparison.OrdinalIgnoreCase);

                if (isMatch)
                {
                    FileSystemNodeModel potentialMatch = node;
                    bool match = true;

                    for (int i = 1; i < pathSuffix.Length; i++)
                    {
                        if (potentialMatch.Type == NodeType.WadFile || potentialMatch.Type == NodeType.RealDirectory || potentialMatch.Type == NodeType.VirtualDirectory)
                        {
                            if (potentialMatch.Children.Count == 0 || (potentialMatch.Children.Count == 1 && potentialMatch.Children[0].Name == "Loading..."))
                            {
                                await loadChildrenFunc(potentialMatch);
                            }
                        }

                        bool isLastComponentInLoop = (i == pathSuffix.Length - 1);
                        var currentComponent = pathSuffix[i];

                        var nextNode = isLastComponentInLoop
                            ? potentialMatch.Children.FirstOrDefault(c => c.Name.StartsWith(currentComponent, StringComparison.OrdinalIgnoreCase))
                            : potentialMatch.Children.FirstOrDefault(c => c.Name.Equals(currentComponent, StringComparison.OrdinalIgnoreCase));

                        if (nextNode != null)
                        {
                            potentialMatch = nextNode;
                        }
                        else
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        return potentialMatch;
                    }
                }
                else
                {
                    if (node.Type == NodeType.WadFile || node.Type == NodeType.RealDirectory || node.Type == NodeType.VirtualDirectory)
                    {
                        if (node.Children.Count == 0 || (node.Children.Count == 1 && node.Children[0].Name == "Loading..."))
                        {
                            await loadChildrenFunc(node);
                        }
                        var foundInChild = await FindNodeByPathSuffixAsync(node.Children, pathSuffix, loadChildrenFunc);
                        if (foundInChild != null)
                        {
                            return foundInChild;
                        }
                    }
                }
            }
            return null;
        }
    }
}
