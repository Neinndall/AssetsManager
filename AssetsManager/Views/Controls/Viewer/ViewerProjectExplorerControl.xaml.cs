using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.WindowsAPICodePack.Dialogs;
using Material.Icons;

namespace AssetsManager.Views.Controls.Viewer
{
    public class ProjectExplorerNode
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public bool IsFile { get; set; }
        public MaterialIconKind IconKind { get; set; }
        public Brush IconColor { get; set; }
        public bool IsExpanded { get; set; } = true;
        public List<ProjectExplorerNode> Children { get; } = new List<ProjectExplorerNode>();
    }

    public partial class ViewerProjectExplorerControl : UserControl
    {
        public event EventHandler<string> ModelSelected;
        public event EventHandler CloseRequested;

        private string _currentRootFolder;
        private readonly List<ProjectExplorerNode> _allNodes = new List<ProjectExplorerNode>();
        private readonly List<ProjectExplorerNode> _folderOnlyNodes = new List<ProjectExplorerNode>();
        
        private readonly SolidColorBrush _folderBrush = new SolidColorBrush(Color.FromRgb(255, 179, 0)); // Accent Orange/Gold
        private readonly SolidColorBrush _modelBrush = new SolidColorBrush(Color.FromRgb(3, 169, 244));  // Accent DodgerBlue
        private readonly SolidColorBrush _mapBrush = new SolidColorBrush(Color.FromRgb(76, 175, 80));    // Accent Green
        private readonly SolidColorBrush _animBrush = new SolidColorBrush(Color.FromRgb(0, 230, 118));   // Accent LightGreen
        private readonly SolidColorBrush _imageBrush = new SolidColorBrush(Color.FromRgb(156, 39, 176)); // Accent Purple
        private readonly SolidColorBrush _skeletonBrush = new SolidColorBrush(Color.FromRgb(233, 30, 99)); // Accent Pink

        public string CurrentRootFolder => _currentRootFolder;

        public ViewerProjectExplorerControl()
        {
            InitializeComponent();
        }

        public void LoadProjectFolder(string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath)) return;

            _currentRootFolder = rootPath;

            ScanDirectory(rootPath);
            ApplyFilter(string.Empty);

            if (_folderOnlyNodes.Count > 0)
            {
                NavigateToFolder(_allNodes[0]);
            }
        }

        private void ScanDirectory(string rootPath)
        {
            _allNodes.Clear();
            _folderOnlyNodes.Clear();

            var rootNode = new ProjectExplorerNode
            {
                Name = Path.GetFileName(rootPath),
                FullPath = rootPath,
                IsFile = false,
                IconKind = MaterialIconKind.FolderHomeOutline,
                IconColor = _folderBrush
            };

            BuildTree(rootPath, rootNode);
            
            if (rootNode.Children.Count > 0 || Directory.GetFiles(rootPath).Length > 0)
            {
                _allNodes.Add(rootNode);
                
                // Create a duplicate tree for TreeView displaying folders only
                var foldersOnlyRoot = CopyFolderStructureOnly(rootNode);
                if (foldersOnlyRoot != null)
                {
                    _folderOnlyNodes.Add(foldersOnlyRoot);
                }
            }
        }

        private ProjectExplorerNode CopyFolderStructureOnly(ProjectExplorerNode node)
        {
            if (node.IsFile) return null;

            var folderCopy = new ProjectExplorerNode
            {
                Name = node.Name,
                FullPath = node.FullPath,
                IsFile = false,
                IconKind = node.IconKind,
                IconColor = node.IconColor,
                IsExpanded = node.IsExpanded
            };

            foreach (var child in node.Children)
            {
                var childFolder = CopyFolderStructureOnly(child);
                if (childFolder != null)
                {
                    folderCopy.Children.Add(childFolder);
                }
            }

            return folderCopy;
        }

        private void CollapseSingleChildFolders(ProjectExplorerNode node)
        {
            while (node.Children.Count == 1 && !node.Children[0].IsFile)
            {
                var singleChild = node.Children[0];
                node.Name = node.Name + "/" + singleChild.Name;
                node.Children.Clear();
                node.Children.AddRange(singleChild.Children);
            }

            foreach (var child in node.Children)
            {
                CollapseSingleChildFolders(child);
            }
        }

        private bool BuildTree(string currentDir, ProjectExplorerNode parentNode)
        {
            bool hasValidAssets = false;

            try
            {
                // Scan directories
                var subDirs = Directory.GetDirectories(currentDir);
                foreach (var dir in subDirs.OrderBy(d => Path.GetFileName(d)))
                {
                    var dirNode = new ProjectExplorerNode
                    {
                        Name = Path.GetFileName(dir),
                        FullPath = dir,
                        IsFile = false,
                        IconKind = MaterialIconKind.FolderOutline,
                        IconColor = _folderBrush
                    };

                    bool subDirHasAssets = BuildTree(dir, dirNode);
                    if (subDirHasAssets || Directory.GetFiles(dir).Length > 0)
                    {
                        parentNode.Children.Add(dirNode);
                        hasValidAssets = true;
                    }
                }

                // Scan files (.skn, .sco, .mapgeo, .skl, .anm, and image extensions)
                var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".skn", ".sco", ".mapgeo", ".skl", ".anm", ".dds", ".tex", ".png", ".jpg", ".tga"
                };

                var files = Directory.GetFiles(currentDir)
                    .Where(f => allowedExtensions.Contains(Path.GetExtension(f)));

                foreach (var file in files.OrderBy(f => Path.GetFileName(f)))
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    MaterialIconKind icon = AssetsManager.Views.Converters.PathToIconConverter.GetExtensionIcon(ext);
                    Brush color = Brushes.White;

                    if (ext == ".skn" || ext == ".sco")
                    {
                        color = _modelBrush;
                    }
                    else if (ext == ".mapgeo")
                    {
                        color = _mapBrush;
                    }
                    else if (ext == ".skl")
                    {
                        color = _skeletonBrush;
                    }
                    else if (ext == ".anm")
                    {
                        color = _animBrush;
                    }
                    else if (ext == ".dds" || ext == ".tex" || ext == ".png" || ext == ".jpg" || ext == ".tga")
                    {
                        color = _imageBrush;
                    }

                    var fileNode = new ProjectExplorerNode
                    {
                        Name = Path.GetFileName(file),
                        FullPath = file,
                        IsFile = true,
                        IconKind = icon,
                        IconColor = color
                    };

                    parentNode.Children.Add(fileNode);
                    hasValidAssets = true;
                }
            }
            catch (Exception)
            {
                // Ignore access errors
            }

            return hasValidAssets;
        }

        private void ApplyFilter(string filter)
        {
            if (string.IsNullOrEmpty(filter))
            {
                FoldersTreeView.ItemsSource = _folderOnlyNodes;
                if (_allNodes.Count > 0) NavigateToFolder(_allNodes[0]);
                return;
            }

            var filtered = new List<ProjectExplorerNode>();
            foreach (var node in _folderOnlyNodes)
            {
                var copy = FilterFolderNodeRecursive(node, filter.ToLowerInvariant());
                if (copy != null)
                {
                    filtered.Add(copy);
                }
            }
            FoldersTreeView.ItemsSource = filtered;

            // Also search flat files in the right side ListBox if filter is active
            var flatSearchResults = new List<ProjectExplorerNode>();
            FindFlatMatchingFiles(_allNodes[0], filter.ToLowerInvariant(), flatSearchResults);
            FilesListBox.ItemsSource = flatSearchResults;
        }

        private void FindFlatMatchingFiles(ProjectExplorerNode node, string filter, List<ProjectExplorerNode> results)
        {
            if (node.IsFile)
            {
                if (node.Name.ToLowerInvariant().Contains(filter))
                {
                    results.Add(node);
                }
                return;
            }

            foreach (var child in node.Children)
            {
                FindFlatMatchingFiles(child, filter, results);
            }
        }

        private ProjectExplorerNode FilterFolderNodeRecursive(ProjectExplorerNode node, string filter)
        {
            var matchingChildren = new List<ProjectExplorerNode>();
            foreach (var child in node.Children)
            {
                var matchedChild = FilterFolderNodeRecursive(child, filter);
                if (matchedChild != null)
                {
                    matchingChildren.Add(matchedChild);
                }
            }

            if (matchingChildren.Count > 0 || node.Name.ToLowerInvariant().Contains(filter))
            {
                var copy = new ProjectExplorerNode
                {
                    Name = node.Name,
                    FullPath = node.FullPath,
                    IsFile = node.IsFile,
                    IconKind = node.IconKind,
                    IconColor = node.IconColor,
                    IsExpanded = true
                };
                copy.Children.AddRange(matchingChildren);
                return copy;
            }

            return null;
        }

        private void SearchBox_SearchTextChanged(object sender, RoutedEventArgs e)
        {
            ApplyFilter(SearchBox.Text);
        }

        private void FoldersTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is ProjectExplorerNode node)
            {
                // Find original node with files in _allNodes
                var originalNode = FindNodeByPath(_allNodes[0], node.FullPath);
                if (originalNode != null)
                {
                    NavigateToFolder(originalNode);
                }
            }
        }

        private ProjectExplorerNode FindNodeByPath(ProjectExplorerNode root, string path)
        {
            if (string.Equals(root.FullPath, path, StringComparison.OrdinalIgnoreCase)) return root;

            foreach (var child in root.Children)
            {
                var found = FindNodeByPath(child, path);
                if (found != null) return found;
            }

            return null;
        }

        private ProjectExplorerNode FindParentNode(ProjectExplorerNode root, ProjectExplorerNode target)
        {
            foreach (var child in root.Children)
            {
                if (ReferenceEquals(child, target)) return root;
                var parent = FindParentNode(child, target);
                if (parent != null) return parent;
            }

            return null;
        }

        private void NavigateToFolder(ProjectExplorerNode folderNode)
        {
            if (folderNode == null || folderNode.IsFile) return;

            FilesListBox.ItemsSource = folderNode.Children;
            UpdateBreadcrumbs(folderNode);
        }

        private void UpdateBreadcrumbs(ProjectExplorerNode folderNode)
        {
            BreadcrumbsContainer.Children.Clear();
            if (folderNode == null) return;

            var path = new List<ProjectExplorerNode>();
            var current = folderNode;
            while (current != null)
            {
                path.Insert(0, current);
                current = FindParentNode(_allNodes[0], current);
            }

            for (int i = 0; i < path.Count; i++)
            {
                var node = path[i];
                var cleanName = node.Name.Contains("/") ? node.Name.Split('/').Last() : node.Name;
                
                var btn = new Button
                {
                    Content = cleanName.ToUpperInvariant(),
                    Style = (Style)FindResource("SmallTextButtonStyle"),
                    Tag = node,
                    Padding = new Thickness(4, 2, 4, 2),
                    Margin = new Thickness(0),
                    FontSize = 9.5,
                    FontWeight = FontWeights.Bold
                };
                btn.Click += Breadcrumb_Click;
                BreadcrumbsContainer.Children.Add(btn);

                if (i < path.Count - 1)
                {
                    var separator = new TextBlock
                    {
                        Text = " › ",
                        Foreground = (Brush)FindResource("TextMuted"),
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 10,
                        FontWeight = FontWeights.Bold
                    };
                    BreadcrumbsContainer.Children.Add(separator);
                }
            }
        }

        private void Breadcrumb_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ProjectExplorerNode node)
            {
                NavigateToFolder(node);
            }
        }

        private void FilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FilesListBox.SelectedItem is ProjectExplorerNode node && node.IsFile)
            {
                // Single click on file triggers ModelSelected (useful for image previews)
                ModelSelected?.Invoke(this, node.FullPath);
            }
        }

        private void FilesListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FilesListBox.SelectedItem is ProjectExplorerNode node)
            {
                if (!node.IsFile)
                {
                    NavigateToFolder(node);
                }
                else
                {
                    ModelSelected?.Invoke(this, node.FullPath);
                }
            }
        }

        private void CloseExplorer_Click(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ChangeFolder_Click(object sender, RoutedEventArgs e)
        {
            var folderBrowser = new CommonOpenFileDialog { IsFolderPicker = true, Title = "Select extracted WAD root folder" };
            if (folderBrowser.ShowDialog() == CommonFileDialogResult.Ok)
            {
                LoadProjectFolder(folderBrowser.FileName);
            }
        }
    }
}
