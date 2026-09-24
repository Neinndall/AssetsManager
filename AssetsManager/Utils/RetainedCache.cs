using System;
using System.Collections.Generic;
using System.Threading;

namespace AssetsManager.Utils
{
    /// <summary>
    /// Shares one value per key while active users hold it, then keeps it alive for a short grace period.
    /// The dispose callback may run on a timer thread; OpenGL resource destruction must stay renderer-owned.
    /// </summary>
    internal sealed class RetainedCache<TKey, TValue> : IDisposable where TKey : notnull
    {
        private sealed class Entry
        {
            internal Entry(TValue value)
            {
                Value = value;
            }

            internal TValue Value { get; }
            internal int Holders;
            internal Timer Expiry;
        }

        internal static readonly TimeSpan DefaultRetention = TimeSpan.FromSeconds(15);

        private readonly object _gate = new();
        private readonly Dictionary<TKey, Entry> _entries;
        private readonly Action<TValue> _dispose;
        private readonly TimeSpan _retention;
        private bool _disposed;

        internal RetainedCache(Action<TValue> dispose, TimeSpan? retention = null, IEqualityComparer<TKey> comparer = null)
        {
            _dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));
            _retention = retention ?? DefaultRetention;
            if (_retention < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(retention));
            _entries = new Dictionary<TKey, Entry>(comparer ?? EqualityComparer<TKey>.Default);
        }

        internal TValue Get(TKey key, Func<TValue> create)
        {
            ArgumentNullException.ThrowIfNull(create);
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_entries.TryGetValue(key, out Entry held))
                    return held.Value;

                var entry = new Entry(create());
                _entries.Add(key, entry);
                ScheduleExpiry(key, entry);
                return entry.Value;
            }
        }

        internal bool TryPeek(TKey key, out TValue value)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_entries.TryGetValue(key, out Entry entry))
                {
                    value = entry.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        internal Action Hold(TKey key)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (!_entries.TryGetValue(key, out Entry entry))
                    return static () => { };

                entry.Holders++;
                entry.Expiry?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                int released = 0;
                return () =>
                {
                    if (Interlocked.Exchange(ref released, 1) != 0)
                        return;
                    Release(key, entry);
                };
            }
        }

        internal void Evict(TKey key)
        {
            Entry removed = null;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_entries.Remove(key, out removed))
                    removed.Expiry?.Dispose();
            }

            if (removed != null)
                _dispose(removed.Value);
        }

        private void Release(TKey key, Entry entry)
        {
            lock (_gate)
            {
                if (_disposed || !_entries.TryGetValue(key, out Entry current) || !ReferenceEquals(current, entry))
                    return;

                entry.Holders = Math.Max(0, entry.Holders - 1);
                if (entry.Holders == 0)
                    ScheduleExpiry(key, entry);
            }
        }

        private void ScheduleExpiry(TKey key, Entry entry)
        {
            entry.Expiry ??= new Timer(_ => Expire(key, entry), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            entry.Expiry.Change(_retention, Timeout.InfiniteTimeSpan);
        }

        private void Expire(TKey key, Entry entry)
        {
            TValue value = default;
            bool dispose = false;
            lock (_gate)
            {
                if (_disposed || entry.Holders != 0 ||
                    !_entries.TryGetValue(key, out Entry current) || !ReferenceEquals(current, entry))
                {
                    return;
                }

                _entries.Remove(key);
                entry.Expiry?.Dispose();
                value = entry.Value;
                dispose = true;
            }

            if (dispose)
                _dispose(value);
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        public void Dispose()
        {
            List<TValue> values;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                values = new List<TValue>(_entries.Count);
                foreach (Entry entry in _entries.Values)
                {
                    entry.Expiry?.Dispose();
                    values.Add(entry.Value);
                }
                _entries.Clear();
            }

            foreach (TValue value in values)
                _dispose(value);
        }
    }
}
