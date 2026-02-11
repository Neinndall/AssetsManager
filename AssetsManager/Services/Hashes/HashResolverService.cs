using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LeagueToolkit.Hashing;
using AssetsManager.Utils;
using AssetsManager.Services.Core;

namespace AssetsManager.Services.Hashes
{
    public class HashResolverService
    {
        internal static readonly SemaphoreSlim _hashFileAccessLock = new SemaphoreSlim(1, 1);

        private readonly Dictionary<ulong, string> _hashToPathMap = new Dictionary<ulong, string>();
        private readonly Dictionary<uint, string> _binHashesMap = new Dictionary<uint, string>();
        private readonly Dictionary<uint, string> _binEntriesMap = new Dictionary<uint, string>();
        private readonly Dictionary<uint, string> _binFieldsMap = new Dictionary<uint, string>();
        private readonly Dictionary<uint, string> _binTypesMap = new Dictionary<uint, string>();

        private readonly Dictionary<ulong, string> _fullRstHashesMap = new Dictionary<ulong, string>();
        public IReadOnlyDictionary<ulong, string> FullRstHashes => _fullRstHashesMap;

        private readonly Dictionary<ulong, string> _rstXxh3HashesMap = new Dictionary<ulong, string>();
        public IReadOnlyDictionary<ulong, string> RstXxh3Hashes => _rstXxh3HashesMap;

