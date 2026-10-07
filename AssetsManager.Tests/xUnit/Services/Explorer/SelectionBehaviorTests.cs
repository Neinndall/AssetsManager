using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
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
        [Theory]
        [InlineData(ModifierKeys.None, 1, 3, "3", 3)]
        [InlineData(ModifierKeys.Control, 1, 3, "0,3", 3)]
        [InlineData(ModifierKeys.Control, 1, 0, "", 0)]
        [InlineData(ModifierKeys.Shift, 1, 3, "1,2,3", 1)]
        [InlineData(ModifierKeys.Shift, 3, 1, "1,2,3", 3)]
        [InlineData(ModifierKeys.Control | ModifierKeys.Shift, 2, 3, "0,2,3", 2)]
        [InlineData(ModifierKeys.Shift, -1, 2, "2", 2)]
        public void ViewportSelectionSharesToggleRangeAndAnchorRules(
            ModifierKeys modifiers, int anchorIndex, int targetIndex, string expected, int expectedAnchor)
        {
            var items = new[] { new object(), new object(), new object(), new object() };
            var selected = new HashSet<object> { items[0] };
            object anchor = SelectionBehavior.SelectItems(items,
                anchorIndex < 0 ? null : items[anchorIndex], items[targetIndex], modifiers,
                selected.Contains, (item, value) => { if (value) selected.Add(item); else selected.Remove(item); });

            var indexes = new List<int>();
            for (int i = 0; i < items.Length; i++) if (selected.Contains(items[i])) indexes.Add(i);
            Assert.Equal(expected, string.Join(",", indexes));
            Assert.Same(items[expectedAnchor], anchor);
        }

        [Theory]
        [InlineData(ModifierKeys.None, false)]
        [InlineData(ModifierKeys.Control, true)]
        [InlineData(ModifierKeys.Shift, true)]
        public void EmptyViewportClickClearsOnlyWithoutModifiers(ModifierKeys modifiers, bool remainsSelected)
        {
            var item = new object();
            var selected = new HashSet<object> { item };
            object anchor = SelectionBehavior.SelectItems(new[] { item }, item, null, modifiers,
                selected.Contains, (value, state) => { if (state) selected.Add(value); else selected.Remove(value); });
            Assert.Equal(remainsSelected, selected.Contains(item));
            Assert.Equal(remainsSelected ? item : null, anchor);
        }

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
            string gridStyle = File.ReadAllText(Path.Combine(repositoryRoot, "AssetsManager", "Themes", "FileGridStyles.xaml"));
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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GridRightClickSelectsTargetOrPreservesItsExistingGroup(bool targetAlreadySelected)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var states = new[] { new SelectionState(), new SelectionState(), new SelectionState() };
                    var list = new ListBox
                    {
                        ItemsSource = states,
                        SelectionMode = SelectionMode.Extended,
                        Template = new ControlTemplate(typeof(ListBox)) { VisualTree = new FrameworkElementFactory(typeof(ItemsPresenter)) },
                        ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(StackPanel)))
                    };
                    list.ApplyTemplate();
                    list.Measure(new Size(240, 240));
                    list.Arrange(new Rect(0, 0, 240, 240));
                    list.UpdateLayout();
                    var containers = new ListBoxItem[states.Length];
                    for (int i = 0; i < states.Length; i++)
                    {
                        containers[i] = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(i));
                        SelectionBehavior.SetEnableUnifiedSelection(containers[i], true);
                        SelectionBehavior.SetPreserveSelectionOnRightClick(containers[i], true);
                    }
                    containers[0].IsSelected = containers[1].IsSelected = true;
                    states[0].IsMultiSelected = states[1].IsMultiSelected = true;
                    // A stale model flag must not make an unselected container join the group.
                    states[2].IsMultiSelected = true;
                    int target = targetAlreadySelected ? 1 : 2;
                    int primaryActions = 0;
                    SelectionBehavior.AddPrimaryActionHandler(list, (_, _) => primaryActions++);
                    var rightClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                    {
                        RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent
                    };

                    containers[target].RaiseEvent(rightClick);

                    Assert.True(rightClick.Handled);
                    Assert.Equal(0, primaryActions);
                    Assert.Equal(targetAlreadySelected ? 2 : 1, list.SelectedItems.Count);
                    Assert.True(list.SelectedItems.Contains(states[target]));
                    Assert.Equal(targetAlreadySelected, states[0].IsSelected);
                    Assert.Equal(targetAlreadySelected, states[1].IsSelected);
                    Assert.Equal(!targetAlreadySelected, states[2].IsSelected);
                    if (!targetAlreadySelected)
                        Assert.All(states, state => Assert.False(state.IsMultiSelected));
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            Assert.Null(failure);
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        public void TreeRightClickUsesGroupMembershipForExplorerAndComparisonNodes(
            bool comparison, bool targetInGroup, bool targetIsPrimary)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    ISelectableTreeNode[] states;
                    ISelectableTreeNode hidden;
                    ISelectableTreeNode collapsedRoot;
                    if (comparison)
                    {
                        states = new ISelectableTreeNode[]
                        {
                            new SerializableChunkDiff { NewPath = "first.bin" },
                            new SerializableChunkDiff { NewPath = "second.bin" },
                            new SerializableChunkDiff { NewPath = "target.bin" }
                        };
                        var diff = new SerializableChunkDiff { NewPath = "hidden.bin", IsMultiSelected = true };
                        var type = new DiffTypeGroupViewModel { IsMultiSelected = true };
                        type.Diffs.Add(diff);
                        var wad = new WadGroupViewModel { IsMultiSelected = true };
                        wad.Types.Add(type);
                        hidden = diff;
                        collapsedRoot = wad;
                    }
                    else
                    {
                        states = new ISelectableTreeNode[]
                        {
                            new FileSystemNodeModel("first", NodeType.VirtualFile),
                            new FileSystemNodeModel("second", NodeType.VirtualFile),
                            new FileSystemNodeModel("target", NodeType.VirtualFile)
                        };
                        var child = new FileSystemNodeModel("hidden", NodeType.VirtualFile) { IsMultiSelected = true };
                        var root = new FileSystemNodeModel("collapsed", NodeType.VirtualDirectory) { IsMultiSelected = true };
                        root.Children.Add(child);
                        hidden = child;
                        collapsedRoot = root;
                    }
                    var tree = new TreeView
                    {
                        ItemsSource = new[] { states[0], states[1], states[2], collapsedRoot },
                        Template = new ControlTemplate(typeof(TreeView)) { VisualTree = new FrameworkElementFactory(typeof(ItemsPresenter)) },
                        ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(StackPanel)))
                    };
                    tree.ApplyTemplate();
                    tree.Measure(new Size(240, 240));
                    tree.Arrange(new Rect(0, 0, 240, 240));
                    tree.UpdateLayout();
                    var containers = new TreeViewItem[states.Length];
                    for (int i = 0; i < states.Length; i++)
                    {
                        containers[i] = Assert.IsType<TreeViewItem>(tree.ItemContainerGenerator.ContainerFromIndex(i));
                        SelectionBehavior.SetSingleClickExpand(containers[i], true);
                        SelectionBehavior.SetPreserveSelectionOnRightClick(containers[i], true);
                    }
                    states[0].IsMultiSelected = states[1].IsMultiSelected = true;
                    int target = targetInGroup ? 1 : 2;
                    containers[targetIsPrimary ? target : 0].IsSelected = true;
                    int primaryActions = 0;
                    SelectionBehavior.AddPrimaryActionHandler(tree, (_, _) => primaryActions++);
                    var rightClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                    {
                        RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent
                    };

                    containers[target].RaiseEvent(rightClick);

                    Assert.True(rightClick.Handled);
                    Assert.Same(states[target], tree.SelectedItem);
                    Assert.True(states[target].IsSelected);
                    Assert.Equal(targetInGroup, states[0].IsMultiSelected);
                    Assert.Equal(targetInGroup, states[1].IsMultiSelected);
                    Assert.False(states[2].IsMultiSelected);
                    Assert.Equal(targetInGroup, hidden.IsMultiSelected);
                    Assert.Equal(targetInGroup, collapsedRoot.IsMultiSelected);
                    Assert.False(collapsedRoot.IsExpanded);
                    Assert.False(containers[target].IsExpanded);
                    Assert.Equal(0, primaryActions);
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            Assert.Null(failure);
        }

        private sealed class SelectionState : IMultiSelectable
        {
            public bool IsSelected { get; set; }
            public bool IsMultiSelected { get; set; }
        }
    }
}
