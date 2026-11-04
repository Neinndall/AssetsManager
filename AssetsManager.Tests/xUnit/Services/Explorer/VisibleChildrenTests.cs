using System.Linq;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Models.Explorer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Explorer;

public sealed class VisibleChildrenTests
{
    [Fact]
    public void FilteringAndClearingKeepTheVisibleCollectionAndSurvivingRows()
    {
        using var node = new FileSystemNodeModel("root", NodeType.VirtualDirectory);
        var first = new FileSystemNodeModel("first", NodeType.VirtualFile);
        var selected = new FileSystemNodeModel("selected", NodeType.VirtualFile) { IsSelected = true };
        var last = new FileSystemNodeModel("last", NodeType.VirtualFile);
        node.Children.AddRange(new[] { first, selected, last });
        var visible = node.VisibleChildren;
        var changes = new System.Collections.Generic.List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        visible.CollectionChanged += (_, e) => changes.Add(e.Action);
        node.SetVisibleChildren(new[] { selected });
        Assert.True(node.HasFilteredChildren);
        Assert.Same(visible, node.VisibleChildren);
        Assert.Equal(new[] { selected }, visible.ToArray());
        node.SetVisibleChildren(null);
        Assert.False(node.HasFilteredChildren);
        Assert.Same(visible, node.VisibleChildren);
        Assert.Equal(node.Children.ToArray(), visible.ToArray());
        Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, changes);
        Assert.True(selected.IsSelected);
    }

    [Fact]
    public void UnfilteredProjectionFollowsChildReplacementAndLaterAudioExpansion()
    {
        using var node = new FileSystemNodeModel("bank", NodeType.SoundBank);
        var oldChildren = node.Children;
        var visible = node.VisibleChildren;
        var first = new FileSystemNodeModel("event", NodeType.AudioEvent);
        oldChildren.Add(first);
        Assert.Same(first, Assert.Single(visible));
        var next = new FileSystemNodeModel("next", NodeType.AudioEvent);
        node.Children = new ObservableRangeCollection<FileSystemNodeModel> { next };
        Assert.Same(visible, node.VisibleChildren);
        Assert.Same(next, Assert.Single(visible));
        oldChildren.Add(new FileSystemNodeModel("detached", NodeType.AudioEvent));
        Assert.Same(next, Assert.Single(visible));
        node.Children.Clear();
        Assert.Empty(visible);
        node.Children.AddRange(new[] { first, next });
        Assert.Equal(new[] { first, next }, visible.ToArray());
        node.Children.Remove(first);
        Assert.Same(next, Assert.Single(visible));
    }
}
