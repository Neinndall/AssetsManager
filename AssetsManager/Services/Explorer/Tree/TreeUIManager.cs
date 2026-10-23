using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Threading.Tasks;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Services.Explorer.Tree
{
    public class TreeUIManager
    {
        private readonly ConditionalWeakTable<ItemsControl, CancellationTokenSource> _navigation = new();

        public async void SelectAndFocusNode(ItemsControl treeView, ObservableRangeCollection<FileSystemNodeModel> rootNodes,
            FileSystemNodeModel node, bool focusNode = true) =>
            await SelectAndFocusNodeAsync(treeView, rootNodes, node, focusNode);

        public void CancelNavigation(ItemsControl treeView)
        {
            if (treeView != null && _navigation.TryGetValue(treeView, out var source)) source.Cancel();
        }

        public async Task<bool> SelectAndFocusNodeAsync(ItemsControl treeView,
            ObservableRangeCollection<FileSystemNodeModel> rootNodes, FileSystemNodeModel node,
            bool focusNode = true, CancellationToken cancellationToken = default)
        {
            if (treeView == null) return false;
            treeView.Dispatcher.VerifyAccess();
            CancelNavigation(treeView);
            var path = FindNodePath(rootNodes, node);
            if (path == null) return false;

            using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _navigation.Remove(treeView);
            _navigation.Add(treeView, source);
            try
            {
                return await treeView.Dispatcher.InvokeAsync(() =>
                    NavigatePathAsync(treeView, path, node, focusNode, source.Token),
                    DispatcherPriority.Input, source.Token).Task.Unwrap();
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested) { return false; }
            finally
            {
                if (_navigation.TryGetValue(treeView, out var current) && ReferenceEquals(current, source))
                    _navigation.Remove(treeView);
            }
        }

        private static async Task<bool> NavigatePathAsync(ItemsControl container, List<FileSystemNodeModel> path,
            FileSystemNodeModel target, bool focus, CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                ItemsControl owner = container;
                foreach (var node in path)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = await RealizeItemAsync(owner, node, cancellationToken);
                    if (item == null) break;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ReferenceEquals(node, target))
                    {
                        item.BringIntoView();
                        await container.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        container.UpdateLayout();
                        cancellationToken.ThrowIfCancellationRequested();
                        // Layout can recycle any ancestor, so the previous owner may no longer belong to this path.
                        var selectedItem = GetGeneratedItem(container, path);
                        if (!ReferenceEquals(selectedItem, item)) break;
                        selectedItem.IsSelected = true;
                        cancellationToken.ThrowIfCancellationRequested();
                        if (container is TreeView tree && !ReferenceEquals(tree.SelectedItem, target)) return false;
                        target.IsSelected = true;
                        if (focus) selectedItem.Focus();
                        return true;
                    }
                    node.IsExpanded = true;
                    item.IsExpanded = true;
                    owner = item;
                }
            }
            return false;
        }

        private static TreeViewItem GetGeneratedItem(ItemsControl owner, List<FileSystemNodeModel> path)
        {
            TreeViewItem item = null;
            foreach (var node in path)
            {
                item = owner.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
                if (item == null) return null;
                owner = item;
            }
            return item;
        }

        private static async Task<TreeViewItem> RealizeItemAsync(ItemsControl owner, FileSystemNodeModel node,
            CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (owner.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem generated) return generated;
                int index = owner.Items.IndexOf(node);
                if (index < 0) return null;

                owner.ApplyTemplate();
                owner.UpdateLayout();
                cancellationToken.ThrowIfCancellationRequested();
                if (owner.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem realized) return realized;
                // A nested panel's indexes refer to different items; only the owner's host can realize this node.
                if (FindItemsHost(owner, owner) is VirtualizingStackPanel panel)
                    panel.BringIndexIntoViewPublic(index);

                await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return owner.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
        }

        private static Panel FindItemsHost(DependencyObject parent, ItemsControl owner)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Panel panel && panel.IsItemsHost && ReferenceEquals(ItemsControl.GetItemsOwner(panel), owner))
                    return panel;
                if (child is ItemsControl) continue;
                var found = FindItemsHost(child, owner);
                if (found != null) return found;
            }
            return null;
        }

        public List<FileSystemNodeModel> FindNodePath(IEnumerable<FileSystemNodeModel> nodes, FileSystemNodeModel nodeToFind)
        {
            if (nodes == null || nodeToFind == null) return null;
            var path = new List<FileSystemNodeModel>();
            var visited = new HashSet<FileSystemNodeModel>();
            for (var current = nodeToFind; current != null; current = current.Parent)
            {
                if (!visited.Add(current)) return null;
                path.Add(current);
            }
            path.Reverse();
            return nodes.Any(root => ReferenceEquals(root, path[0])) ? path : null;
        }

        public void CollapseAll(FileSystemNodeModel node)
        {
            if (node == null) return;
            node.IsExpanded = false;
            var children = node.LoadedChildren;
            if (children != null)
            {
                foreach (var child in children)
                    CollapseAll(child);
            }
        }

        public TreeViewItem SafeVisualUpwardSearch(DependencyObject source)
        {
            while (source != null && !(source is TreeViewItem))
                source = (source is Visual || source is System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            return source as TreeViewItem;
        }

        public List<FileSystemNodeModel> GetSelectedNodes(IEnumerable<FileSystemNodeModel> rootNodes, FileSystemNodeModel currentSelectedItem)
        {
            var selected = new List<FileSystemNodeModel>();
            FindMultiSelectedNodes(rootNodes, selected);

            if (selected.Count > 0)
            {
                if (currentSelectedItem != null && !selected.Contains(currentSelectedItem) && !selected.Any(n => IsAncestor(currentSelectedItem, n)))
                {
                    selected.Insert(0, currentSelectedItem);
                }
                return selected;
            }

            return currentSelectedItem != null ? new List<FileSystemNodeModel> { currentSelectedItem } : new List<FileSystemNodeModel>();
        }

        private bool IsAncestor(FileSystemNodeModel potentialAncestor, FileSystemNodeModel node)
        {
            for (var p = node.Parent; p != null; p = p.Parent)
            {
                if (p == potentialAncestor) return true;
            }
            return false;
        }

        private void FindMultiSelectedNodes(IEnumerable<FileSystemNodeModel> nodes, List<FileSystemNodeModel> result)
        {
            if (nodes == null) return;
            foreach (var node in nodes)
            {
                if (node.IsMultiSelected) result.Add(node);
                var children = node.LoadedChildren;
                if (children != null) FindMultiSelectedNodes(children, result);
            }
        }

    }
}
