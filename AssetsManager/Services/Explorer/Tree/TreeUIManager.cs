using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Threading.Tasks;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Services.Explorer.Tree
{
    public class TreeUIManager
    {
        public void SelectAndFocusNode(ItemsControl treeView, ObservableRangeCollection<FileSystemNodeModel> rootNodes, FileSystemNodeModel node, bool focusNode = true)
        {
            var path = FindNodePath(rootNodes, node);
            if (path == null) return;

            // Use Input priority to ensure it responds fast to search clearing/user actions
            treeView.Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (focusNode) treeView.Focus();

                // Start async navigation to handle virtualization properly
                await NavigatePathAsync(treeView, path, node, focusNode);
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private async Task NavigatePathAsync(ItemsControl container, List<FileSystemNodeModel> path, FileSystemNodeModel target, bool focus)
        {
            // Fast-path: if target is already selected and all ancestors are expanded,
            // we do not need to do any heavy scrolling or retries. Just ensure focus/view if possible.
            bool isAlreadyPrepared = path.All(n => n == target || n.IsExpanded);
            if (target.IsSelected && isAlreadyPrepared)
            {
                var finalContainer = GetGeneratedContainerForPath(container, path);
                if (finalContainer != null)
                {
                    if (focus) finalContainer.Focus();
                    finalContainer.BringIntoView();
                    return;
                }
            }

            ItemsControl currentContainer = container;

            for (int i = 0; i < path.Count; i++)
            {
                var node = path[i];
                
                // Ensure expansion
                if (node != target && !node.IsExpanded) node.IsExpanded = true;
                if (node == target) node.IsSelected = true;

                TreeViewItem itemContainer = currentContainer.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;

                if (itemContainer == null)
                {
                    // Virtualization fix: scroll to it so the container is generated
                    var vsp = FindVisualChild<VirtualizingStackPanel>(currentContainer);
                    if (vsp != null)
                    {
                        int index = currentContainer.Items.IndexOf(node);
                        if (index >= 0) vsp.BringIndexIntoViewPublic(index);
                    }

                    // A single tiny wait is usually enough for the ItemContainerGenerator to catch up
                    await Task.Delay(25);
                    itemContainer = currentContainer.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
                }

                if (itemContainer != null)
                {
                    if (node == target)
                    {
                        itemContainer.IsSelected = true;
                        itemContainer.BringIntoView();
                        if (focus) itemContainer.Focus();
                        return;
                    }
                    
                    if (!itemContainer.IsExpanded) itemContainer.IsExpanded = true;
                    currentContainer = itemContainer;
                }
                else
                {
                    break;
                }
            }
        }

        private TreeViewItem GetGeneratedContainerForPath(ItemsControl currentContainer, List<FileSystemNodeModel> path)
        {
            foreach (var node in path)
            {
                if (currentContainer == null) return null;
                var itemContainer = currentContainer.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
                if (itemContainer == null) return null;
                currentContainer = itemContainer;
            }
            return currentContainer as TreeViewItem;
        }

        public List<FileSystemNodeModel> FindNodePath(IEnumerable<FileSystemNodeModel> nodes, FileSystemNodeModel nodeToFind)
        {
            if (nodeToFind == null) return null;
            
            var path = new List<FileSystemNodeModel>();
            var current = nodeToFind;
            
            while (current != null)
            {
                path.Insert(0, current);
                current = current.Parent;
            }
            
            return path;
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
                if (currentSelectedItem != null && !selected.Contains(currentSelectedItem))
                {
                    selected.Insert(0, currentSelectedItem);
                }
                return selected;
            }

            return currentSelectedItem != null ? new List<FileSystemNodeModel> { currentSelectedItem } : new List<FileSystemNodeModel>();
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

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result) return result;
                var descendant = FindVisualChild<T>(child);
                if (descendant != null) return descendant;
            }
            return null;
        }
    }
}
