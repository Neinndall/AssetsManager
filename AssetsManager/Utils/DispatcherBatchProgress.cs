using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace AssetsManager.Utils
{
    // Worker reports only enqueue references; a bounded UI batch keeps input and rendering responsive.
    internal sealed class DispatcherBatchProgress<T> : IProgress<T>, IDisposable
    {
        private readonly ConcurrentQueue<T> _pending = new();
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _timer;
        private readonly Action<T> _consume;
        private readonly Action _afterBatch;
        private bool _disposed;

        public DispatcherBatchProgress(Dispatcher dispatcher, TimeSpan interval, Action<T> consume, Action afterBatch = null)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _dispatcher.VerifyAccess();
            _consume = consume ?? throw new ArgumentNullException(nameof(consume));
            _afterBatch = afterBatch;
            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = interval };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        public void Report(T value) => _pending.Enqueue(value);

        private void OnTick(object sender, EventArgs e) => DrainBatch();

        private void DrainBatch()
        {
            long started = Stopwatch.GetTimestamp();
            int consumed = 0;
            while (consumed < 256 && _pending.TryDequeue(out T value))
            {
                _consume(value);
                consumed++;
                if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 8) break;
            }
            if (consumed > 0) _afterBatch?.Invoke();
        }

        // Call after the worker has stopped, including cancellation, before updating the final UI state.
        public async Task DrainAsync()
        {
            _dispatcher.VerifyAccess();
            _timer.Stop();
            while (!_pending.IsEmpty)
            {
                DrainBatch();
                if (!_pending.IsEmpty) await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }

        public void Dispose()
        {
            _dispatcher.VerifyAccess();
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTick;
        }
    }
}
