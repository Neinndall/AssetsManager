using System;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Explorer.Tree;
using AssetsManager.Utils;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Models.Explorer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Explorer
{
    public sealed class ExplorerTreeLoadServiceTests
    {
        [Fact]
        public async Task LeavingExplorerDisposesTreeAndInvalidatesIndexBeforeClearingRoots()
        {
            var roots = new ObservableRangeCollection<FileSystemNodeModel>();
            var parent = new FileSystemNodeModel("folder", NodeType.VirtualDirectory);
            var child = new FileSystemNodeModel("asset.bin", NodeType.VirtualFile) { Parent = parent };
            parent.Children.Add(child);
            int invalidations = 0;
            var loader = new ExplorerTreeLoadService(roots, () =>
            {
                invalidations++;
                if (invalidations == 2) Assert.Same(parent, Assert.Single(roots));
            });
            Assert.True(await loader.LoadAsync(_ => Task.FromResult(Nodes(parent))));

            loader.Dispose();
            loader.Dispose();

            Assert.Empty(roots);
            Assert.Null(parent.Name);
            Assert.Null(parent.LoadedChildren);
            Assert.Null(child.Name);
            Assert.Null(child.Parent);
            Assert.Equal(2, invalidations);
            Assert.True(loader.IsDisposed);
        }

        [Fact]
        public async Task LateResultsAfterLeavingCannotRestoreTreeAndAreDisposed()
        {
            var roots = new ObservableRangeCollection<FileSystemNodeModel>();
            var completion = PendingNodes();
            CancellationToken token = default;
            using var loader = new ExplorerTreeLoadService(roots, null);
            var load = loader.LoadAsync(ct => { token = ct; return completion.Task; });
            loader.Dispose();
            Assert.True(token.IsCancellationRequested);

            var lateNode = new FileSystemNodeModel("late.bin", NodeType.VirtualFile);
            completion.SetResult(Nodes(lateNode));

            Assert.False(await load);
            Assert.Empty(roots);
            Assert.Null(lateNode.Name);
        }

        [Fact]
        public async Task OlderLoadCannotReplaceNewerTree()
        {
            var roots = new ObservableRangeCollection<FileSystemNodeModel>();
            var oldCompletion = PendingNodes();
            using var loader = new ExplorerTreeLoadService(roots, null);
            var oldLoad = loader.LoadAsync(_ => oldCompletion.Task);
            var currentNode = new FileSystemNodeModel("current.bin", NodeType.VirtualFile);
            Assert.True(await loader.LoadAsync(_ => Task.FromResult(Nodes(currentNode))));

            var oldNode = new FileSystemNodeModel("old.bin", NodeType.VirtualFile);
            oldCompletion.SetResult(Nodes(oldNode));

            Assert.False(await oldLoad);
            Assert.Same(currentNode, Assert.Single(roots));
            Assert.Null(oldNode.Name);
        }

        [Fact]
        public async Task OlderCompletionCannotMarkNewerLoadFinished()
        {
            var oldCompletion = PendingNodes();
            var newCompletion = PendingNodes();
            using var loader = new ExplorerTreeLoadService(new(), null);
            var oldLoad = loader.LoadAsync(_ => oldCompletion.Task);
            var newLoad = loader.LoadAsync(_ => newCompletion.Task);
            long version = loader.Version;
            oldCompletion.SetResult(new());

            Assert.False(await oldLoad);
            Assert.True(loader.IsLoading);
            Assert.Equal(version, loader.Version);
            newCompletion.SetResult(new());
            Assert.True(await newLoad);
            Assert.False(loader.IsLoading);
        }

        [Fact]
        public async Task ExplicitCancellationKeepsPreviouslyPublishedTreeAndCanBeReloaded()
        {
            var roots = new ObservableRangeCollection<FileSystemNodeModel>();
            using var loader = new ExplorerTreeLoadService(roots, null);
            var original = new FileSystemNodeModel("original.bin", NodeType.VirtualFile);
            await loader.LoadAsync(_ => Task.FromResult(Nodes(original)));
            var pending = PendingNodes();
            var load = loader.LoadAsync(_ => pending.Task);

            Assert.True(loader.Cancel());
            Assert.False(loader.Cancel());
            pending.SetResult(new());
            Assert.False(await load);
            Assert.Same(original, Assert.Single(roots));

            var replacement = new FileSystemNodeModel("replacement.bin", NodeType.VirtualFile);
            Assert.True(await loader.LoadAsync(_ => Task.FromResult(Nodes(replacement))));
            Assert.Null(original.Name);
            Assert.Same(replacement, Assert.Single(roots));
        }

        [Fact]
        public async Task CancellingHashWaitDoesNotCancelSharedCatalogLoading()
        {
            var sharedHashes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var loader = new ExplorerTreeLoadService(new(), null);
            bool built = false;
            var load = loader.LoadAsync(async ct =>
            {
                await sharedHashes.Task.WaitAsync(ct);
                built = true;
                return new();
            });

            Assert.True(loader.Cancel());
            Assert.False(await load);
            Assert.False(sharedHashes.Task.IsCompleted);
            sharedHashes.SetResult();
            await sharedHashes.Task;
            Assert.False(built);
        }

        [Fact]
        public async Task LeavingExplorerDoesNotCancelGlobalExtractionOrComparison()
        {
            using var globalTasks = new TaskCancellationManager();
            var globalToken = globalTasks.PrepareNewOperation();
            using var loader = new ExplorerTreeLoadService(new(), null);
            var load = loader.LoadAsync(async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new();
            });

            loader.Dispose();

            Assert.False(await load);
            Assert.False(globalToken.IsCancellationRequested);
            Assert.False(globalTasks.IsCancelling);
        }

        [Fact]
        public async Task ReenteringExplorerStartsWithAnEmptyTreeAndBuildsAgain()
        {
            var previousRoots = new ObservableRangeCollection<FileSystemNodeModel>();
            using (var previous = new ExplorerTreeLoadService(previousRoots, null))
                await previous.LoadAsync(_ => Task.FromResult(Nodes(new("previous.bin", NodeType.VirtualFile))));

            var nextRoots = new ObservableRangeCollection<FileSystemNodeModel>();
            using var next = new ExplorerTreeLoadService(nextRoots, null);
            Assert.Empty(previousRoots);
            Assert.Empty(nextRoots);
            bool built = false;
            Assert.True(await next.LoadAsync(_ =>
            {
                built = true;
                return Task.FromResult(Nodes(new("new.bin", NodeType.VirtualFile)));
            }));
            Assert.True(built);
            Assert.Equal("new.bin", Assert.Single(nextRoots).Name);
        }

        [Fact]
        public async Task DisposedExplorerRejectsReloadWithoutInvokingBuilder()
        {
            using var loader = new ExplorerTreeLoadService(new(), null);
            loader.Dispose();
            Assert.False(await loader.LoadAsync(_ => throw new InvalidOperationException("Must not run.")));
        }

        [Fact]
        public async Task BuildFailureLeavesLoaderReadyForRetry()
        {
            using var loader = new ExplorerTreeLoadService(new(), null);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                loader.LoadAsync(_ => throw new InvalidOperationException("Broken container.")));
            Assert.False(loader.IsLoading);
            Assert.True(await loader.LoadAsync(_ => Task.FromResult(new ObservableRangeCollection<FileSystemNodeModel>())));
        }

        private static ObservableRangeCollection<FileSystemNodeModel> Nodes(FileSystemNodeModel node) => new() { node };
        private static TaskCompletionSource<ObservableRangeCollection<FileSystemNodeModel>> PendingNodes() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
