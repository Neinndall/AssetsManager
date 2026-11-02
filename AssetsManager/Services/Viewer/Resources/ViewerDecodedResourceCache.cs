using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Resources;

internal readonly record struct ViewerDecodeKey(
    MapResolvedAssetCacheKey Asset, string Profile, int Width = 0, int Height = 0);

/// <summary>Shares immutable decodes by source version; weak entries preserve identity for live scenes.</summary>
internal sealed class ViewerDecodedResourceCache<T> : IDisposable where T : class
{
    internal static ViewerDecodedResourceCache<T> Shared { get; } = new();
    private sealed class Entry
    {
        internal WeakReference<T> Weak;
        internal T Retained;
        internal TaskCompletionSource<T> Pending;
        internal long LastUsed;
        internal long Bytes;
    }

    private readonly object _gate = new();
    private readonly Dictionary<ViewerDecodeKey, Entry> _entries = new();
    private readonly Func<long> _clock;
    private readonly long _retention;
    private readonly long _byteLimit;
    private readonly Timer _timer;
    private bool _disposed;

    internal ViewerDecodedResourceCache(long byteLimit = 128L * 1024 * 1024,
        long retentionMilliseconds = 15_000, Func<long> clock = null)
    {
        _clock = clock ?? (() => Environment.TickCount64);
        _retention = retentionMilliseconds;
        _byteLimit = byteLimit;
        _timer = new Timer(_ => Sweep(), null, 15_000, 15_000);
    }

    internal T Get(ViewerDecodeKey key, Func<T> load, Func<T, long> size)
        => GetAsync(key, () => Task.FromResult(load()), size).GetAwaiter().GetResult();

    internal Task<T> GetAsync(ViewerDecodeKey key, Func<Task<T>> load, Func<T, long> size,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Entry entry;
        Task<T> task;
        bool start = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out entry))
                _entries[key] = entry = new Entry();
            if (entry.Weak != null && entry.Weak.TryGetTarget(out T cached))
            {
                entry.Retained = cached;
                entry.LastUsed = _clock();
                SweepCore();
                return Task.FromResult(cached);
            }
            if (entry.Pending == null)
            {
                entry.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                start = true;
            }
            task = entry.Pending.Task;
        }
        // Cancellation belongs to the waiter, never to a load shared with another scene.
        if (start) _ = CompleteAsync(key, entry, load, size);
        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    private async Task CompleteAsync(ViewerDecodeKey key, Entry entry, Func<Task<T>> load, Func<T, long> size)
    {
        TaskCompletionSource<T> pending = entry.Pending;
        try
        {
            T value = await load().ConfigureAwait(false);
            long bytes = value == null ? 0 : Math.Max(0, size(value));
            lock (_gate)
            {
                entry.Pending = null;
                if (!_disposed && value != null)
                {
                    entry.Weak = new(value);
                    entry.Retained = value;
                    entry.Bytes = bytes;
                    entry.LastUsed = _clock();
                    SweepCore();
                }
                else _entries.Remove(key);
            }
            pending.TrySetResult(value);
        }
        catch (Exception ex)
        {
            lock (_gate) _entries.Remove(key);
            pending.TrySetException(ex);
        }
    }

    internal void Sweep()
    {
        lock (_gate) SweepCore();
    }

    private void SweepCore()
    {
        long now = _clock();
        long bytes = 0;
        var idle = new List<KeyValuePair<ViewerDecodeKey, Entry>>();
        var dead = new List<ViewerDecodeKey>();
        foreach (var pair in _entries)
        {
            Entry entry = pair.Value;
            if (now - entry.LastUsed >= _retention) entry.Retained = null;
            if (entry.Retained != null) { bytes += entry.Bytes; idle.Add(pair); }
            else if (entry.Pending == null && (entry.Weak == null || !entry.Weak.TryGetTarget(out _))) dead.Add(pair.Key);
        }
        if (bytes > _byteLimit)
        {
            idle.Sort((a, b) => a.Value.LastUsed.CompareTo(b.Value.LastUsed));
            foreach (var pair in idle)
            {
                if (bytes <= _byteLimit) break;
                pair.Value.Retained = null;
                bytes -= pair.Value.Bytes;
            }
        }
        foreach (var key in dead) _entries.Remove(key);
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _entries.Clear(); }
        _timer.Dispose();
    }
}
