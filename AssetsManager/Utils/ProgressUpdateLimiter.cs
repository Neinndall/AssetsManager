using System;
using System.Diagnostics;

namespace AssetsManager.Utils
{
    internal sealed class ProgressUpdateLimiter<T> : IProgress<T>
    {
        private readonly IProgress<T> _target;
        private readonly long _minimumIntervalTicks;
        private readonly object _sync = new();
        private long _lastUpdateTimestamp;
        private T _pending;
        private bool _hasPending;

        public ProgressUpdateLimiter(IProgress<T> target, TimeSpan minimumInterval)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            if (minimumInterval < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(minimumInterval));

            _minimumIntervalTicks = (long)(minimumInterval.TotalSeconds * Stopwatch.Frequency);
        }

        public void Report(T value)
        {
            lock (_sync)
            {
                long now = Stopwatch.GetTimestamp();
                if (_lastUpdateTimestamp != 0 && now - _lastUpdateTimestamp < _minimumIntervalTicks)
                {
                    _pending = value;
                    _hasPending = true;
                    return;
                }

                _lastUpdateTimestamp = now;
                _hasPending = false;
            }

            _target.Report(value);
        }

        // Flushes the last throttled value so the UI reaches its final state before completion.
        public void Flush()
        {
            T pending;
            lock (_sync)
            {
                if (!_hasPending)
                    return;

                pending = _pending;
                _hasPending = false;
                _pending = default;
                _lastUpdateTimestamp = Stopwatch.GetTimestamp();
            }

            _target.Report(pending);
        }
    }
}
