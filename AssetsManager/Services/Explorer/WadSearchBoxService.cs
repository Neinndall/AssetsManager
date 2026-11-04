using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AssetsManager.Services.Explorer
{
    /// <summary>
    /// Service responsible for tree filtering, metadata-based searching, and path navigation (GO TO).
    /// Optimized for handling millions of nodes with time-slicing and surgical UI updates.
    /// </summary>
    public class WadSearchBoxService
    {
        private readonly NarrativeMetadataService _metadataService;
        private readonly LogService _logService;
        private CancellationTokenSource _searchCts;
        private ObservableRangeCollection<FileSystemNodeModel> _activeSearchRoots;
        private readonly object _searchLock = new object();
        private readonly object _indexLock = new object();
        private readonly ConditionalWeakTable<ObservableRangeCollection<FileSystemNodeModel>, List<FileSystemNodeModel>> _treeIndexes = new();

        public WadSearchBoxService(NarrativeMetadataService metadataService, LogService logService)
        {
            _metadataService = metadataService;
            _logService = logService;
        }

        public void RebuildIndex(ObservableRangeCollection<FileSystemNodeModel> rootNodes)
        {
            if (rootNodes == null) return;

            var index = BuildIndex(rootNodes, CancellationToken.None);

            lock (_indexLock)
            {
                _treeIndexes.Remove(rootNodes);
                _treeIndexes.Add(rootNodes, index);
            }
        }

        public void InvalidateIndex(ObservableRangeCollection<FileSystemNodeModel> rootNodes)
        {
            if (rootNodes == null) return;

            CancelSearch(rootNodes);

            lock (_indexLock)
            {
                _treeIndexes.Remove(rootNodes);
            }
        }

        public void CancelSearch(ObservableRangeCollection<FileSystemNodeModel> rootNodes)
        {
            if (rootNodes == null) return;

            lock (_searchLock)
            {
                if (ReferenceEquals(_activeSearchRoots, rootNodes))
                {
                    _searchCts?.Cancel();
                    _activeSearchRoots = null;
                }
            }
        }

        #region --- Public API ---

        /// <summary>
        /// Main entry point for performing a search. Routes to either Path Navigation (/) or Filtered Search.
        /// </summary>
        public async Task<FileSystemNodeModel> PerformSearchAsync(
            string searchText,
            ObservableRangeCollection<FileSystemNodeModel> rootNodes,
            FileSystemNodeModel activeNode = null)
        {
            var token = PrepareCancellationToken(rootNodes);

            try
            {
                bool navigate = searchText?.Contains('/') == true;
                var selected = await FilterTreeAsync(rootNodes, navigate ? string.Empty : searchText, token, activeNode);
                token.ThrowIfCancellationRequested();
                return navigate ? await FindPathAsync(searchText, rootNodes, token) : selected;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        /// <summary>
        /// Resets search state and navigates directly to a path.
        /// </summary>
        public async Task<FileSystemNodeModel> NavigateToPathAsync(
            string path,
            ObservableRangeCollection<FileSystemNodeModel> rootNodes)
        {
            if (string.IsNullOrEmpty(path)) return null;

            var token = PrepareCancellationToken(rootNodes);
            try
            {
                await FilterTreeAsync(rootNodes, string.Empty, token);
                token.ThrowIfCancellationRequested();
                return await FindPathAsync(path, rootNodes, token);
            }
            catch (OperationCanceledException) { return null; }
        }

        /// <summary>
        /// Orchestrates the tree filtering process: Flatten -> Prepare -> Identify -> Apply.
        /// Returns the activeNode if resetting, or null otherwise to maintain manual navigation.
        /// </summary>
        private async Task<FileSystemNodeModel> FilterTreeAsync(
            ObservableRangeCollection<FileSystemNodeModel> nodes, 
            string searchText, 
            CancellationToken cancellationToken, 
            FileSystemNodeModel activeNode = null)
        {
            try
            {
                // 1. Reuse the immutable node projection for this tree.
                var allNodes = await GetIndexedNodesAsync(nodes, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                bool isMetadataSearch = searchText?.StartsWith('*') == true;
                string query = (isMetadataSearch ? searchText.Substring(1) : searchText ?? string.Empty).Trim();
                string[] keywords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (keywords.Length == 0)
                {
                    await PerformResetAsync(allNodes, cancellationToken);
                    return activeNode;
                }

                if (isMetadataSearch)
                {
                    var contextNode = allNodes.FirstOrDefault(n => !string.IsNullOrEmpty(n.SourceWadPath));
                    if (contextNode != null) await _metadataService.PreloadMetadataAsync(contextNode);
                }

                var searchResult = await Task.Run(() =>
                {
                    var toShow = new HashSet<FileSystemNodeModel>();
                    var matchIndices = new Dictionary<FileSystemNodeModel, int>();
                    
                    // PHASE 1: Identification (Find matching nodes)
                    IdentifyMatchingNodes(allNodes, keywords, query, isMetadataSearch, toShow, matchIndices, cancellationToken);

                    cancellationToken.ThrowIfCancellationRequested();

                    var visibleChildren = BuildVisibleChildren(allNodes, toShow, cancellationToken);
                    return new TreeFilterProjection(toShow, matchIndices, visibleChildren);
                }, cancellationToken);

                await (Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher)
                    .InvokeAsync(() => ApplySearchVisibilityAsync(allNodes, searchResult, query, isMetadataSearch, cancellationToken))
                    .Task
                    .Unwrap();

                return null; // Always return null during search to enforce manual navigation
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "An error occurred during search filtering.");
                return null;
            }
        }

        #endregion

        #region --- Core Logic (Private) ---

        /// <summary>
        /// Evaluates each node to see if it matches the search criteria.
        /// </summary>
        private void IdentifyMatchingNodes(
            IReadOnlyList<FileSystemNodeModel> allNodes,
            string[] keywords, 
            string query,
            bool isMetadataSearch,
            HashSet<FileSystemNodeModel> toShow,
            Dictionary<FileSystemNodeModel, int> matchIndices,
            CancellationToken ct)
        {
            foreach (var node in allNodes)
            {
                ct.ThrowIfCancellationRequested();
                if (node.Name == "Loading...") continue;

                bool found = false;
                int matchIndex = -1;

                if (isMetadataSearch)
                {
                    if (_metadataService.IsMetadataSupported(node))
                    {
                        var metadata = _metadataService.GetMetadataSync(node);
                        if (metadata != null && AllKeywordsMatch(metadata, keywords))
                        {
                            found = true;
                            matchIndex = 0;
                        }
                    }
                }
                else
                {
                    matchIndex = node.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    if (matchIndex >= 0) found = true;
                }

                if (found)
                {
                    matchIndices[node] = matchIndex;
                    toShow.Add(node);

                    // Propagate visibility upwards
                    var parent = node.Parent;
                    while (parent != null && !toShow.Contains(parent))
                    {
                        toShow.Add(parent);
                        parent = parent.Parent;
                    }
                }
            }
        }

        /// <summary>
        /// Efficiently applies visibility and highlighting changes to the UI.
        /// </summary>
        private async Task ApplySearchVisibilityAsync(
            IReadOnlyList<FileSystemNodeModel> allNodes,
            TreeFilterProjection searchResult,
            string query,
            bool isMetadataSearch,
            CancellationToken ct)
        {
            var appSw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var node in allNodes)
            {
                ct.ThrowIfCancellationRequested();

                bool shouldBeVisible = searchResult.VisibleNodes.Contains(node);
                bool hasMatch = searchResult.MatchIndices.TryGetValue(node, out int matchIndex);

                // 1. Highlighting (Standard mode only)
                bool finalHasMatch = hasMatch && !isMetadataSearch;
                if (node.HasMatch != finalHasMatch) node.HasMatch = finalHasMatch;
                
                if (finalHasMatch)
                {
                    string pre = node.Name.Substring(0, matchIndex);
                    string match = node.Name.Substring(matchIndex, query.Length);
                    string post = node.Name.Substring(matchIndex + query.Length);

                    if (node.PreMatch != pre) node.PreMatch = pre;
                    if (node.Match != match) node.Match = match;
                    if (node.PostMatch != post) node.PostMatch = post;
                }
                else if (node.PreMatch != null)
                {
                    node.PreMatch = null; node.Match = null; node.PostMatch = null;
                }

                // 2. Visibility
                if (node.IsVisible != shouldBeVisible) node.IsVisible = shouldBeVisible;

                // 3. Insta-Expansion (Filtered children)
                var children = node.LoadedChildren;
                if (FileSystemNodeModel.CanHaveChildren(node.Type) && children != null)
                {
                    if (searchResult.VisibleChildren.TryGetValue(node, out var visibleItems))
                    {
                        node.SetVisibleChildren(visibleItems);
                    }
                    else if (node.HasFilteredChildren)
                    {
                        node.SetVisibleChildren(null);
                    }
                }

                // Time-slice UI updates
                if (appSw.ElapsedMilliseconds > 16)
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    appSw.Restart();
                }
            }
        }

        private static Dictionary<FileSystemNodeModel, IReadOnlyList<FileSystemNodeModel>> BuildVisibleChildren(
            IReadOnlyList<FileSystemNodeModel> allNodes,
            HashSet<FileSystemNodeModel> visibleNodes,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<FileSystemNodeModel, IReadOnlyList<FileSystemNodeModel>>();
            foreach (var node in allNodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var children = node.LoadedChildren;
                if (children == null || children.Count == 0) continue;

                List<FileSystemNodeModel> visibleChildren = null;
                for (int i = 0; i < children.Count; i++)
                {
                    var child = children[i];
                    if (visibleNodes.Contains(child))
                    {
                        visibleChildren ??= new List<FileSystemNodeModel>();
                        visibleChildren.Add(child);
                    }
                }

                if (visibleChildren == null || visibleChildren.Count != children.Count)
                {
                    result[node] = visibleChildren is null
                        ? Array.Empty<FileSystemNodeModel>()
                        : visibleChildren;
                }
            }

            return result;
        }

        private static bool AllKeywordsMatch(NarrativeMetadata metadata, string[] keywords)
        {
            foreach (var kw in keywords)
            {
                bool inTitle = metadata.Title?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false;
                bool inDesc = metadata.Description?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false;
                if (!inTitle && !inDesc) return false;
            }
            return true;
        }

        #endregion

        #region --- Reset Logic ---

        private async Task PerformResetAsync(IReadOnlyList<FileSystemNodeModel> allNodes, CancellationToken cancellationToken)
        {
            // Restore descendants before exposing their parent collections. Navigation must wait
            // for the complete projection so later resets cannot move the selected row again.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = allNodes.Count - 1; i >= 0; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ResetNodeState(allNodes[i]);
                if (sw.ElapsedMilliseconds > 16)
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    sw.Restart();
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        private void ResetNodeState(FileSystemNodeModel node)
        {
            if (!node.IsVisible) node.IsVisible = true;
            if (node.HasFilteredChildren) node.SetVisibleChildren(null);
            if (node.HasMatch)
            {
                node.HasMatch = false;
                node.PreMatch = null;
                node.Match = null;
                node.PostMatch = null;
            }
        }

        #endregion

        #region --- Helpers & Navigation ---

        private static void Flatten(IEnumerable<FileSystemNodeModel> roots, List<FileSystemNodeModel> result, CancellationToken ct)
        {
            if (roots == null) return;
            var stack = new Stack<FileSystemNodeModel>();
            foreach (var root in roots.Reverse()) stack.Push(root);

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var node = stack.Pop();
                result.Add(node);
                var children = node.LoadedChildren;
                if (children != null)
                {
                    for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
                }
            }
        }

        private async Task<IReadOnlyList<FileSystemNodeModel>> GetIndexedNodesAsync(
            ObservableRangeCollection<FileSystemNodeModel> rootNodes,
            CancellationToken cancellationToken)
        {
            lock (_indexLock)
            {
                if (_treeIndexes.TryGetValue(rootNodes, out var existingIndex))
                {
                    return existingIndex;
                }
            }

            var newIndex = await Task.Run(() => BuildIndex(rootNodes, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_indexLock)
            {
                if (_treeIndexes.TryGetValue(rootNodes, out var existingIndex))
                {
                    return existingIndex;
                }

                _treeIndexes.Add(rootNodes, newIndex);
                return newIndex;
            }
        }

        private static List<FileSystemNodeModel> BuildIndex(
            ObservableRangeCollection<FileSystemNodeModel> rootNodes,
            CancellationToken cancellationToken)
        {
            var nodes = new List<FileSystemNodeModel>();
            Flatten(rootNodes, nodes, cancellationToken);
            return nodes;
        }

        private async Task<FileSystemNodeModel> FindPathAsync(string path, ObservableRangeCollection<FileSystemNodeModel> rootNodes, CancellationToken token)
        {
            string[] components = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (components.Length == 0) return null;
            var indexed = await GetIndexedNodesAsync(rootNodes, token);
            return await Task.Run(() =>
            {
                foreach (var node in indexed)
                {
                    token.ThrowIfCancellationRequested();
                    if (!string.Equals(node.Name, components[^1], StringComparison.OrdinalIgnoreCase)) continue;
                    var current = node;
                    int index = components.Length - 1;
                    while (index >= 0 && current != null && string.Equals(current.Name, components[index], StringComparison.OrdinalIgnoreCase))
                    {
                        current = current.Parent;
                        index--;
                    }
                    if (index < 0)
                    {
                        token.ThrowIfCancellationRequested();
                        return node;
                    }
                }
                return null;
            }, token);
        }

        private CancellationToken PrepareCancellationToken(ObservableRangeCollection<FileSystemNodeModel> rootNodes)
        {
            lock (_searchLock)
            {
                _searchCts?.Cancel();
                _searchCts?.Dispose();
                _searchCts = new CancellationTokenSource();
                _activeSearchRoots = rootNodes;
                return _searchCts.Token;
            }
        }

        #endregion

        private sealed record TreeFilterProjection(
            HashSet<FileSystemNodeModel> VisibleNodes,
            Dictionary<FileSystemNodeModel, int> MatchIndices,
            Dictionary<FileSystemNodeModel, IReadOnlyList<FileSystemNodeModel>> VisibleChildren);
    }
}
