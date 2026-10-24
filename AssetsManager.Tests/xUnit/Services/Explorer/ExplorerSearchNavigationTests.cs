using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
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
    public void FilteringAllowsInputToCancelBeforeTheRemainingRowsAreUpdated() => Run(async () =>
    {
        var roots = new ObservableRangeCollection<FileSystemNodeModel>(Enumerable.Range(0, 500)
            .Select(i => new FileSystemNodeModel($"file{i}.bin", NodeType.VirtualFile)));
        var search = new WadSearchBoxService(null, new LogService(Log.Logger));
        bool inputHandled = false;
        roots[0].PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(FileSystemNodeModel.IsVisible) || roots[0].IsVisible) return;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                inputHandled = true;
                search.CancelSearch(roots);
            }));
            // Force one UI batch over its budget without depending on machine speed.
            Thread.Sleep(20);
        };
        try
        {
            search.RebuildIndex(roots);
            await search.PerformSearchAsync("missing-file", roots);
            Assert.True(inputHandled);
            Assert.False(roots[0].IsVisible);
            Assert.True(roots[^1].IsVisible);
            await search.PerformSearchAsync("file", roots);
            Assert.All(roots, node => Assert.True(node.IsVisible));
        }
        finally { search.InvalidateIndex(roots); }
    });

    [Fact]
    public void NavigationScrollsTheTargetBeforeCommittingSelection() => Run(async () =>
    {
        using var fixture = new TreeFixture(0, 0, productionStyle: true);
        bool? selectedOnFirstScroll = null;
        fixture.Tree.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, e) =>
        {
            if (e.TargetObject is TreeViewItem item && ReferenceEquals(item.DataContext, fixture.Target))
                selectedOnFirstScroll ??= item.IsSelected;
        }), handledEventsToo: true);
        var navigation = new TreeUIManager();
        Assert.True(await navigation.SelectAndFocusNodeAsync(fixture.Tree, fixture.Roots, fixture.Target, false));
        Assert.Equal(false, selectedOnFirstScroll);
        Assert.Same(fixture.Target, fixture.Tree.SelectedItem);
        Assert.True(fixture.Target.IsSelected);
        AssertInViewport(fixture.Tree, fixture.Path);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void ClearingClickedSkinsJsonWithProductionStylesPreservesSelection(int depth) => Run(async () =>
    {
        using var fixture = new TreeFixture(depth, 160, productionStyle: true);
        fixture.Target.Name = "skins.json";
        var search = new WadSearchBoxService(null, new LogService(Log.Logger));
        var navigation = new TreeUIManager();
        try
        {
            await search.PerformSearchAsync("skins.json", fixture.Roots);
            ItemsControl owner = fixture.Tree;
            foreach (var node in fixture.Path)
            {
                owner.UpdateLayout();
                var item = Assert.IsType<TreeViewItem>(owner.ItemContainerGenerator.ContainerFromItem(node));
                SelectionBehavior.ApplyPrimaryTreeAction(item);
                owner = item;
            }
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.Same(fixture.Target, fixture.Tree.SelectedItem);
            var selected = fixture.Tree.SelectedItem as FileSystemNodeModel;
            var restored = await search.PerformSearchAsync("", fixture.Roots, selected);
            Assert.True(await navigation.SelectAndFocusNodeAsync(fixture.Tree, fixture.Roots, restored, false));
            await Task.Delay(150);
            fixture.Tree.UpdateLayout();
            Assert.Same(fixture.Target, fixture.Tree.SelectedItem);
            Assert.True(fixture.Target.IsSelected);
            AssertInViewport(fixture.Tree, fixture.Path);
        }
        finally { search.InvalidateIndex(fixture.Roots); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReturningToSkinsJsonAfterOtherFiltersRestoresTheClickedFile(bool clearBetweenQueries) => Run(async () =>
    {
        using var fixture = new TreeFixture(6, 160, productionStyle: true);
        fixture.Target.Name = "skins.json";
        var other = fixture.Target.Parent.Children[5];
        var search = new WadSearchBoxService(null, new LogService(Log.Logger));
        var navigation = new TreeUIManager();
        try
        {
            foreach (var target in new[] { fixture.Target, other, fixture.Target, other, fixture.Target })
            {
                await search.PerformSearchAsync(target.Name, fixture.Roots, fixture.Tree.SelectedItem as FileSystemNodeModel);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                var path = navigation.FindNodePath(fixture.Roots, target);
                ItemsControl owner = fixture.Tree;
                foreach (var node in path)
                {
                    owner.UpdateLayout();
                    var item = Assert.IsType<TreeViewItem>(owner.ItemContainerGenerator.ContainerFromItem(node));
                    if (ReferenceEquals(node, target)) SelectionBehavior.ApplyPrimaryTreeAction(item);
                    else item.IsExpanded = true;
                    owner = item;
                }
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Assert.Same(target, fixture.Tree.SelectedItem);
                if (!clearBetweenQueries && !ReferenceEquals(target, fixture.Target)) continue;
                var restored = await search.PerformSearchAsync("", fixture.Roots, fixture.Tree.SelectedItem as FileSystemNodeModel);
                Assert.Same(target, restored);
                var selectionChanges = new List<bool>();
                System.ComponentModel.PropertyChangedEventHandler selectionChanged = (_, e) =>
                {
                    if (e.PropertyName == nameof(FileSystemNodeModel.IsSelected)) selectionChanges.Add(target.IsSelected);
                };
                target.PropertyChanged += selectionChanged;
                bool navigated = await navigation.SelectAndFocusNodeAsync(fixture.Tree, fixture.Roots, restored, false);
                target.PropertyChanged -= selectionChanged;
                Assert.All(selectionChanges.SkipWhile(selected => !selected), selected => Assert.True(selected,
                    $"Selection was cleared after being restored: {string.Join(",", selectionChanges)}"));
                var state = new List<string>();
                owner = fixture.Tree;
                foreach (var node in path)
                {
                    var item = owner?.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
                    state.Add($"{node.Name}: index={owner?.Items.IndexOf(node)}, realized={item != null}, expanded={node.IsExpanded}, selected={item?.IsSelected}");
                    owner = item;
                }
                Assert.True(navigated, $"Target {target.Name}, selected {(fixture.Tree.SelectedItem as FileSystemNodeModel)?.Name}: {string.Join("; ", state)}");
                await Task.Delay(150);
                fixture.Tree.UpdateLayout();
                Assert.Same(target, fixture.Tree.SelectedItem);
                Assert.True(target.IsSelected);
                AssertInViewport(fixture.Tree, path);
            }
        }
        finally { search.InvalidateIndex(fixture.Roots); }
    });

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

        internal TreeFixture(int depth, int siblings, bool deferRoot = false, bool productionStyle = false)
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
            if (productionStyle)
            {
                var sourceRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../AssetsManager/Themes"));
                var styles = XElement.Load(System.IO.Path.Combine(sourceRoot, "TreeViewStyles.xaml"));
                var buttons = XElement.Load(System.IO.Path.Combine(sourceRoot, "ButtonStyles.xaml"));
                styles.Elements().First().AddAfterSelf(buttons.Elements().Where(e => e.Name.LocalName != "ResourceDictionary.MergedDictionaries"));
                var xaml = styles.ToString()
                        .Replace("/Themes/", "/AssetsManager;component/Themes/")
                        .Replace("clr-namespace:AssetsManager.Views.Helpers", "clr-namespace:AssetsManager.Views.Helpers;assembly=AssetsManager");
                Tree.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(xaml));
                style = new Style(typeof(TreeViewItem), (Style)Tree.FindResource("ModernTreeViewItem"));
                style.Setters.Add(new Setter(UIElement.VisibilityProperty, new Binding("IsVisible") { Converter = new BooleanToVisibilityConverter() }));
                Tree.Style = (Style)Tree.FindResource("ModernTreeView");
                Tree.ItemContainerStyle = style;
            }
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
