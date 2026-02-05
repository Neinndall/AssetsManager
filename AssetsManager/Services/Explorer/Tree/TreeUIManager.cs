using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AssetsManager.Views.Models.Explorer;

namespace AssetsManager.Services.Explorer.Tree
{
    public class TreeUIManager
    {
        public void SelectAndFocusNode(ItemsControl treeView, ObservableCollection<FileSystemNodeModel> rootNodes, FileSystemNodeModel node, bool focusNode = true)
        {
            var path = FindNodePath(rootNodes, node);
            if (path == null) return;

            var container = treeView;
            TreeViewItem itemContainer = null;

            foreach (var parentNode in path)
            {
                if (parentNode == node) break;

                itemContainer = (TreeViewItem)container.ItemContainerGenerator.ContainerFromItem(parentNode);
                if (itemContainer == null)
                {
                    container.UpdateLayout();
                    itemContainer = (TreeViewItem)container.ItemContainerGenerator.ContainerFromItem(parentNode);
                }

                if (itemContainer == null) return;

                parentNode.IsExpanded = true;
                if (!itemContainer.IsExpanded)
                {
                    itemContainer.IsExpanded = true;
                }
                container = itemContainer;
            }

            itemContainer = (TreeViewItem)container.ItemContainerGenerator.ContainerFromItem(node);
            if (itemContainer == null)
            {
                container.UpdateLayout();
                itemContainer = (TreeViewItem)container.ItemContainerGenerator.ContainerFromItem(node);
            }

            if (itemContainer != null)
            {
                itemContainer.BringIntoView();
                itemContainer.IsSelected = true;
                if (focusNode)
                {
                    itemContainer.Focus();
                }
            }
        }

        public List<FileSystemNodeModel> FindNodePath(IEnumerable<FileSystemNodeModel> nodes, FileSystemNodeModel nodeToFind)
        {
            foreach (var n in nodes)
            {
                if (n == nodeToFind)
                {
                    return new List<FileSystemNodeModel> { n };
                }

                if (n.Children != null)
                {
                    var path = FindNodePath(n.Children, nodeToFind);
                    if (path != null)
                    {
                        path.Insert(0, n);
                        return path;
                    }
                }
            }
            return null;
        }

        public void CollapseAll(FileSystemNodeModel node)
        {
            node.IsExpanded = false;
            if (node.Children == null) return;
            foreach (var child in node.Children)
            {
                CollapseAll(child);
            }
        }

        public TreeViewItem SafeVisualUpwardSearch(DependencyObject source)
        {
            while (source != null && !(source is TreeViewItem))
            {
                if (source is Visual || source is System.Windows.Media.Media3D.Visual3D)
                {
                    source = VisualTreeHelper.GetParent(source);
                }
                else
                {
                    source = LogicalTreeHelper.GetParent(source);
                }
            }
            return source as TreeViewItem;
        }

        public List<FileSystemNodeModel> GetSelectedNodes(IEnumerable<FileSystemNodeModel> rootNodes, FileSystemNodeModel currentSelectedItem)
        {
            var selected = new List<FileSystemNodeModel>();
            FindMultiSelectedNodes(rootNodes, selected);

            if (selected.Count == 0 && currentSelectedItem != null)
            {
                selected.Add(currentSelectedItem);
            }

            return selected;
        }

        private void FindMultiSelectedNodes(IEnumerable<FileSystemNodeModel> nodes, List<FileSystemNodeModel> result)
        {
            if (nodes == null) return;
            foreach (var node in nodes)
            {
                if (node.IsMultiSelected)
                {
                    result.Add(node);
                }
                FindMultiSelectedNodes(node.Children, result);
            }
        }
    }
}
