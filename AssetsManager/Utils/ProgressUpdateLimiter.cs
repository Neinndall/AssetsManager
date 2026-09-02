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
                    return;

                _lastUpdateTimestamp = now;
            }

            _target.Report(value);
        }
    }
}
