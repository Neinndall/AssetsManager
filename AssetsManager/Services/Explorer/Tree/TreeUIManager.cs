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
            ItemsControl currentContainer = container;

            for (int i = 0; i < path.Count; i++)
            {
                var node = path[i];
                
                // Set expansion first (only if needed to avoid churn)
                if (node != target && !node.IsExpanded) node.IsExpanded = true;
                if (node == target) node.IsSelected = true;

                TreeViewItem itemContainer = null;
                
                // Initial check: if container is already there, skip loop
                itemContainer = currentContainer.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;

                if (itemContainer == null)
                {
                    // Retry loop to handle virtualization and async generation
                    for (int retry = 0; retry < 15; retry++)
                    {
                        itemContainer = currentContainer.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;

                        if (itemContainer != null) break;

                        // Virtualization fix: search for the panel and force scroll to index
                        var vsp = FindVisualChild<VirtualizingStackPanel>(currentContainer);
                        if (vsp != null)
                        {
                            int index = currentContainer.Items.IndexOf(node);
                            if (index >= 0) vsp.BringIndexIntoViewPublic(index);
                        }

                        // Use a shorter initial delay and allow UI thread to process without forcing layout yet
                        if (retry < 5)
                        {
                            await Task.Delay(20);
                        }
                        else
                        {
                            await Task.Delay(35);
                            // Only force layout as a last resort and sparingly
                            if (retry % 5 == 0) currentContainer.UpdateLayout();
                        }
                    }
                }

                if (itemContainer != null)
                {
                    if (node == target)
                    {
                        itemContainer.IsSelected = true;
                        
                        // Only bring into view if not already visible to avoid "jumping"
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

            if (selected.Count > 0)
            {
                // Si hay multi-selección, aseguramos que el ítem principal (SelectedItem) 
                // también esté en la lista si no lo estaba ya.
                if (currentSelectedItem != null && !selected.Contains(currentSelectedItem))
                {
                    // Lo insertamos al principio para que sea el "líder" de la selección
                    selected.Insert(0, currentSelectedItem);
                }
                return selected;
            }

            // Si no hay multi-selección, devolvemos solo el seleccionado actual
            return currentSelectedItem != null ? new List<FileSystemNodeModel> { currentSelectedItem } : new List<FileSystemNodeModel>();
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
