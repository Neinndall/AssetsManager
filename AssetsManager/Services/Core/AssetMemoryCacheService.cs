using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AssetsManager.Services.Core
{
    public sealed class AssetMemoryCacheService
    {
        public const long DefaultByteBudget = 256L * 1024 * 1024;
        public const long DefaultImageBudget = 192L * 1024 * 1024;
        public const long DefaultTextBudget = 64L * 1024 * 1024;

        private readonly MemoryBudgetLruCache<string, byte[]> _byteCache;
        private readonly MemoryBudgetLruCache<string, ImageSource> _imageCache;
        private readonly MemoryBudgetLruCache<string, string> _textCache;

        public AssetMemoryCacheService()
            : this(DefaultByteBudget, DefaultImageBudget, DefaultTextBudget)
        {
        }

        public AssetMemoryCacheService(long byteBudget, long imageBudget, long textBudget)
        {
            _byteCache = new MemoryBudgetLruCache<string, byte[]>(byteBudget, value => value.LongLength);
            _imageCache = new MemoryBudgetLruCache<string, ImageSource>(imageBudget, EstimateImageBytes);
            _textCache = new MemoryBudgetLruCache<string, string>(textBudget, value => (long)value.Length * sizeof(char));
        }

        public bool TryGetBytes(string key, out byte[] value) => _byteCache.TryGet(key, out value);

        public void SetBytes(string key, byte[] value)
        {
            if (value != null)
            {
                _byteCache.Set(key, value);
            }
        }

        public string CreateImageKey(ReadOnlySpan<byte> data, string extension, int maxWidth, int maxHeight) =>
            CreateDerivedKey("image", data, extension, maxWidth, maxHeight);

        public bool TryGetImage(string key, out ImageSource value) => _imageCache.TryGet(key, out value);

        public void SetImage(string key, ImageSource value)
        {
            if (value == null)
            {
                return;
            }

            if (!value.IsFrozen)
            {
                if (!value.CanFreeze)
                {
                    return;
                }

                value.Freeze();
            }

            _imageCache.Set(key, value);
        }

        public string CreateTextKey(string dataType, ReadOnlySpan<byte> data) =>
            CreateDerivedKey("text", data, dataType, 0, 0);

        public bool TryGetText(string key, out string value) => _textCache.TryGet(key, out value);

        public void SetText(string key, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                _textCache.Set(key, value);
            }
        }

        public AssetMemoryCacheSnapshot GetSnapshot() => new(
            _byteCache.GetSnapshot(),
            _imageCache.GetSnapshot(),
            _textCache.GetSnapshot());

        private static string CreateDerivedKey(string tier, ReadOnlySpan<byte> data, string discriminator, int width, int height)
        {
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(data, digest);
            ulong first = BinaryPrimitives.ReadUInt64LittleEndian(digest);
            ulong second = BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]);
            return $"{tier}|{discriminator?.ToLowerInvariant()}|{width}x{height}|{data.Length}|{first:X16}{second:X16}";
        }

        private static long EstimateImageBytes(ImageSource image)
        {
            if (image is BitmapSource bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
            {
                int bitsPerPixel = Math.Max(bitmap.Format.BitsPerPixel, 32);
                long stride = ((long)bitmap.PixelWidth * bitsPerPixel + 7) / 8;
                return Math.Max(1, stride * bitmap.PixelHeight);
            }

            double width = image.Width;
            double height = image.Height;
            if (double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0)
            {
                return Math.Max(1, checked((long)Math.Ceiling(width * height * 4)));
            }

            return 4096;
        }
    }

    public readonly record struct AssetMemoryCacheSnapshot(
        MemoryBudgetCacheSnapshot Bytes,
        MemoryBudgetCacheSnapshot Images,
        MemoryBudgetCacheSnapshot Text);

    public readonly record struct MemoryBudgetCacheSnapshot(
        long BudgetBytes,
        long UsedBytes,
        int Count,
        long Hits,
        long Misses,
        long Evictions);

    internal sealed class MemoryBudgetLruCache<TKey, TValue> where TKey : notnull
    {
        private sealed class Entry
        {
            public Entry(TKey key, TValue value, long size)
            {
                Key = key;
                Value = value;
                Size = size;
            }

            public TKey Key { get; }
            public TValue Value { get; }
            public long Size { get; }
        }

        private readonly object _sync = new();
        private readonly long _budgetBytes;
        private readonly Func<TValue, long> _sizeEstimator;
        private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = new();
        private readonly LinkedList<Entry> _lru = new();
        private long _usedBytes;
        private long _hits;
        private long _misses;
        private long _evictions;

        public MemoryBudgetLruCache(long budgetBytes, Func<TValue, long> sizeEstimator)
        {
            if (budgetBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(budgetBytes));
            }

            _budgetBytes = budgetBytes;
            _sizeEstimator = sizeEstimator ?? throw new ArgumentNullException(nameof(sizeEstimator));
        }

        public bool TryGet(TKey key, out TValue value)
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    Interlocked.Increment(ref _hits);
                    value = node.Value.Value;
                    return true;
                }

                Interlocked.Increment(ref _misses);
                value = default;
                return false;
            }
        }

        public void Set(TKey key, TValue value)
        {
            long size = Math.Max(0, _sizeEstimator(value));
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var existing))
                {
                    Remove(existing);
                }

                if (size > _budgetBytes || _budgetBytes == 0)
                {
                    return;
                }

                while (_usedBytes + size > _budgetBytes && _lru.Last != null)
                {
                    Remove(_lru.Last);
                    Interlocked.Increment(ref _evictions);
                }

                var node = new LinkedListNode<Entry>(new Entry(key, value, size));
                _lru.AddFirst(node);
                _entries.Add(key, node);
                _usedBytes += size;
            }
        }

        public MemoryBudgetCacheSnapshot GetSnapshot()
        {
            lock (_sync)
            {
                return new MemoryBudgetCacheSnapshot(
                    _budgetBytes,
                    _usedBytes,
                    _entries.Count,
                    Interlocked.Read(ref _hits),
                    Interlocked.Read(ref _misses),
                    Interlocked.Read(ref _evictions));
            }
        }

        private void Remove(LinkedListNode<Entry> node)
        {
            _entries.Remove(node.Value.Key);
            _lru.Remove(node);
            _usedBytes -= node.Value.Size;
        }
    }
}
