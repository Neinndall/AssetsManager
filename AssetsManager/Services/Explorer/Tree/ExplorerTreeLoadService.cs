using System;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Utils.Framework;
using AssetsManager.Views.Models.Explorer;

namespace AssetsManager.Services.Explorer.Tree
{
    /// <summary>Owns one Explorer tree and rejects results from cancelled or replaced loads.</summary>
    public sealed class ExplorerTreeLoadService : IDisposable
    {
        private readonly ObservableRangeCollection<FileSystemNodeModel> _roots;
        private readonly Action _invalidateIndex;
        private CancellationTokenSource _currentLoad;

        public long Version { get; private set; }
        public bool IsDisposed { get; private set; }
        public bool IsLoading => _currentLoad != null && !_currentLoad.IsCancellationRequested;

        public ExplorerTreeLoadService(ObservableRangeCollection<FileSystemNodeModel> roots, Action invalidateIndex)
        {
            _roots = roots;
            _invalidateIndex = invalidateIndex;
        }

        public async Task<bool> LoadAsync(Func<CancellationToken, Task<ObservableRangeCollection<FileSystemNodeModel>>> build)
        {
            if (IsDisposed) return false;

            Cancel();
            var source = new CancellationTokenSource();
            _currentLoad = source;
            Version++;
            ObservableRangeCollection<FileSystemNodeModel> result = null;
            try
            {
                result = await build(source.Token);
                source.Token.ThrowIfCancellationRequested();
                if (IsDisposed || !ReferenceEquals(_currentLoad, source)) return false;

                ReleaseTree();
                _roots.ReplaceRange(result);
                result = null; // The published tree now owns these nodes.
                return true;
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested)
            {
                return false;
            }
            finally
            {
                if (result != null) DisposeNodes(result);
                if (ReferenceEquals(_currentLoad, source)) _currentLoad = null;
                // A superseded load retains its source until its work has actually unwound.
                source.Dispose();
            }
        }

        public bool Cancel()
        {
            if (!IsLoading) return false;
            Version++;
            _currentLoad.Cancel();
            return true;
        }

        private void ReleaseTree()
        {
            _invalidateIndex?.Invoke();
            DisposeNodes(_roots);
        }

        internal static void DisposeNodes(ObservableRangeCollection<FileSystemNodeModel> nodes)
        {
            foreach (var node in nodes) node?.Dispose();
            nodes.Clear();
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            Cancel();
            Version++;
            ReleaseTree();
        }
    }
}
