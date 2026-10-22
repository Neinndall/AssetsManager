using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Explorer.Tree;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Helpers;
using Serilog;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Explorer;

public sealed class ExplorerSearchNavigationTests
{
    [Fact]
    public void ClearingSearchDoesNotInitializeUnloadedFolderChildren() => Run(async () =>
    {
        var folder = new FileSystemNodeModel("unloaded", NodeType.VirtualDirectory);
        var roots = new ObservableRangeCollection<FileSystemNodeModel> { folder };
        var search = new WadSearchBoxService(null, new LogService(Log.Logger));
        try
        {
            Assert.Null(folder.LoadedChildren);
            await search.PerformSearchAsync("", roots);
            Assert.Null(folder.LoadedChildren);
        }
        finally { search.InvalidateIndex(roots); }
    });

    [Theory]
    [InlineData(1, "")]
    [InlineData(6, "")]
    [InlineData(6, "   ")]
    public void ClearingFilterRevealsTheSelectedFileInTheCompleteVirtualizedTree(int depth, string clearedQuery) => Run(async () =>
    {
        using var fixture = new TreeFixture(depth, 160);
        var search = new WadSearchBoxService(null, new LogService(Log.Logger));
        var navigation = new TreeUIManager();
        try
        {
            Assert.Null(await search.PerformSearchAsync("needle", fixture.Roots));
            Assert.True(await navigation.SelectAndFocusNodeAsync(fixture.Tree, fixture.Roots, fixture.Target, false));
            Assert.Same(fixture.Target, fixture.Tree.SelectedItem);
            var selected = fixture.Tree.SelectedItem as FileSystemNodeModel;
            var focus = Keyboard.FocusedElement;

            var restored = await search.PerformSearchAsync(clearedQuery, fixture.Roots, selected);
            Assert.Same(fixture.Target, restored);
            Assert.True(await navigation.SelectAndFocusNodeAsync(fixture.Tree, fixture.Roots, restored, false));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            fixture.Tree.UpdateLayout();

            Assert.Same(fixture.Target, fixture.Tree.SelectedItem);
            Assert.True(fixture.Target.IsSelected);
            Assert.Same(focus, Keyboard.FocusedElement);
            foreach (var ancestor in fixture.Path.Take(fixture.Path.Count - 1))
            {
                Assert.True(ancestor.IsExpanded);
                Assert.Same(ancestor.LoadedChildren, ancestor.VisibleChildren);
            }
            Assert.False(fixture.Roots[0].IsExpanded);
            AssertInViewport(fixture.Tree, fixture.Path);
        }
        finally { search.InvalidateIndex(fixture.Roots); }
    });

    [Fact]
    public void DeferredRealizationRetriesTheCorrectItemsHost() => Run(async () =>
    {
        using var fixture = new TreeFixture(0, 200, deferRoot: true);
        var panel = FindDeferredPanel(fixture.Tree);
        Assert.NotNull(panel);
        // Reproduce the previous one-attempt/25ms path before exercising the replacement.
        panel.BringIndexIntoViewPublic(fixture.Roots.IndexOf(fixture.Target));
        await Task.Delay(25);
        Assert.Null(fixture.Tree.ItemContainerGenerator.ContainerFromItem(fixture.Target));
        DeferredPanel.IgnoredRequests = 0;
        var navigation = new TreeUIManager();
        Assert.True(await navigation.SelectAndFocusNodeAsync(fixture.Tree, fixture.Roots, fixture.Target, false));
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.Same(fixture.Target, fixture.Tree.SelectedItem);
        AssertInViewport(fixture.Tree, fixture.Path);
        Assert.True(DeferredPanel.IgnoredRequests > 0, "The test must exercise delayed realization.");
    });

