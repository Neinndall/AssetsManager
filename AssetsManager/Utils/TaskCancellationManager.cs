using System;
using System.Threading;

namespace AssetsManager.Utils
{
    public class TaskCancellationManager : IDisposable
    {
        private CancellationTokenSource _cancellationTokenSource;

        public CancellationToken Token => _cancellationTokenSource?.Token ?? CancellationToken.None;

        public CancellationToken PrepareNewOperation()
        {
            // Dispose the old one if it exists, to prevent leaks
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
            return _cancellationTokenSource.Token;
        }

        public void CancelCurrentOperation()
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
            }
        }

        public void Dispose()
        {
            _cancellationTokenSource?.Dispose();
        }
    }
}
