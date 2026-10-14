using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;

namespace AssetsManager.Services.Viewer.Interaction
{
    /// <summary>Requests viewport frames after queued input, keeping one pending request per visible host.</summary>
    internal sealed class ViewportFrameScheduler : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly Action _invalidate;
        private readonly Action _draw;
        private readonly Action _promote;
        private readonly Timer _deadlineTimer;
        private DispatcherOperation _pending;
        private long _requestStarted;
        private int _promotionQueued;
        private static readonly TimeSpan MaximumInputDeferral = TimeSpan.FromMilliseconds(8);
        private TimeSpan? _lastRenderingTime;
        private bool _running;
        private bool _disposed;

        internal ViewportFrameScheduler(Dispatcher dispatcher, Action invalidate)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _invalidate = invalidate ?? throw new ArgumentNullException(nameof(invalidate));
            _draw = Draw;
            _promote = PromoteOverdueFrame;
            // WPF also postpones DispatcherTimer ticks while native input is pending. A pool timer
            // can post a foreground operation even during sustained mouse/keyboard input.
            _deadlineTimer = new Timer(_ => QueuePromotion(), null, Timeout.Infinite, Timeout.Infinite);
        }

        internal void Start()
        {
            _dispatcher.VerifyAccess();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) return;
            _running = true;
            CompositionTarget.Rendering += OnRendering;
            RequestFrame(TimeSpan.Zero);
        }

        internal void Stop()
        {
            _dispatcher.VerifyAccess();
            if (_disposed) return;
            if (_running) CompositionTarget.Rendering -= OnRendering;
            _running = false;
            _deadlineTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _pending?.Abort();
            _pending = null;
            _lastRenderingTime = null;
        }

        private void OnRendering(object sender, EventArgs args)
        {
            if (args is RenderingEventArgs frame) RequestFrame(frame.RenderingTime);
        }

        internal void RequestFrame(TimeSpan renderingTime)
        {
            if (!_running || _disposed || _lastRenderingTime == renderingTime) return;
            _lastRenderingTime = renderingTime;
            if (_pending?.Status == DispatcherOperationStatus.Pending) return;
            // WPF's automatic visual invalidation runs above Input priority. Let mouse/key updates
            // finish before scheduling a costly GL frame, without changing its simulation delta.
            _pending = _dispatcher.InvokeAsync(_draw, DispatcherPriority.Background);
            _requestStarted = Stopwatch.GetTimestamp();
            _deadlineTimer.Change(MaximumInputDeferral, Timeout.InfiniteTimeSpan);
        }

        private void QueuePromotion()
        {
            if (_dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _promotionQueued, 1) != 0) return;
            _dispatcher.InvokeAsync(_promote, DispatcherPriority.Loaded);
        }

        private void PromoteOverdueFrame()
        {
            Interlocked.Exchange(ref _promotionQueued, 0);
            if (!_running || _disposed || _pending?.Status != DispatcherOperationStatus.Pending) return;
            TimeSpan elapsed = Stopwatch.GetElapsedTime(_requestStarted);
            if (elapsed >= MaximumInputDeferral)
                _pending.Priority = DispatcherPriority.Loaded;
            else
                _deadlineTimer.Change(TimeSpan.FromMilliseconds(Math.Max(1,
                    (MaximumInputDeferral - elapsed).TotalMilliseconds)), Timeout.InfiniteTimeSpan);
        }

        private void Draw()
        {
            _pending = null;
            _deadlineTimer.Change(Timeout.Infinite, Timeout.Infinite);
            if (_running && !_disposed) _invalidate();
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
            _deadlineTimer.Dispose();
        }
    }
}
