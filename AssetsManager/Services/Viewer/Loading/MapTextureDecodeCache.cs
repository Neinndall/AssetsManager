using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Loading
{
    /// <summary>
    /// Shares one decoded MAP image per asset/width between stock and game-program texture users.
    /// GPU uploads remain separate so stock colour textures can use sRGB while Hexshade samples raw data.
    /// </summary>
    internal sealed class MapTextureDecodeCache : IDisposable
    {
        private sealed record DecodeKey(MapResolvedAssetCacheKey Asset, int RequestedWidth);

        private sealed class DecodeLoad
        {
            private readonly object _gate = new();
            private readonly DecodeKey _key;
            private readonly Func<Task<MapTextureImage>> _load;
            private readonly Action<DecodeKey, MapTextureImage> _landed;
            private Task<MapTextureImage> _task;

            internal DecodeLoad(
                DecodeKey key,
                Func<Task<MapTextureImage>> load,
                Action<DecodeKey, MapTextureImage> landed)
            {
                _key = key;
                _load = load;
                _landed = landed;
            }

            internal Task<MapTextureImage> GetTask()
            {
                lock (_gate)
                    return _task ??= RunAsync();
            }

            internal Task<MapTextureImage> StartedTask
            {
                get
                {
                    lock (_gate)
                        return _task;
                }
            }

            internal bool TryGetCompleted(out MapTextureImage image)
            {
                lock (_gate)
                {
                    if (_task?.IsCompletedSuccessfully == true && _task.Result != null)
                    {
                        image = _task.Result;
                        return true;
                    }
                }

                image = null;
                return false;
            }

            private async Task<MapTextureImage> RunAsync()
            {
                MapTextureImage image = await _load().ConfigureAwait(false);
                if (image != null)
                    _landed(_key, image);
                return image;
            }
        }

        private readonly RetainedCache<DecodeKey, DecodeLoad> _entries;
        private readonly ConcurrentDictionary<MapTextureImage, DecodeKey> _keysByImage =
            new(ReferenceEqualityComparer.Instance);
        private int _disposed;

        internal MapTextureDecodeCache(TimeSpan? retention = null)
        {
            _entries = new RetainedCache<DecodeKey, DecodeLoad>(OnEvicted, retention);
        }

        internal int TrackedImageCount => _keysByImage.Count;

        internal bool TryGetCompleted(
            MapResolvedAsset asset,
            int requestedWidth,
            out MapTextureImage image)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(asset);
            var key = new DecodeKey(MapResolvedAssetCacheKey.From(asset), requestedWidth);
            if (_entries.TryPeek(key, out DecodeLoad load) && load.TryGetCompleted(out image))
                return true;

            image = null;
            return false;
        }

        internal async Task<MapTextureImage> GetOrLoadAsync(
            MapResolvedAsset asset,
            int requestedWidth,
            Func<Task<MapTextureImage>> load,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(asset);
            ArgumentNullException.ThrowIfNull(load);
            if (requestedWidth <= 0)
                throw new ArgumentOutOfRangeException(nameof(requestedWidth));

            var key = new DecodeKey(MapResolvedAssetCacheKey.From(asset), requestedWidth);
            DecodeLoad entry = _entries.Get(key, () => new DecodeLoad(key, load, OnLanded));
            Action release = _entries.Hold(key);
            Task<MapTextureImage> task = entry.GetTask();
            try
            {
                MapTextureImage image = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (image == null)
                    _entries.Evict(key);
                return image;
            }
            catch when (task.IsFaulted || task.IsCanceled)
            {
                _entries.Evict(key);
                throw;
            }
            finally
            {
                release();
            }
        }

        /// <summary>
        /// Keeps the decoded pixels alive while a MAP runtime is drawing an image. The returned
        /// release starts the same 15-second grace period used by the upstream retained cache.
        /// </summary>
        internal Action Hold(MapTextureImage image)
        {
            ThrowIfDisposed();
            if (image == null || !_keysByImage.TryGetValue(image, out DecodeKey key))
                return static () => { };
            return _entries.Hold(key);
        }

        private void OnLanded(DecodeKey key, MapTextureImage image)
        {
            if (Volatile.Read(ref _disposed) == 0)
                _keysByImage[image] = key;
        }

        private void OnEvicted(DecodeLoad load)
        {
            if (load == null)
                return;
            if (load.TryGetCompleted(out MapTextureImage image))
            {
                _keysByImage.TryRemove(image, out _);
                return;
            }

            Task<MapTextureImage> pending = load.StartedTask;
            if (pending == null)
                return;
            _ = pending.ContinueWith(
                completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion && completed.Result != null)
                        _keysByImage.TryRemove(completed.Result, out _);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _entries.Dispose();
            _keysByImage.Clear();
        }
    }
}