    [Fact]
    public void NewNavigationSupersedesAnOlderQueuedSelection() => Run(async () =>
    {
        using var fixture = new TreeFixture(4, 80);
        var navigation = new TreeUIManager();
        var old = navigation.SelectAndFocusNodeAsync(fixture.Tree, fixture.Roots, fixture.Target, false);
        var current = navigation.SelectAndFocusNodeAsync(fixture.Tree, fixture.Roots, fixture.Roots[0], false);
        Assert.False(await old);
        Assert.True(await current);
        Assert.Same(fixture.Roots[0], fixture.Tree.SelectedItem);
        Assert.False(fixture.Target.IsSelected);
    });

    [Fact]
    public void SelectionChangedDuringRestorationIsNotOverwritten() => Run(async () =>
    {
        using var fixture = new TreeFixture(2, 40);
        var navigation = new TreeUIManager();
        fixture.Tree.SelectedItemChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.NewValue, fixture.Target)) return;
            navigation.CancelNavigation(fixture.Tree);
            var ancestor = Assert.IsType<TreeViewItem>(fixture.Tree.ItemContainerGenerator.ContainerFromItem(fixture.Path[0]));
            ancestor.IsSelected = true;
        };
        Assert.False(await navigation.SelectAndFocusNodeAsync(fixture.Tree, fixture.Roots, fixture.Target, false));
        Assert.Same(fixture.Path[0], fixture.Tree.SelectedItem);
        Assert.False(fixture.Target.IsSelected);
    });

    [Fact]
    public void CancelledNavigationCannotRestoreAnOldSelection() => Run(async () =>
    {
        using var fixture = new TreeFixture(3, 80);
        var navigation = new TreeUIManager();
        var pending = navigation.SelectAndFocusNodeAsync(fixture.Tree, fixture.Roots, fixture.Target, false);
        navigation.CancelNavigation(fixture.Tree);
        Assert.False(await pending);
        Assert.Null(fixture.Tree.SelectedItem);
        Assert.False(await navigation.SelectAndFocusNodeAsync(fixture.Tree, fixture.Roots, fixture.Target, false, new CancellationToken(true)));
    });

    [Fact]
    public void IndependentTreesDoNotCancelEachOthersNavigation() => Run(async () =>
    {
        using var first = new TreeFixture(2, 30);
        using var second = new TreeFixture(2, 30);
        var navigation = new TreeUIManager();
        var a = navigation.SelectAndFocusNodeAsync(first.Tree, first.Roots, first.Target, false);
        var b = navigation.SelectAndFocusNodeAsync(second.Tree, second.Roots, second.Target, false);
        Assert.True(await a);
        Assert.True(await b);
    });

    [Fact]
    public void DetachedNodesAndParentCyclesCannotNavigateIntoAnotherTree()
    {
        var navigation = new TreeUIManager();
        var root = new FileSystemNodeModel("root", NodeType.VirtualDirectory);
        var child = new FileSystemNodeModel("child", NodeType.VirtualFile) { Parent = root };
        Assert.Null(navigation.FindNodePath(new[] { child }, child));
        Assert.Equal(new[] { root, child }, navigation.FindNodePath(new[] { root }, child));
        root.Parent = child;
        Assert.Null(navigation.FindNodePath(new[] { root }, child));
    }

    [Fact]
    public void CancellingAFilterAllowsTheLatestQueryToWin() => Run(async () =>
    {
        using var fixture = new TreeFixture(2, 150);
        var search = new WadSearchBoxService(null, new LogService(Log.Logger));
        try
        {
            search.RebuildIndex(fixture.Roots);
            var old = search.PerformSearchAsync("needle", fixture.Roots);
            search.CancelSearch(fixture.Roots);
            var current = search.PerformSearchAsync("no-such-file", fixture.Roots);
            await Task.WhenAll(old, current);
            Assert.All(fixture.Roots, root => Assert.False(root.IsVisible));
            Assert.Null(await search.PerformSearchAsync("", fixture.Roots));
            Assert.All(fixture.Roots, root => Assert.True(root.IsVisible));
        }
        finally { search.InvalidateIndex(fixture.Roots); }
    });

    private static void AssertInViewport(TreeView tree, IReadOnlyList<FileSystemNodeModel> path)
    {
        ItemsControl owner = tree;
        foreach (var node in path)
        {
            var item = Assert.IsType<TreeViewItem>(owner.ItemContainerGenerator.ContainerFromItem(node));
            owner = item;
        }
        var bounds = owner.TransformToAncestor(tree).TransformBounds(new Rect(owner.RenderSize));
        Assert.True(bounds.Height > 0 && bounds.Bottom > 0 && bounds.Top < tree.ActualHeight,
            $"Selected row is outside the viewport: {bounds}, tree height {tree.ActualHeight}.");
    }

    private static DeferredPanel FindDeferredPanel(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is DeferredPanel panel) return panel;
            var found = FindDeferredPanel(child);
            if (found != null) return found;
        }
        return null;
    }

    private static void Run(Func<Task> action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); }
                catch (Exception ex) { failure = ex; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "WPF navigation timed out.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class TreeFixture : IDisposable
    {
        private readonly Window _window;
        internal TreeView Tree { get; }
        internal ObservableRangeCollection<FileSystemNodeModel> Roots { get; } = new();
        internal List<FileSystemNodeModel> Path { get; } = new();
        internal FileSystemNodeModel Target { get; }

        internal TreeFixture(int depth, int siblings, bool deferRoot = false)
        {
            for (int i = 0; i < siblings; i++) Roots.Add(new FileSystemNodeModel($"root{i:D4}", NodeType.VirtualDirectory));
            FileSystemNodeModel parent = null;
            for (int level = 0; level <= depth; level++)
            {
                var node = new FileSystemNodeModel(level == depth ? "needle.bin" : $"folder{level}",
                    level == depth ? NodeType.VirtualFile : NodeType.VirtualDirectory) { Parent = parent };
                if (parent == null) Roots.Add(node);
                else
                {
                    for (int i = 0; i < siblings; i++) parent.Children.Add(new FileSystemNodeModel($"sibling{i:D4}.bin", NodeType.VirtualFile) { Parent = parent });
                    parent.Children.Add(node);
                }
                Path.Add(node);
                parent = node;
            }
            Target = parent;
            var template = new HierarchicalDataTemplate(typeof(FileSystemNodeModel)) { ItemsSource = new Binding("VisibleChildren") };
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            template.VisualTree = text;
            var style = new Style(typeof(TreeViewItem));
            style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, new Binding("IsExpanded") { Mode = BindingMode.TwoWay }));
            style.Setters.Add(new Setter(SelectionBehavior.SingleClickExpandProperty, true));
            style.Setters.Add(new Setter(UIElement.VisibilityProperty, new Binding("IsVisible") { Converter = new BooleanToVisibilityConverter() }));
            style.Setters.Add(new Setter(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)))));
            Tree = new TreeView { ItemsSource = Roots, ItemTemplate = template };
            Tree.Resources.Add(typeof(TreeViewItem), style);
            VirtualizingPanel.SetIsVirtualizing(Tree, true);
            VirtualizingPanel.SetVirtualizationMode(Tree, VirtualizationMode.Recycling);
            VirtualizingPanel.SetCacheLength(Tree, new VirtualizationCacheLength(30));
            VirtualizingPanel.SetCacheLengthUnit(Tree, VirtualizationCacheLengthUnit.Item);
            ScrollViewer.SetCanContentScroll(Tree, true);
            if (deferRoot)
            {
                DeferredPanel.IgnoredRequests = 0;
                Tree.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(DeferredPanel)));
            }
            _window = new Window { Width = 430, Height = 310, Content = Tree, ShowActivated = false,
                ShowInTaskbar = false, Opacity = 0, WindowStyle = WindowStyle.None };
            _window.Show();
            _window.UpdateLayout();
        }

        public void Dispose() => _window.Close();
    }

    public sealed class DeferredPanel : VirtualizingStackPanel
    {
        [ThreadStatic] internal static int IgnoredRequests;
        protected override void BringIndexIntoView(int index)
        {
            if (index >= 200 && IgnoredRequests++ == 0) return;
            base.BringIndexIntoView(index);
        }
    }
}
