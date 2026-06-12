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
    /// <summary>
    /// Service responsible for tree filtering, metadata-based searching, and path navigation (GO TO).
    /// Optimized for handling millions of nodes with time-slicing and surgical UI updates.
    /// </summary>
    public class WadSearchBoxService
    {
        private readonly NarrativeMetadataService _metadataService;
        private readonly LogService _logService;
        private CancellationTokenSource _searchCts;
        private readonly object _searchLock = new object();

        public WadSearchBoxService(NarrativeMetadataService metadataService, LogService logService)
        {
            _metadataService = metadataService;
            _logService = logService;
        }

        #region --- Public API ---

        /// <summary>
        /// Main entry point for performing a search. Routes to either Path Navigation (/) or Filtered Search.
        /// </summary>
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
                    return await FilterTreeAsync(rootNodes, string.Empty, token, activeNode);
                }

                // MODE: Path Navigation (GO TO)
                if (searchText.Contains("/"))
                {
                    await FilterTreeAsync(rootNodes, string.Empty, token, activeNode);
                    return await ExpandToPathAsync(searchText, rootNodes);
                }

                // MODE: Normal/Metadata Filtered Search (Manual Navigation)
                // We filter the tree but return null to prevent automatic scrolling/selection.
                await FilterTreeAsync(rootNodes, searchText, token, activeNode);
                return null;
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
            ObservableRangeCollection<FileSystemNodeModel> rootNodes,
            Func<FileSystemNodeModel, Task> loadChildrenFunc)
        {
            if (string.IsNullOrEmpty(path)) return null;

            var token = PrepareCancellationToken();
            await FilterTreeAsync(rootNodes, string.Empty, token);

            return await ExpandToPathAsync(path, rootNodes);
        }

        /// <summary>
        /// Orchestrates the tree filtering process: Flatten -> Prepare -> Identify -> Apply.
        /// Returns the activeNode if resetting, or null otherwise to maintain manual navigation.
        /// </summary>
        public async Task<FileSystemNodeModel> FilterTreeAsync(
            ObservableRangeCollection<FileSystemNodeModel> nodes, 
            string searchText, 
            CancellationToken cancellationToken, 
            FileSystemNodeModel activeNode = null)
        {
            try
            {
                // 1. Identification and Flattening (Fast, no UI updates)
                var allNodes = new List<FileSystemNodeModel>();
                await Task.Run(() => Flatten(nodes, allNodes, cancellationToken), cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(searchText))
                {
                    await PerformResetAsync(nodes, allNodes, activeNode, cancellationToken);
                    return activeNode; // Return activeNode so the UI takes the user to it after clearing
                }

                // 2. Search Preparation
                bool isMetadataSearch = searchText.StartsWith("*");
                string query = (isMetadataSearch ? searchText.Substring(1) : searchText).Trim();
                string[] keywords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (keywords.Length == 0)
                {
                    await PerformResetAsync(nodes, allNodes, activeNode, cancellationToken);
                    return activeNode;
                }

                if (isMetadataSearch)
                {
                    var contextNode = allNodes.FirstOrDefault(n => !string.IsNullOrEmpty(n.SourceWadPath));
                    if (contextNode != null) await _metadataService.PreloadMetadataAsync(contextNode);
                }

                await Task.Run(async () =>
                {
                    var toShow = new HashSet<FileSystemNodeModel>();
                    var matchIndices = new Dictionary<FileSystemNodeModel, int>();
                    FileSystemNodeModel dummyMatch = null;
                    
                    // PHASE 1: Identification (Find matching nodes)
                    IdentifyMatchingNodes(allNodes, keywords, query, isMetadataSearch, toShow, matchIndices, ref dummyMatch, cancellationToken);

                    cancellationToken.ThrowIfCancellationRequested();

                    // PHASE 2: Application (Surgical UI updates with time-slicing)
                    await ApplySearchVisibilityAsync(allNodes, toShow, matchIndices, query, isMetadataSearch, cancellationToken);
                }, cancellationToken);

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
        private int IdentifyMatchingNodes(
            List<FileSystemNodeModel> allNodes, 
            string[] keywords, 
            string query,
            bool isMetadataSearch,
            HashSet<FileSystemNodeModel> toShow,
            Dictionary<FileSystemNodeModel, int> matchIndices,
            ref FileSystemNodeModel firstMatch,
            CancellationToken ct)
        {
            int matchesFound = 0;
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
                    matchesFound++;
                    if (firstMatch == null) firstMatch = node;

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
            return matchesFound;
        }

        /// <summary>
        /// Efficiently applies visibility and highlighting changes to the UI.
        /// </summary>
        private async Task ApplySearchVisibilityAsync(
            List<FileSystemNodeModel> allNodes,
            HashSet<FileSystemNodeModel> toShow,
            Dictionary<FileSystemNodeModel, int> matchIndices,
            string query,
            bool isMetadataSearch,
            CancellationToken ct)
        {
            var appSw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var node in allNodes)
            {
                ct.ThrowIfCancellationRequested();

                bool shouldBeVisible = toShow.Contains(node);
                bool hasMatch = matchIndices.TryGetValue(node, out int matchIndex);
                bool changed = false;

                // 1. Highlighting (Standard mode only)
                bool finalHasMatch = hasMatch && !isMetadataSearch;
                if (node.HasMatch != finalHasMatch) { node.HasMatch = finalHasMatch; changed = true; }
                
                if (finalHasMatch)
                {
                    string pre = node.Name.Substring(0, matchIndex);
                    string match = node.Name.Substring(matchIndex, query.Length);
                    string post = node.Name.Substring(matchIndex + query.Length);

                    if (node.PreMatch != pre) { node.PreMatch = pre; changed = true; }
                    if (node.Match != match) { node.Match = match; changed = true; }
                    if (node.PostMatch != post) { node.PostMatch = post; changed = true; }
                }
                else if (node.PreMatch != null)
                {
                    node.PreMatch = null; node.Match = null; node.PostMatch = null; changed = true;
                }

                // 2. Visibility
                if (node.IsVisible != shouldBeVisible) { node.IsVisible = shouldBeVisible; changed = true; }

                // 3. Insta-Expansion (Filtered children)
                var children = node.LoadedChildren;
                if (FileSystemNodeModel.CanHaveChildren(node.Type) && children != null)
                {
                    var visibleItems = children.Where(c => toShow.Contains(c)).ToList();
                    if (visibleItems.Count != children.Count)
                    {
                        if (node.VisibleChildren == children || node.VisibleChildren.Count != visibleItems.Count)
                        {
                            node.VisibleChildren = new ObservableRangeCollection<FileSystemNodeModel>(visibleItems);
                            changed = true;
                        }
                    }
                    else if (node.VisibleChildren != children)
                    {
                        node.VisibleChildren = null; changed = true;
                    }
                }

                // Time-slice UI updates
                if (changed && appSw.ElapsedMilliseconds > 16)
                {
                    await Task.Yield();
                    appSw.Restart();
                }
            }
        }

        private bool AllKeywordsMatch(NarrativeMetadata metadata, string[] keywords)
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

        private async Task PerformResetAsync(ObservableRangeCollection<FileSystemNodeModel> nodes, List<FileSystemNodeModel> allNodes, FileSystemNodeModel activeNode, CancellationToken cancellationToken)
        {
            var prioritizedNodes = new HashSet<FileSystemNodeModel>();
            foreach (var root in nodes) prioritizedNodes.Add(root);

            if (activeNode != null)
            {
                var current = activeNode;
                while (current != null)
                {
                    prioritizedNodes.Add(current);
                    if (current.Parent != null && current.Parent.LoadedChildren != null)
                        foreach (var sibling in current.Parent.LoadedChildren) prioritizedNodes.Add(sibling);
                    current = current.Parent;
                }
            }

            foreach (var node in allNodes)
            {
                if (node.IsExpanded)
                {
                    prioritizedNodes.Add(node);
                    var children = node.LoadedChildren;
                    if (children != null)
                        foreach (var child in children) prioritizedNodes.Add(child);
                }
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var node in prioritizedNodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ResetNodeState(node);
                if (sw.ElapsedMilliseconds > 16) { await Task.Yield(); sw.Restart(); }
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(50, cancellationToken);
                    var bgSw = System.Diagnostics.Stopwatch.StartNew();
                    foreach (var node in allNodes)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        if (prioritizedNodes.Contains(node)) continue;

                        if (!node.IsVisible || node.HasMatch || node.VisibleChildren != node.LoadedChildren)
                        {
                            ResetNodeState(node);
                            if (bgSw.ElapsedMilliseconds > 16) { await Task.Yield(); bgSw.Restart(); }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logService.LogError(ex, "An error occurred during background search reset.");
                }
            }, cancellationToken);
        }

        private void ResetNodeState(FileSystemNodeModel node)
        {
            if (!node.IsVisible) node.IsVisible = true;
            if (node.VisibleChildren != node.LoadedChildren) node.VisibleChildren = null;
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
                var children = node.LoadedChildren;
                if (children != null)
                {
                    for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
                }
            }
        }

        private async Task<FileSystemNodeModel> ExpandToPathAsync(string path, ObservableRangeCollection<FileSystemNodeModel> rootNodes)
        {
            path = path.Replace("\\", "/").Trim('/');
            if (string.IsNullOrEmpty(path)) return null;
            string[] pathComponents = path.Split('/');
            return await FindNodeByPathSuffixAsync(rootNodes, pathComponents, 0);
        }

        private async Task<FileSystemNodeModel> FindNodeByPathSuffixAsync(IEnumerable<FileSystemNodeModel> nodes, string[] pathSuffix, int suffixIndex)
        {
            if (nodes == null) return null;
            foreach (var node in nodes)
            {
                bool isMatch = node.Name.Equals(pathSuffix[suffixIndex], StringComparison.OrdinalIgnoreCase);

                if (isMatch)
                {
                    if (suffixIndex == pathSuffix.Length - 1) return node;

                    var children = node.LoadedChildren;
                    if (children != null)
                    {
                        var foundInDescendant = await FindNodeByPathSuffixAsync(children, pathSuffix, suffixIndex + 1);
                        if (foundInDescendant != null) return foundInDescendant;
                    }
                }

                var childrenFallback = node.LoadedChildren;
                if (childrenFallback != null && childrenFallback.Any())
                {
                    var foundDeeper = await FindNodeByPathSuffixAsync(childrenFallback, pathSuffix, suffixIndex);
                    if (foundDeeper != null) return foundDeeper;
                }
            }
            return null;
        }

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

        #endregion
    }
}