        private readonly Dictionary<ulong, string> _rstXxh64HashesMap = new Dictionary<ulong, string>();
        public IReadOnlyDictionary<ulong, string> RstXxh64Hashes => _rstXxh64HashesMap;

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
                await Task.WhenAll(
                    LoadHashesAsync(),
                    LoadBinHashesAsync(),
                    LoadRstHashesAsync()
                );
                _logService.LogSuccess("Hashes loaded on startup.");
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to load hashes on startup.");
            }
            finally
            {
                _hashFileAccessLock.Release();
            }
        }

        private bool _gameLcuHashesLoaded = false;
        private bool _binHashesLoaded = false;
        private bool _rstHashesLoaded = false;

        public async Task LoadHashesAsync()
        {
            if (_gameLcuHashesLoaded)
            {
                return;
            }

            var hashesDir = _directoriesCreator.HashesPath;
            var gameHashesFile = Path.Combine(hashesDir, "hashes.game.txt");
            var lcuHashesFile = Path.Combine(hashesDir, "hashes.lcu.txt");

            long totalSize = 0;
            if (File.Exists(gameHashesFile)) totalSize += new FileInfo(gameHashesFile).Length;
            if (File.Exists(lcuHashesFile)) totalSize += new FileInfo(lcuHashesFile).Length;

            _hashToPathMap.Clear();

            await LoadHashesFromFile(gameHashesFile, _hashToPathMap, text => (ulong.TryParse(text, NumberStyles.HexNumber, null, out ulong hash), hash));
            await LoadHashesFromFile(lcuHashesFile, _hashToPathMap, text => (ulong.TryParse(text, NumberStyles.HexNumber, null, out ulong hash), hash));
            _gameLcuHashesLoaded = true;
        }

        public async Task LoadBinHashesAsync()
        {
            if (_binHashesLoaded)
            {
                return;
            }

            _binHashesMap.Clear();
            _binEntriesMap.Clear();
            _binFieldsMap.Clear();
            _binTypesMap.Clear();

            var binHashesDir = _directoriesCreator.HashesPath;
            var files = new[]
            {
                Path.Combine(binHashesDir, "hashes.binhashes.txt"),
                Path.Combine(binHashesDir, "hashes.binentries.txt"),
                Path.Combine(binHashesDir, "hashes.binfields.txt"),
                Path.Combine(binHashesDir, "hashes.bintypes.txt")
            };

            await Task.WhenAll(
                LoadHashesFromFile(files[0], _binHashesMap, text => (uint.TryParse(text, NumberStyles.HexNumber, null, out uint hash), hash)),
                LoadHashesFromFile(files[1], _binEntriesMap, text => (uint.TryParse(text, NumberStyles.HexNumber, null, out uint hash), hash)),
                LoadHashesFromFile(files[2], _binFieldsMap, text => (uint.TryParse(text, NumberStyles.HexNumber, null, out uint hash), hash)),
                LoadHashesFromFile(files[3], _binTypesMap, text => (uint.TryParse(text, NumberStyles.HexNumber, null, out uint hash), hash))
            );
            _binHashesLoaded = true;
        }

        private async Task LoadHashesFromFile<T>(string filePath, IDictionary<T, string> map, Func<string, (bool, T)> parser)
        {
            if (!File.Exists(filePath))
            {
                _logService.LogWarning($"Hash file not found, skipping: {filePath}");
                return;
            }

            await Task.Run(() =>
            {
                using var reader = new StreamReader(filePath);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // The pattern is: [HASH][SPACE][ASSET_PATH]
                    int spaceIndex = line.IndexOf(' ');
                    
                    // Original behavior: Only accept if there is a hash AND a value after the space
                    if (spaceIndex > 0 && spaceIndex < line.Length - 1)
                    {
                        var hashPart = line.Substring(0, spaceIndex);
                        var valuePart = line.Substring(spaceIndex + 1);

                        if (!string.IsNullOrWhiteSpace(valuePart))
                        {
                            var (success, hash) = parser(hashPart);
                            if (success)
                            {
                                map[hash] = valuePart;
                            }
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Resolves a 64-bit game or LCU path hash into a readable file path.
        /// </summary>
        /// <param name="pathHash">The 64-bit hash to resolve.</param>
        /// <returns>The resolved path or the hash as a 16-character hex string if not found.</returns>
        public string ResolveHash(ulong pathHash)
        {
            return _hashToPathMap.TryGetValue(pathHash, out var path) ? path : pathHash.ToString("x16");
        }

        /// <summary>
        /// Resolves a 32-bit BIN hash (entry, field, type, etc.) into its readable name.
        /// </summary>
        /// <param name="hash">The 32-bit hash to resolve.</param>
        /// <returns>The resolved name or the hash as an 8-character hex string if not found.</returns>
        public string ResolveBinHashGeneral(uint hash)
        {
            if (_binEntriesMap.TryGetValue(hash, out var path)) return path;
            if (_binFieldsMap.TryGetValue(hash, out path)) return path;
            if (_binTypesMap.TryGetValue(hash, out path)) return path;
            if (_binHashesMap.TryGetValue(hash, out path)) return path;
            return hash.ToString("x8");
        }

        /// <summary>
        /// Resolves a 64-bit RST text hash into its readable string.
        /// </summary>
        /// <param name="rstHash">The 64-bit hash to resolve.</param>
        /// <returns>The resolved string or the hash as a 16-character hex string if not found.</returns>
        public string ResolveRstHash(ulong rstHash)
        {
            if (_rstXxh3HashesMap.TryGetValue(rstHash, out var path)) return path;
            if (_rstXxh64HashesMap.TryGetValue(rstHash, out path)) return path;
            return rstHash.ToString("x16");
        }

        public async Task LoadRstHashesAsync()
        {
            if (_rstHashesLoaded)
            {
                return;
            }

            // Parallel loading for RST hashes
            await Task.WhenAll(
                LoadRstXxh3HashesAsync(),
                LoadRstXxh64HashesAsync()
            );

            _rstHashesLoaded = true;
        }

        public async Task LoadRstXxh3HashesAsync()
        {
            _rstXxh3HashesMap.Clear();
            var rstHashesDir = _directoriesCreator.HashesPath;
            var file = Path.Combine(rstHashesDir, "hashes.rst.xxh3.txt");
            if (File.Exists(file)) 
            {
                await LoadHashesFromFile(file, _rstXxh3HashesMap, text => (ulong.TryParse(text, NumberStyles.HexNumber, null, out ulong hash), hash));
            }
        }

        public async Task LoadRstXxh64HashesAsync()
        {
            _rstXxh64HashesMap.Clear();
            var rstHashesDir = _directoriesCreator.HashesPath;
            var file = Path.Combine(rstHashesDir, "hashes.rst.xxh64.txt");
            if (File.Exists(file)) 
            {
                await LoadHashesFromFile(file, _rstXxh64HashesMap, text => (ulong.TryParse(text, NumberStyles.HexNumber, null, out ulong hash), hash));
            }
        }
        public Task ForceReloadHashesAsync()
        {
            _gameLcuHashesLoaded = false;
            _binHashesLoaded = false;
            _rstHashesLoaded = false;
            _loadingTask = null; 
            return LoadAllHashesAsync();
        }
    }
}
