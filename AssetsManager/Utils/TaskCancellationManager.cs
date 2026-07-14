using System;
using System.Threading;

namespace AssetsManager.Utils
{
    public class TaskCancellationManager : IDisposable
    {
        private CancellationTokenSource _cancellationTokenSource;

        public bool IsCancelling { get; private set; }
        public string CancellationMessage { get; private set; } = "Cancelling Task...";

        public event EventHandler OperationStateChanged;

        public CancellationToken Token => _cancellationTokenSource?.Token ?? CancellationToken.None;

        public CancellationToken PrepareNewOperation()
        {
            // Cancel and dispose the old one if it exists, to signal ongoing tasks
            if (_cancellationTokenSource != null)
            {
                if (!_cancellationTokenSource.IsCancellationRequested)
                {
                    _cancellationTokenSource.Cancel();
                }
                _cancellationTokenSource.Dispose();
            }

            _cancellationTokenSource = new CancellationTokenSource();
            IsCancelling = false;
            OperationStateChanged?.Invoke(this, EventArgs.Empty);
            return _cancellationTokenSource.Token;
        }

        public void CancelCurrentOperation(bool notifyUI = true, string message = "Cancelling Task...")
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                if (notifyUI)
                {
                    CancellationMessage = message;
                    IsCancelling = true;
                    OperationStateChanged?.Invoke(this, EventArgs.Empty);
                }
                _cancellationTokenSource.Cancel();
            }
        }

        public void CompleteCurrentOperation()
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            IsCancelling = false;
            OperationStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (_cancellationTokenSource != null)
            {
                try
                {
                    if (!_cancellationTokenSource.IsCancellationRequested)
                    {
                        _cancellationTokenSource.Cancel();
                    }
                }
                catch (ObjectDisposedException) { }

                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;
            }
            IsCancelling = false;
        }
    }
}
