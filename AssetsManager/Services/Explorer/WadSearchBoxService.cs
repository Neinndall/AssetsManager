
using AssetsManager.Services.Core;
using AssetsManager.Views.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace AssetsManager.Services.Explorer
{
    public class WadSearchBoxService
    {
        public async Task<FileSystemNodeModel> PerformSearchAsync(
            string searchText,
            ObservableCollection<FileSystemNodeModel> rootNodes,
            Func<FileSystemNodeModel, Task> loadChildrenFunc)
        {
            if (string.IsNullOrEmpty(searchText))
            {
                await FilterTreeAsync(rootNodes, string.Empty);
                return null;
            }

            if (searchText.Contains("/"))
            {
                await FilterTreeAsync(rootNodes, string.Empty);
                var targetNode = await ExpandToPathAsync(searchText, rootNodes, loadChildrenFunc);
                return targetNode;
            }
            else
            {
                await FilterTreeAsync(rootNodes, searchText);
                return null;
            }
        }

        public async Task FilterTreeAsync(ObservableCollection<FileSystemNodeModel> nodes, string searchText)
        {
            await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(searchText))
                {
                    SetVisibility(nodes, true);
                    return;
                }

                FilterNodes(nodes, searchText);
            });
        }

        private async Task<FileSystemNodeModel> ExpandToPathAsync(
            string path,
            ObservableCollection<FileSystemNodeModel> rootNodes,
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

        private bool FilterNodes(IEnumerable<FileSystemNodeModel> nodes, string searchText)
        {
            bool somethingVisibleInThisLevel = false;
            if (nodes == null) return false;

            foreach (var node in nodes)
            {
                if (node.Name == "Loading...")
                {
                    node.IsVisible = false;
                    continue;
                }

                bool selfMatches = node.Name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;
                bool childMatches = FilterNodes(node.Children, searchText);

                node.IsVisible = selfMatches || childMatches;

                if (selfMatches)
                {
                    var index = node.Name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
                    var length = searchText.Length;

                    node.PreMatch = node.Name.Substring(0, index);
                    node.Match = node.Name.Substring(index, length);
                    node.PostMatch = node.Name.Substring(index + length);
                    node.HasMatch = true;
                }
                else
                {
                    node.HasMatch = false;
                    node.PreMatch = null;
                    node.Match = null;
                    node.PostMatch = null;
                }

                if (node.IsVisible)
                {
                    somethingVisibleInThisLevel = true;
                }
            }
            return somethingVisibleInThisLevel;
        }

        private void SetVisibility(IEnumerable<FileSystemNodeModel> nodes, bool isVisible)
        {
            if (nodes == null) return;
            foreach (var node in nodes)
            {
                node.IsVisible = isVisible;
                node.HasMatch = false;
                node.PreMatch = null;
                node.Match = null;
                node.PostMatch = null;

                if (!isVisible)
                {
                    node.IsExpanded = false;
                }
                SetVisibility(node.Children, isVisible);
            }
        }
    }
}
