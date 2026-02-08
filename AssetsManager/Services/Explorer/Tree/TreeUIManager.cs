using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Threading.Tasks;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Utils;

namespace AssetsManager.Services.Explorer.Tree
{
    public class TreeUIManager
    {
        public void SelectAndFocusNode(ItemsControl treeView, ObservableRangeCollection<FileSystemNodeModel> rootNodes, FileSystemNodeModel node, bool focusNode = true)
        {
            var path = FindNodePath(rootNodes, node);
            if (path == null) return;

            if (focusNode) treeView.Focus();
            treeView.UpdateLayout();

            // Start async navigation to handle virtualization properly
            _ = NavigatePathAsync(treeView, path, node, focusNode);
        }

        private async Task NavigatePathAsync(ItemsControl container, List<FileSystemNodeModel> path, FileSystemNodeModel target, bool focus)
        {
            ItemsControl currentContainer = container;

            for (int i = 0; i < path.Count; i++)
            {
                var node = path[i];
                node.IsExpanded = true;
                if (node == target) node.IsSelected = true;

                TreeViewItem itemContainer = currentContainer.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;

                if (itemContainer == null)
                {
                    // Virtualization fix: search for the panel and force scroll to index
                    var vsp = FindVisualChild<VirtualizingStackPanel>(currentContainer);
                    if (vsp != null)
                    {
                        int index = currentContainer.Items.IndexOf(node);
                        if (index >= 0) vsp.BringIndexIntoViewPublic(index);
                    }
                    
                    await Task.Delay(30); // Give time for virtualization to render
                    currentContainer.UpdateLayout();
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
                    itemContainer.IsExpanded = true;
                    currentContainer = itemContainer;
                }
            }
        }

        public List<FileSystemNodeModel> FindNodePath(IEnumerable<FileSystemNodeModel> nodes, FileSystemNodeModel nodeToFind)
        {
            if (nodes == null) return null;
            foreach (var n in nodes)
            {
                if (n == nodeToFind) return new List<FileSystemNodeModel> { n };
                var path = FindNodePath(n.Children, nodeToFind);
                if (path != null) { path.Insert(0, n); return path; }
            }
            return null;
        }

        public void CollapseAll(FileSystemNodeModel node)
        {
            if (node == null) return;
            node.IsExpanded = false;
            foreach (var child in node.Children ?? Enumerable.Empty<FileSystemNodeModel>()) 
                CollapseAll(child);
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
            return selected.Count > 0 ? selected : (currentSelectedItem != null ? new List<FileSystemNodeModel> { currentSelectedItem } : selected);
        }

        private void FindMultiSelectedNodes(IEnumerable<FileSystemNodeModel> nodes, List<FileSystemNodeModel> result)
        {
            if (nodes == null) return;
            foreach (var node in nodes)
            {
                if (node.IsMultiSelected) result.Add(node);
                FindMultiSelectedNodes(node.Children, result);
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
