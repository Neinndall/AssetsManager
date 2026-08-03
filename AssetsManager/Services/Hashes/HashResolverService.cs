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
        Official,
        LocalVerified
    }

    public readonly record struct HashResolution(string Value, HashResolutionOrigin Origin);

    public class HashResolverService : IDisposable
    {
        internal static readonly SemaphoreSlim _hashFileAccessLock = new SemaphoreSlim(1, 1);

        private BinaryHashCache _gameCache;
        private BinaryHashCache _lcuCache;
        private readonly HashCatalog _binHashCatalog = new();
        private readonly HashCatalog _binEntryCatalog = new();
        private readonly HashCatalog _binFieldCatalog = new();
        private readonly HashCatalog _binTypeCatalog = new();
        private readonly HashCatalog _rstXxh3Catalog = new();
        private readonly HashCatalog _rstXxh64Catalog = new();

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
            _gameCache = LoadHashCache(Path.Combine(hashesDir, "hashes.game.txt"));
            _lcuCache = LoadHashCache(Path.Combine(hashesDir, "hashes.lcu.txt"));
            _gameLcuHashesLoaded = true;
        }

        public void LoadBinHashes()
        {
            if (_binHashesLoaded) return;
            bool loadVerified = HasCurrentVerificationSchema();

            LoadHashCatalog(_binHashCatalog, "hashes.binhashes.txt", loadVerified);
            LoadHashCatalog(_binEntryCatalog, "hashes.binentries.txt", loadVerified);
            LoadHashCatalog(_binFieldCatalog, "hashes.binfields.txt", loadVerified);
            LoadHashCatalog(_binTypeCatalog, "hashes.bintypes.txt", loadVerified);
            _binHashesLoaded = true;
        }

        private void LoadHashCatalog(HashCatalog catalog, string fileName, bool loadVerified)
        {
            catalog.Official = LoadHashCache(Path.Combine(_directoriesCreator.HashesPath, fileName));

            if (loadVerified)
            {
                catalog.Verified = LoadHashCache(
                    Path.Combine(_directoriesCreator.HashLabPath, "verified", fileName));
            }
        }

        private BinaryHashCache LoadHashCache(string path)
        {
            if (!File.Exists(path)) return null;

            var cache = new BinaryHashCache(path, _logService);
            cache.Load();
            return cache;
        }

        public void LoadRstHashes()
        {
            if (_rstHashesLoaded) return;
            bool loadVerified = HasCurrentVerificationSchema();
            LoadHashCatalog(_rstXxh3Catalog, "hashes.rst.xxh3.txt", loadVerified);
            LoadHashCatalog(_rstXxh64Catalog, "hashes.rst.xxh64.txt", loadVerified);
            _rstHashesLoaded = true;
        }

        private bool HasCurrentVerificationSchema()
        {
            string path = Path.Combine(
                _directoriesCreator.HashLabPath,
                "verified",
                BinRstHashGuessingStore.VerificationSchemaFileName);
            return File.Exists(path) &&
                   int.TryParse(File.ReadAllText(path).Trim(), out int schema) &&
                   schema == InternalHashGuessMatch.CurrentVerificationSchema;
        }

        public Task LoadHashesAsync() { LoadHashes(); return Task.CompletedTask; }
        public Task LoadBinHashesAsync() { LoadBinHashes(); return Task.CompletedTask; }
        public Task LoadRstHashesAsync() { LoadRstHashes(); return Task.CompletedTask; }

        private Dictionary<ulong, string> _cachedRstXxh3Hashes;
        private Dictionary<ulong, string> _cachedRstXxh64Hashes;

        public Dictionary<ulong, string> RstXxh3Hashes => _cachedRstXxh3Hashes ??=
            GetMergedCacheDictionary(_rstXxh3Catalog.Official, _rstXxh3Catalog.Verified);
        public Dictionary<ulong, string> RstXxh64Hashes => _cachedRstXxh64Hashes ??=
            GetMergedCacheDictionary(_rstXxh64Catalog.Official, _rstXxh64Catalog.Verified);

        private static Dictionary<ulong, string> GetMergedCacheDictionary(
            BinaryHashCache officialCache,
            BinaryHashCache verifiedCache)
        {
            var dict = new Dictionary<ulong, string>();
            Add(officialCache, overwrite: true);
            Add(verifiedCache, overwrite: false);
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

        public string ResolveBinHash(uint hash) => ResolveBinHashDetailed(hash).Value;

        public string ResolveBinEntry(uint hash) => ResolveBinEntryDetailed(hash).Value;

        public string ResolveBinField(uint hash) => ResolveBinFieldDetailed(hash).Value;

        public string ResolveBinType(uint hash) => ResolveBinTypeDetailed(hash).Value;

        // BinTreeHash does not carry a domain marker, so generic values need all BIN catalogs.
        public string ResolveBinHashGeneral(uint hash)
        {
            string result = _binHashCatalog.Official?.Resolve(hash);
            if (result != null) return result;
            result = _binEntryCatalog.Official?.Resolve(hash);
            if (result != null) return result;
            result = _binFieldCatalog.Official?.Resolve(hash);
            if (result != null) return result;
            result = _binTypeCatalog.Official?.Resolve(hash);
            if (result != null) return result;

            result = _binHashCatalog.Verified?.Resolve(hash);
            if (result != null) return result;
            result = _binEntryCatalog.Verified?.Resolve(hash);
            if (result != null) return result;
            result = _binFieldCatalog.Verified?.Resolve(hash);
            if (result != null) return result;
            result = _binTypeCatalog.Verified?.Resolve(hash);
            if (result != null) return result;

            return hash.ToString("x8");
        }

        internal HashResolution ResolveBinHashDetailed(uint hash)
            => ResolveBinCatalog(_binHashCatalog, hash);

        internal HashResolution ResolveBinEntryDetailed(uint hash)
            => ResolveBinCatalog(_binEntryCatalog, hash);

        internal HashResolution ResolveBinFieldDetailed(uint hash)
            => ResolveBinCatalog(_binFieldCatalog, hash);

        internal HashResolution ResolveBinTypeDetailed(uint hash)
            => ResolveBinCatalog(_binTypeCatalog, hash);

        private static HashResolution ResolveBinCatalog(HashCatalog catalog, uint hash)
        {
            string result = catalog.Official?.Resolve(hash);
            if (result != null) return new HashResolution(result, HashResolutionOrigin.Official);

            result = catalog.Verified?.Resolve(hash);
            if (result != null) return new HashResolution(result, HashResolutionOrigin.LocalVerified);

            return new HashResolution(hash.ToString("x8"), HashResolutionOrigin.Unknown);
        }

        public string ResolveRstHash(ulong rstHash)
        {
            string result = _rstXxh3Catalog.Official?.Resolve(rstHash);
            if (result != null) return result;
            result = _rstXxh64Catalog.Official?.Resolve(rstHash);
            if (result != null) return result;
            result = _rstXxh3Catalog.Verified?.Resolve(rstHash);
            if (result != null) return result;
            result = _rstXxh64Catalog.Verified?.Resolve(rstHash);
            if (result != null) return result;

            return rstHash.ToString("x16");
        }

        public void ReloadVerifiedHashes()
        {
            bool loadVerified = HasCurrentVerificationSchema();
            ReloadVerifiedCatalog(_binHashCatalog, "hashes.binhashes.txt", loadVerified);
            ReloadVerifiedCatalog(_binEntryCatalog, "hashes.binentries.txt", loadVerified);
            ReloadVerifiedCatalog(_binFieldCatalog, "hashes.binfields.txt", loadVerified);
            ReloadVerifiedCatalog(_binTypeCatalog, "hashes.bintypes.txt", loadVerified);
            ReloadVerifiedCatalog(_rstXxh3Catalog, "hashes.rst.xxh3.txt", loadVerified);
            ReloadVerifiedCatalog(_rstXxh64Catalog, "hashes.rst.xxh64.txt", loadVerified);
            _cachedRstXxh3Hashes = null;
            _cachedRstXxh64Hashes = null;
        }

        private void ReloadVerifiedCatalog(HashCatalog catalog, string fileName, bool loadVerified)
        {
            catalog.Verified?.Dispose();
            catalog.Verified = loadVerified
                ? LoadHashCache(Path.Combine(_directoriesCreator.HashLabPath, "verified", fileName))
                : null;
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
            _binHashCatalog.Dispose();
            _binEntryCatalog.Dispose();
            _binFieldCatalog.Dispose();
            _binTypeCatalog.Dispose();
            _rstXxh3Catalog.Dispose();
            _rstXxh64Catalog.Dispose();
            _gameCache = null;
            _lcuCache = null;
        }

        private sealed class HashCatalog : IDisposable
        {
            public BinaryHashCache Official { get; set; }
            public BinaryHashCache Verified { get; set; }

            public void Dispose()
            {
                Official?.Dispose();
                Verified?.Dispose();
                Official = null;
                Verified = null;
            }
        }
    }
}
