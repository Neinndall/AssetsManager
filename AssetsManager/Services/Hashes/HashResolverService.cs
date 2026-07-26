using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LeagueToolkit.Hashing;
using AssetsManager.Utils;
using AssetsManager.Services.Core;

namespace AssetsManager.Services.Hashes
{
    public class HashResolverService : IDisposable
    {
        internal static readonly SemaphoreSlim _hashFileAccessLock = new SemaphoreSlim(1, 1);

        private readonly List<BinaryHashCache> _gameCaches = new();
        private readonly List<BinaryHashCache> _binCaches = new();
        private readonly List<BinaryHashCache> _binOverrideCaches = new();
        private readonly List<BinaryHashCache> _rstCaches = new();
        private readonly List<BinaryHashCache> _rstOverrideCaches = new();

        private readonly DirectoriesCreator _directoriesCreator;
        private readonly LogService _logService;
        private Task _loadingTask = null;

        public HashResolverService(DirectoriesCreator directoriesCreator, LogService logService)
        {
            _directoriesCreator = directoriesCreator;
            _logService = logService;
        }

        public Task LoadAllHashesAsync()
        {
            if (_loadingTask == null)
            {
                _loadingTask = LoadAllHashesInternalAsync();
            }
            return _loadingTask;
        }

        private async Task LoadAllHashesInternalAsync()
        {
            await _hashFileAccessLock.WaitAsync();
            try
            {
                // Ejecutar en hilo de fondo para no congelar la UI
                await Task.Run(() =>
                {
                    LoadHashes();
                    LoadBinHashes();
                    LoadRstHashes();
                });
                
                _logService.LogSuccess("Hashes loaded on startup.");
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to load hashes.");
            }
            finally
            {
                _hashFileAccessLock.Release();
            }
        }

        private bool _gameLcuHashesLoaded = false;
        private bool _binHashesLoaded = false;
        private bool _rstHashesLoaded = false;

        public void LoadHashes()
        {
            if (_gameLcuHashesLoaded) return;
            var hashesDir = _directoriesCreator.HashesPath;
            var files = new[] { "hashes.game.txt", "hashes.lcu.txt" };
            foreach (var file in files)
            {
                var path = Path.Combine(hashesDir, file);
                if (File.Exists(path))
                {
                    var cache = new BinaryHashCache(path, _logService);
                    cache.Load();
                    _gameCaches.Add(cache);
                }
            }
            _gameLcuHashesLoaded = true;
        }

        public void LoadBinHashes()
        {
            if (_binHashesLoaded) return;
            var binHashesDir = _directoriesCreator.HashesPath;
            var files = new[] { "hashes.binhashes.txt", "hashes.binentries.txt", "hashes.binfields.txt", "hashes.bintypes.txt" };
            foreach (var file in files)
            {
                var path = Path.Combine(binHashesDir, file);
                if (File.Exists(path))
                {
                    var cache = new BinaryHashCache(path, _logService);
                    cache.Load();
                    _binCaches.Add(cache);
                }
                else
                {
                    _binCaches.Add(null);
                }

                var overridePath = Path.Combine(_directoriesCreator.HashLabPath, "overrides", file);
                if (File.Exists(overridePath))
                {
                    var overrideCache = new BinaryHashCache(overridePath, _logService);
                    overrideCache.Load();
                    _binOverrideCaches.Add(overrideCache);
                }
                else
                {
                    _binOverrideCaches.Add(null);
                }
            }
            _binHashesLoaded = true;
        }

        public void LoadRstHashes()
        {
            if (_rstHashesLoaded) return;
            var rstHashesDir = _directoriesCreator.HashesPath;
            var files = new[] { "hashes.rst.xxh3.txt", "hashes.rst.xxh64.txt" };
            foreach (var file in files)
            {
                var path = Path.Combine(rstHashesDir, file);
                if (File.Exists(path))
                {
                    var cache = new BinaryHashCache(path, _logService);
                    cache.Load();
                    _rstCaches.Add(cache);
                }
                else
                {
                    _rstCaches.Add(null);
                }

                var overridePath = Path.Combine(_directoriesCreator.HashLabPath, "overrides", file);
                if (File.Exists(overridePath))
                {
                    var overrideCache = new BinaryHashCache(overridePath, _logService);
                    overrideCache.Load();
                    _rstOverrideCaches.Add(overrideCache);
                }
                else
                {
                    _rstOverrideCaches.Add(null);
                }
            }
            _rstHashesLoaded = true;
        }

