using System;
using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using AssetsManager.Views.Controls.Viewer;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Dialogs.Controls;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Wad;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Explorer
{
    public class SelectionBehaviorTests
    {
        [Fact]
        public void PrimaryAction_SelectsLeafTreeItem()
        {
            Exception failure = null;
            bool isSelected = false;
            bool isModelSelected = false;

            var thread = new Thread(() =>
            {
                try
                {
                    var state = new SelectionState();
                    var item = new TreeViewItem { DataContext = state };
                    SelectionBehavior.SetSingleClickExpand(item, true);

                    SelectionBehavior.ApplyPrimaryTreeAction(item);

                    isSelected = item.IsSelected;
                    isModelSelected = state.IsSelected;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            Assert.Null(failure);
            Assert.True(isSelected);
            Assert.True(isModelSelected);
        }

        [Fact]
        public void VirtualizedSelection_IsNotOwnedByContainerBindings()
        {
            string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string treeStyle = File.ReadAllText(Path.Combine(repositoryRoot, "AssetsManager", "Themes", "TreeViewStyles.xaml"));
            string gridStyle = File.ReadAllText(Path.Combine(repositoryRoot, "AssetsManager", "Themes", "GridView.xaml"));
            string resultsTree = File.ReadAllText(Path.Combine(repositoryRoot, "AssetsManager", "Views", "Dialogs", "Controls", "WadResultsTreeControl.xaml"));

            Assert.DoesNotContain("Property=\"IsSelected\" Value=\"{Binding IsSelected", treeStyle);
            Assert.DoesNotContain("Property=\"IsSelected\" Value=\"{Binding IsSelected", gridStyle);
            Assert.DoesNotContain("Property=\"IsSelected\" Value=\"{Binding IsSelected", resultsTree);
            Assert.Contains("Property=\"IsExpanded\" Value=\"{Binding IsExpanded, Mode=TwoWay}\"", treeStyle);
        }

        [Fact]
        public void EveryTreeViewUsesSharedSelectionStyle()
        {
            string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string viewsRoot = Path.Combine(repositoryRoot, "AssetsManager", "Views");

            foreach (string file in Directory.EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories))
            {
                string xaml = File.ReadAllText(file);
                foreach (Match tree in Regex.Matches(xaml, @"<TreeView(?!\.)\b[^>]*>", RegexOptions.Singleline))
                {
                    Assert.Contains("Style=\"{StaticResource ModernTreeView}\"", tree.Value);
                }
            }
        }

        [Fact]
        public void ViewerBrowserUsesExtendedFileSelection()
        {
            string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string browser = File.ReadAllText(Path.Combine(
                repositoryRoot,
                "AssetsManager",
                "Views",
                "Controls",
                "Viewer",
                "ViewerProjectExplorerControl.xaml"));
            string gridStyle = File.ReadAllText(Path.Combine(repositoryRoot, "AssetsManager", "Themes", "GridView.xaml"));

            Assert.Matches(
                @"<ListBox[^>]*x:Name=""FilesListBox""[^>]*Style=""\{StaticResource ModernFileGridListBox\}""",
                browser.ReplaceLineEndings(" "));
            Assert.Matches(
                @"<Style x:Key=""ModernFileGridListBox""[^>]*>.*?<Setter Property=""SelectionMode"" Value=""Extended""",
                gridStyle.ReplaceLineEndings(" "));
            Assert.Contains("Header=\"Load Animations\"", browser);
            Assert.Contains("PreviewMouseRightButtonDown=\"FilesListBox_PreviewMouseRightButtonDown\"", browser);
        }

        [Theory]
        [InlineData(ModifierKeys.None, true)]
        [InlineData(ModifierKeys.Control, false)]
        [InlineData(ModifierKeys.Shift, false)]
        [InlineData(ModifierKeys.Control | ModifierKeys.Shift, false)]
        public void PrimaryActionRequiresNoSelectionModifier(
            ModifierKeys modifiers,
            bool expected) =>
            Assert.Equal(expected, SelectionBehavior.IsPrimaryActionIntent(modifiers));

        [Theory]
        [InlineData(ModifierKeys.None, false)]
        [InlineData(ModifierKeys.Control, false)]
        [InlineData(ModifierKeys.Shift, true)]
        [InlineData(ModifierKeys.Control | ModifierKeys.Shift, true)]
        public void RangeIntentTracksShift(
            ModifierKeys modifiers,
            bool expected) =>
            Assert.Equal(expected, SelectionBehavior.IsRangeSelectIntent(modifiers));

        [Fact]
        public void TreeRangeUsesExpandedVisibleOrder()
        {
            var root = new FileSystemNodeModel("root", NodeType.VirtualDirectory) { IsExpanded = true };
            var first = new FileSystemNodeModel("first", NodeType.VirtualFile);
            var hidden = new FileSystemNodeModel("hidden", NodeType.VirtualFile) { IsVisible = false };
            var second = new FileSystemNodeModel("second", NodeType.VirtualFile);
            var target = new FileSystemNodeModel("target", NodeType.VirtualFile);
            root.Children.Add(first);
            root.Children.Add(hidden);
            root.Children.Add(second);

            Assert.True(SelectionBehavior.SelectTreeRange(
                new ArrayList { root, target },
                first,
                target,
                additive: false,
                out bool usedAnchor));

            Assert.True(usedAnchor);
            Assert.False(root.IsMultiSelected);
            Assert.True(first.IsMultiSelected);
            Assert.False(hidden.IsMultiSelected);
            Assert.True(second.IsMultiSelected);
            Assert.True(target.IsMultiSelected);
        }

        [Fact]
        public void TreeRangeDoesNotSelectCollapsedDescendants()
        {
            var root = new FileSystemNodeModel("root", NodeType.VirtualDirectory);
            var collapsedChild = new FileSystemNodeModel("child", NodeType.VirtualFile);
            var target = new FileSystemNodeModel("target", NodeType.VirtualFile);
            root.Children.Add(collapsedChild);

            Assert.True(SelectionBehavior.SelectTreeRange(
                new ArrayList { root, target },
                root,
                target,
                additive: false,
                out bool usedAnchor));

            Assert.True(usedAnchor);
            Assert.True(root.IsMultiSelected);
            Assert.False(collapsedChild.IsMultiSelected);
            Assert.True(target.IsMultiSelected);
        }

        [Fact]
        public void AdditiveTreeRangePreservesPreviousSelection()
        {
            var previous = new FileSystemNodeModel("previous", NodeType.VirtualFile) { IsMultiSelected = true };
            var anchor = new FileSystemNodeModel("anchor", NodeType.VirtualFile);
            var target = new FileSystemNodeModel("target", NodeType.VirtualFile);

            Assert.True(SelectionBehavior.SelectTreeRange(
                new ArrayList { previous, anchor, target },
                anchor,
                target,
                additive: true,
                out bool usedAnchor));

            Assert.True(usedAnchor);
            Assert.True(previous.IsMultiSelected);
            Assert.True(anchor.IsMultiSelected);
            Assert.True(target.IsMultiSelected);
        }

        [Fact]
        public void ComparisonTreeRangeUsesSharedVisibleOrder()
        {
            var first = new SerializableChunkDiff { NewPath = "first.bin" };
            var middle = new SerializableChunkDiff { NewPath = "middle.bin" };
            var target = new SerializableChunkDiff { NewPath = "target.bin" };
            var type = new DiffTypeGroupViewModel { IsExpanded = true };
            type.Diffs.Add(first);
            type.Diffs.Add(middle);
            type.Diffs.Add(target);
            var wad = new WadGroupViewModel { IsExpanded = true };
            wad.Types.Add(type);

            Assert.True(SelectionBehavior.SelectTreeRange(
                new ArrayList { wad },
                first,
                target,
                additive: false,
                out bool usedAnchor));

            Assert.True(usedAnchor);
            Assert.False(wad.IsMultiSelected);
            Assert.False(type.IsMultiSelected);
            Assert.True(first.IsMultiSelected);
            Assert.True(middle.IsMultiSelected);
            Assert.True(target.IsMultiSelected);
        }

        [Fact]
        public void ViewerTreeRangeUsesSharedVisibleOrder()
        {
            var first = new ProjectExplorerNode { Name = "first" };
            var middle = new ProjectExplorerNode { Name = "middle" };
            var target = new ProjectExplorerNode { Name = "target" };
            var root = new ProjectExplorerNode { Name = "root", IsExpanded = true };
            root.Children.Add(first);
            root.Children.Add(middle);
            root.Children.Add(target);

            Assert.True(SelectionBehavior.SelectTreeRange(
                new ArrayList { root },
                first,
                target,
                additive: false,
                out bool usedAnchor));

            Assert.True(usedAnchor);
            Assert.False(root.IsMultiSelected);
            Assert.True(first.IsMultiSelected);
            Assert.True(middle.IsMultiSelected);
            Assert.True(target.IsMultiSelected);
        }

        private sealed class SelectionState : IMultiSelectable
        {
            public bool IsSelected { get; set; }
            public bool IsMultiSelected { get; set; }
        }
    }
}
