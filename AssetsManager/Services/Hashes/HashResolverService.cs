using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LeagueToolkit.Hashing;
using AssetsManager.Utils;
using AssetsManager.Services.Core;

namespace AssetsManager.Services.Hashes
{
    public class HashResolverService
    {
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

        public Task StartupTask { get; private set; }

        public HashResolverService(DirectoriesCreator directoriesCreator, LogService logService)
        {
            _directoriesCreator = directoriesCreator;
            _logService = logService;
            StartupTask = LoadAllHashesOnStartupAsync();
        }

        private async Task LoadAllHashesOnStartupAsync()
        {
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

            _hashToPathMap.Clear();
            var newHashesDir = _directoriesCreator.HashesNewPath;
            var gameHashesFile = Path.Combine(newHashesDir, "hashes.game.txt");
            var lcuHashesFile = Path.Combine(newHashesDir, "hashes.lcu.txt");
            await LoadHashesFromFile(gameHashesFile, _hashToPathMap, text => (ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out ulong hash), hash));
            await LoadHashesFromFile(lcuHashesFile, _hashToPathMap, text => (ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out ulong hash), hash));
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
            var binHashesDir = _directoriesCreator.HashesNewPath;
            var binHashesFile = Path.Combine(binHashesDir, "hashes.binhashes.txt");
            var binEntriesFile = Path.Combine(binHashesDir, "hashes.binentries.txt");
            var binFieldsFile = Path.Combine(binHashesDir, "hashes.binfields.txt");
            var binTypesFile = Path.Combine(binHashesDir, "hashes.bintypes.txt");
            await LoadHashesFromFile(binHashesFile, _binHashesMap, text => (uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out uint hash), hash));
            await LoadHashesFromFile(binEntriesFile, _binEntriesMap, text => (uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out uint hash), hash));
            await LoadHashesFromFile(binFieldsFile, _binFieldsMap, text => (uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out uint hash), hash));
            await LoadHashesFromFile(binTypesFile, _binTypesMap, text => (uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out uint hash), hash));
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
                var lines = File.ReadLines(filePath);
                foreach (var line in lines)
                {
                    var parts = line.Trim().Split(' ');
                    if (parts.Length == 2)
                    {
                        var (success, hash) = parser(parts[0]);
                        if (success)
                        {
                            var path = parts[1];
                            map[hash] = path;
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

            await LoadRstXxh3HashesAsync();
            await LoadRstXxh64HashesAsync();
            _rstHashesLoaded = true;
        }

        public async Task LoadRstXxh3HashesAsync()
        {
            _rstXxh3HashesMap.Clear();
            var rstHashesDir = _directoriesCreator.HashesNewPath;
            var rstXxh3HashesFile = Path.Combine(rstHashesDir, "hashes.rst.xxh3.txt");
            await LoadHashesFromFile(rstXxh3HashesFile, _rstXxh3HashesMap, text => (ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out ulong hash), hash));
        }

        public async Task LoadRstXxh64HashesAsync()
        {
            _rstXxh64HashesMap.Clear();
            var rstHashesDir = _directoriesCreator.HashesNewPath;
            var rstXxh64HashesFile = Path.Combine(rstHashesDir, "hashes.rst.xxh64.txt");
            await LoadHashesFromFile(rstXxh64HashesFile, _rstXxh64HashesMap, text => (ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out ulong hash), hash));
        }
        public async Task ForceReloadHashesAsync()
        {
            _gameLcuHashesLoaded = false;
            _binHashesLoaded = false;
            _rstHashesLoaded = false;
            StartupTask = LoadAllHashesOnStartupAsync();
            await StartupTask;
        }
    }
}
