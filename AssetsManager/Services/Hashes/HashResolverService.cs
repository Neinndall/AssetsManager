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
using AssetsManager.Views.Models.Hashes;

namespace AssetsManager.Services.Hashes
{
    public enum HashResolutionOrigin
    {
        Unknown,
        Official
    }

    public readonly record struct HashResolution(string Value, HashResolutionOrigin Origin);

    public class HashResolverService : IDisposable
    {
        internal static readonly SemaphoreSlim _hashFileAccessLock = new SemaphoreSlim(1, 1);

        private BinaryHashCache _gameCache;
        private BinaryHashCache _lcuCache;
        private BinaryHashCache _binHashCache;
        private BinaryHashCache _binEntryCache;
        private BinaryHashCache _binFieldCache;
        private BinaryHashCache _binTypeCache;
        private BinaryHashCache _rstXxh3Cache;
        private BinaryHashCache _rstXxh64Cache;

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
            _gameCache = LoadHashCache(Path.Combine(hashesDir, "hashes.game.txt"));
            _lcuCache = LoadHashCache(Path.Combine(hashesDir, "hashes.lcu.txt"));
            _gameLcuHashesLoaded = true;
        }

        public void LoadBinHashes()
        {
            if (_binHashesLoaded) return;
            var hashesDir = _directoriesCreator.HashesPath;
            _binHashCache = LoadHashCache(Path.Combine(hashesDir, "hashes.binhashes.txt"));
            _binEntryCache = LoadHashCache(Path.Combine(hashesDir, "hashes.binentries.txt"));
            _binFieldCache = LoadHashCache(Path.Combine(hashesDir, "hashes.binfields.txt"));
            _binTypeCache = LoadHashCache(Path.Combine(hashesDir, "hashes.bintypes.txt"));
            _binHashesLoaded = true;
        }

        public void LoadRstHashes()
        {
            if (_rstHashesLoaded) return;
            var hashesDir = _directoriesCreator.HashesPath;
            _rstXxh3Cache = LoadHashCache(Path.Combine(hashesDir, "hashes.rst.xxh3.txt"));
            _rstXxh64Cache = LoadHashCache(Path.Combine(hashesDir, "hashes.rst.xxh64.txt"));
            _rstHashesLoaded = true;
        }

        private BinaryHashCache LoadHashCache(string path)
        {
            if (!File.Exists(path)) return null;

            var cache = new BinaryHashCache(path, _logService);
            cache.Load();
            return cache;
        }

        public Task LoadHashesAsync() { LoadHashes(); return Task.CompletedTask; }
        public Task LoadBinHashesAsync() { LoadBinHashes(); return Task.CompletedTask; }
        public Task LoadRstHashesAsync() { LoadRstHashes(); return Task.CompletedTask; }

        private Dictionary<ulong, string> _cachedRstXxh3Hashes;
        private Dictionary<ulong, string> _cachedRstXxh64Hashes;

        public Dictionary<ulong, string> RstXxh3Hashes => _cachedRstXxh3Hashes ??=
            BuildCacheDictionary(_rstXxh3Cache);
        public Dictionary<ulong, string> RstXxh64Hashes => _cachedRstXxh64Hashes ??=
            BuildCacheDictionary(_rstXxh64Cache);

        private static Dictionary<ulong, string> BuildCacheDictionary(BinaryHashCache cache)
        {
            var dict = new Dictionary<ulong, string>();
            if (cache == null) return dict;
            for (int index = 0; index < cache.Count; index++)
            {
                dict[cache.GetHash(index)] = cache.ResolveByIndex(index);
            }
            return dict;
        }

        public string ResolveHash(ulong pathHash)
        {
            string result = _gameCache?.Resolve(pathHash);
            if (result != null) return result;
            result = _lcuCache?.Resolve(pathHash);
            if (result != null) return result;

            return pathHash.ToString("x16");
        }

        public bool IsKnownHash(ulong pathHash)
        {
            return _gameCache?.Resolve(pathHash) != null ||
                   _lcuCache?.Resolve(pathHash) != null;
        }

        public string ResolveBinHash(uint hash) => _binHashCache?.Resolve(hash) ?? hash.ToString("x8");

        public string ResolveBinEntry(uint hash) => _binEntryCache?.Resolve(hash) ?? hash.ToString("x8");

        public string ResolveBinField(uint hash) => _binFieldCache?.Resolve(hash) ?? hash.ToString("x8");

        public string ResolveBinType(uint hash) => _binTypeCache?.Resolve(hash) ?? hash.ToString("x8");

        // BinTreeHash does not carry a domain marker, so generic values need all BIN catalogs.
        public string ResolveBinHashGeneral(uint hash)
        {
            string result = _binHashCache?.Resolve(hash);
            if (result != null) return result;
            result = _binEntryCache?.Resolve(hash);
            if (result != null) return result;
            result = _binFieldCache?.Resolve(hash);
            if (result != null) return result;
            result = _binTypeCache?.Resolve(hash);
            if (result != null) return result;

            return hash.ToString("x8");
        }

        internal HashResolution ResolveBinHashDetailed(uint hash)
            => ResolveDetailed(_binHashCache, hash);

        internal HashResolution ResolveBinEntryDetailed(uint hash)
            => ResolveDetailed(_binEntryCache, hash);

        internal HashResolution ResolveBinFieldDetailed(uint hash)
            => ResolveDetailed(_binFieldCache, hash);

        internal HashResolution ResolveBinTypeDetailed(uint hash)
            => ResolveDetailed(_binTypeCache, hash);

        private static HashResolution ResolveDetailed(BinaryHashCache cache, uint hash)
        {
            string result = cache?.Resolve(hash);
            if (result != null) return new HashResolution(result, HashResolutionOrigin.Official);

            return new HashResolution(hash.ToString("x8"), HashResolutionOrigin.Unknown);
        }

        public string ResolveRstHash(ulong rstHash)
        {
            string result = _rstXxh3Cache?.Resolve(rstHash);
            if (result != null) return result;
            result = _rstXxh64Cache?.Resolve(rstHash);
            if (result != null) return result;

            return rstHash.ToString("x16");
        }

        public void ReloadBinRstHashes()
        {
            _binHashCache?.Dispose();
            _binEntryCache?.Dispose();
            _binFieldCache?.Dispose();
            _binTypeCache?.Dispose();
            _rstXxh3Cache?.Dispose();
            _rstXxh64Cache?.Dispose();

            _binHashesLoaded = false;
            _rstHashesLoaded = false;
            _cachedRstXxh3Hashes = null;
            _cachedRstXxh64Hashes = null;

            LoadBinHashes();
            LoadRstHashes();
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
            _gameCache?.Dispose();
            _lcuCache?.Dispose();
            _binHashCache?.Dispose();
            _binEntryCache?.Dispose();
            _binFieldCache?.Dispose();
            _binTypeCache?.Dispose();
            _rstXxh3Cache?.Dispose();
            _rstXxh64Cache?.Dispose();
            _gameCache = null;
            _lcuCache = null;
            _binHashCache = null;
            _binEntryCache = null;
            _binFieldCache = null;
            _binTypeCache = null;
            _rstXxh3Cache = null;
            _rstXxh64Cache = null;
        }
    }
}
