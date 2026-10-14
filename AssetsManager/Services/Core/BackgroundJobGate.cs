using System;
using System.Threading;
using System.Threading.Tasks;

namespace AssetsManager.Services.Core
{
    internal sealed class BackgroundJobGate : IDisposable
    {
        private readonly object _sync = new();
        private CancellationTokenSource _lifetime = new();
        private int _running;

        internal void Start()
        {
            lock (_sync)
            {
                if (!_lifetime.IsCancellationRequested) return;
                _lifetime.Dispose();
                _lifetime = new CancellationTokenSource();
            }
        }

        internal void Stop()
        {
            lock (_sync)
            {
                if (!_lifetime.IsCancellationRequested) _lifetime.Cancel();
            }
        }

        internal async Task<bool> TryRunAsync(Func<CancellationToken, Task> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            CancellationToken token;
            lock (_sync) token = _lifetime.Token;
            if (token.IsCancellationRequested || Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;

            try
            {
                token.ThrowIfCancellationRequested();
                await operation(token);
                return !token.IsCancellationRequested;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return false;
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        }

        public void Dispose()
        {
            Stop();
            lock (_sync) _lifetime.Dispose();
        }
    }
}
