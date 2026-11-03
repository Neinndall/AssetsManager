using System;
using System.Collections.Generic;

namespace AssetsManager.Services.Viewer.Rendering.Core;

/// <summary>Context-owned texture lifetime. All calls, including expiry, require the owning GL context.</summary>
internal sealed class GlRetainedTextureCache<TKey> : IDisposable where TKey : class
{
    private sealed class Entry
    {
        internal uint Id;
        internal long Bytes;
        internal int Holders;
        internal long ReleasedAt;
    }
    private readonly Dictionary<TKey, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly Action<uint> _delete;
    private readonly Func<long> _clock;
    private readonly long _retention;
    private readonly long _byteLimit;
    private long _nextSweep;
    private bool _dirty = true;

    internal GlRetainedTextureCache(Action<uint> delete, long byteLimit = 128L * 1024 * 1024,
        long retentionMilliseconds = 15_000, Func<long> clock = null)
    {
        _delete = delete;
        _clock = clock ?? (() => Environment.TickCount64);
        _byteLimit = byteLimit;
        _retention = retentionMilliseconds;
    }

    internal static long MipChainBytes(int width, int height)
    {
        long bytes = 0;
        while (true)
        {
            bytes = checked(bytes + (long)width * height * 4);
            if (width <= 1 && height <= 1) return bytes;
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
    }

    internal bool TryGet(TKey key, out uint id)
    {
        if (_entries.TryGetValue(key, out Entry entry)) { id = entry.Id; return true; }
        id = 0;
        return false;
    }

    internal uint Get(TKey key, long bytes, Func<uint> upload)
    {
        if (!_entries.TryGetValue(key, out Entry entry))
        {
            uint id = upload();
            if (id == 0) return 0;
            _entries.Add(key, entry = new Entry { Id = id, Bytes = bytes, ReleasedAt = _clock() });
            _dirty = true;
        }
        return entry.Id;
    }

    internal uint Acquire(TKey key, long bytes, Func<uint> upload)
    {
        uint id = Get(key, bytes, upload);
        if (id != 0) { _entries[key].Holders++; _dirty = true; }
        return id;
    }

    internal uint GetPinned(TKey key, long bytes, Func<uint> upload)
    {
        uint id = Get(key, bytes, upload);
        if (id != 0) _entries[key].Holders = 1;
        return id;
    }

    internal void SetActiveKeys(IReadOnlySet<TKey> resources)
    {
        foreach (var pair in _entries)
            SetActive(pair.Value, resources.Contains(pair.Key));
        Sweep();
    }

    internal void Release(TKey key)
    {
        if (key == null || !_entries.TryGetValue(key, out Entry entry) || entry.Holders == 0) return;
        if (--entry.Holders == 0) { entry.ReleasedAt = _clock(); _dirty = true; }
    }

    internal void SetActive(IReadOnlySet<uint> handles)
    {
        foreach (Entry entry in _entries.Values)
            SetActive(entry, handles.Contains(entry.Id));
        Sweep();
    }

    private void SetActive(Entry entry, bool active)
    {
        if (active != (entry.Holders > 0)) _dirty = true;
        if (!active && entry.Holders > 0) entry.ReleasedAt = _clock();
        entry.Holders = active ? 1 : 0;
    }

    internal void Sweep()
    {
        long now = _clock();
        if (!_dirty && now < _nextSweep) return;
        _dirty = false;
        _nextSweep = now + 250;
        long idleBytes = 0;
        var idle = new List<KeyValuePair<TKey, Entry>>();
        foreach (var pair in _entries)
            if (pair.Value.Holders == 0) { idle.Add(pair); idleBytes += pair.Value.Bytes; }
        // Avoid sorting the cache on ordinary frames.
        if (idleBytes > _byteLimit) idle.Sort((a, b) => a.Value.ReleasedAt.CompareTo(b.Value.ReleasedAt));
        foreach (var pair in idle)
        {
            if (now - pair.Value.ReleasedAt < _retention && idleBytes <= _byteLimit) continue;
            _delete(pair.Value.Id);
            _entries.Remove(pair.Key);
            idleBytes -= pair.Value.Bytes;
        }
    }

    internal void Abandon() => _entries.Clear();

    public void Dispose()
    {
        try { foreach (Entry entry in _entries.Values) _delete(entry.Id); }
        finally { _entries.Clear(); _dirty = true; }
    }
}