        public Task LoadHashesAsync() { LoadHashes(); return Task.CompletedTask; }
        public Task LoadBinHashesAsync() { LoadBinHashes(); return Task.CompletedTask; }
        public Task LoadRstHashesAsync() { LoadRstHashes(); return Task.CompletedTask; }

        private Dictionary<ulong, string> _cachedRstXxh3Hashes;
        private Dictionary<ulong, string> _cachedRstXxh64Hashes;

        public Dictionary<ulong, string> RstXxh3Hashes => _cachedRstXxh3Hashes ??=
            GetMergedCacheDictionary(GetCache(_rstCaches, 0), GetCache(_rstOverrideCaches, 0));
        public Dictionary<ulong, string> RstXxh64Hashes => _cachedRstXxh64Hashes ??=
            GetMergedCacheDictionary(GetCache(_rstCaches, 1), GetCache(_rstOverrideCaches, 1));

        private static BinaryHashCache GetCache(IReadOnlyList<BinaryHashCache> caches, int index) =>
            index >= 0 && index < caches.Count ? caches[index] : null;

        private static Dictionary<ulong, string> GetMergedCacheDictionary(
            BinaryHashCache officialCache,
            BinaryHashCache overrideCache)
        {
            var dict = new Dictionary<ulong, string>();
            Add(officialCache, overwrite: true);
            Add(overrideCache, overwrite: false);
            return dict;

            void Add(BinaryHashCache cache, bool overwrite)
            {
                if (cache == null) return;
                for (int index = 0; index < cache.Count; index++)
                {
                    ulong hash = cache.GetHash(index);
                    if (overwrite)
                        dict[hash] = cache.ResolveByIndex(index);
                    else
                        dict.TryAdd(hash, cache.ResolveByIndex(index));
                }
            }
        }

        public string ResolveHash(ulong pathHash)
        {
            foreach (var cache in _gameCaches)
            {
                var result = cache.Resolve(pathHash);
                if (result != null) return result;
            }
            return pathHash.ToString("x16");
        }

        public bool IsKnownHash(ulong pathHash)
        {
            foreach (var cache in _gameCaches)
            {
                if (cache.Resolve(pathHash) != null) return true;
            }
            return false;
        }

        public string ResolveBinHashGeneral(uint hash)
        {
            foreach (var cache in _binCaches)
            {
                if (cache == null) continue;
                var result = cache.Resolve(hash);
                if (result != null) return result;
            }
            foreach (var cache in _binOverrideCaches)
            {
                if (cache == null) continue;
                var result = cache.Resolve(hash);
                if (result != null) return result;
            }
            return hash.ToString("x8");
        }

        internal string ResolveBinDomain(uint hash, int index)
        {
            if (index >= 0 && index < _binCaches.Count && _binCaches[index] != null)
            {
                string result = _binCaches[index].Resolve(hash);
                if (result != null) return result;
            }
            if (index >= 0 && index < _binOverrideCaches.Count && _binOverrideCaches[index] != null)
            {
                string result = _binOverrideCaches[index].Resolve(hash);
                if (result != null) return result;
            }
            return hash.ToString("x8");
        }

        public string ResolveRstHash(ulong rstHash)
        {
            foreach (var cache in _rstCaches)
            {
                if (cache == null) continue;
                var result = cache.Resolve(rstHash);
                if (result != null) return result;
            }
            foreach (var cache in _rstOverrideCaches)
            {
                if (cache == null) continue;
                var result = cache.Resolve(rstHash);
                if (result != null) return result;
            }
            return rstHash.ToString("x16");
        }

        public Task ForceReloadHashesAsync()
        {
            Dispose();
            _cachedRstXxh3Hashes = null;
            _cachedRstXxh64Hashes = null;
            _gameLcuHashesLoaded = false;
            _binHashesLoaded = false;
            _rstHashesLoaded = false;
            _loadingTask = null; 
            return LoadAllHashesAsync();
        }

        public void Dispose()
        {
            foreach (var c in _gameCaches) c.Dispose();
            foreach (var c in _binCaches) c?.Dispose();
            foreach (var c in _binOverrideCaches) c?.Dispose();
            foreach (var c in _rstCaches) c?.Dispose();
            foreach (var c in _rstOverrideCaches) c?.Dispose();
            _gameCaches.Clear();
            _binCaches.Clear();
            _binOverrideCaches.Clear();
            _rstCaches.Clear();
            _rstOverrideCaches.Clear();
        }
    }
}
