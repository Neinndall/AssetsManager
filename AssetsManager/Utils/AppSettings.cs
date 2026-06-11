using System;
using System.IO;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Views.Models.Shared;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Audio;

namespace AssetsManager.Utils
{
    public class AppSettings
    {
        public bool SyncHashesWithCDTB { get; set; }
        public bool EnableExtraction { get; set; } 
        public bool OrganizeExtractedAssets { get; set; }
        public ReportGenerationSettings ReportGeneration { get; set; }
        public bool AssetWatcherUpdates { get; set; }
        public bool AssetTrackerTimer { get; set; }
        public bool SaveJsonHistory { get; set; }
        public bool SaveWadComparisonHistory { get; set; }
        public bool BackgroundUpdates { get; set; }
        public bool CheckPbeStatus { get; set; }
        public bool MinimizeToTrayOnClose { get; set; }

        public int UpdateCheckFrequency { get; set; }
        public int AssetTrackerFrequency { get; set; }
        public int PbeStatusFrequency { get; set; }

        public string LolPbeDirectory { get; set; }
        public string LolLiveDirectory { get; set; }
        public string DefaultExtractedSelectDirectory { get; set; }
        public string LastPbeStatusMessage { get; set; }
        public string LastPbeCheckTime { get; set; }
        public PreferredClient PreferredClient { get; set; } = PreferredClient.PBE;
        public PreferredDirectory PreferredDirectory { get; set; } = PreferredDirectory.All;
        public string CustomFloorTexturePath { get; set; } = string.Empty;

        private static IList<T> WrapList<T>(IList<T> value) =>
            value is SafeList<T> sl ? sl : new SafeList<T>(value ?? new List<T>());

        private static ConcurrentDictionary<TKey, TValue> WrapDictionary<TKey, TValue>(IDictionary<TKey, TValue> value) =>
            value is ConcurrentDictionary<TKey, TValue> cd ? cd : new ConcurrentDictionary<TKey, TValue>(value ?? new Dictionary<TKey, TValue>());

        private ConcurrentDictionary<string, long> _hashesSizes = new ConcurrentDictionary<string, long>();
        public IDictionary<string, long> HashesSizes
        {
            get => _hashesSizes;
            set => _hashesSizes = WrapDictionary(value);
        }

        public AudioExportFormat AudioExportFormat { get; set; } = AudioExportFormat.Ogg;
        public ImageExportFormat ImageExportFormat { get; set; } = ImageExportFormat.Original;
        public DataExportFormat DataExportFormat { get; set; } = DataExportFormat.Original;

        // Diagnostic: writes a per-file audit log after each comparison so the
        // user can verify that the New/Modified/Removed/Renamed classification
        // is correct (Checksum + size + compression per file).
        public bool VerboseComparisonLog { get; set; } = true;

        // New structure for monitored assets (Local WADs/Plugins)
        private IList<MonitoredAsset> _monitoredAssets = new SafeList<MonitoredAsset>();
        public IList<MonitoredAsset> MonitoredAssets
        {
            get => _monitoredAssets;
            set => _monitoredAssets = WrapList(value);
        }

        private IList<HistoryEntry> _diffHistory = new SafeList<HistoryEntry>();
        public IList<HistoryEntry> DiffHistory
        {
            get => _diffHistory;
            set => _diffHistory = WrapList(value);
        }

        private ConcurrentDictionary<string, long> _assetTrackerProgress = new ConcurrentDictionary<string, long>();
        public IDictionary<string, long> AssetTrackerProgress
        {
            get => _assetTrackerProgress;
            set => _assetTrackerProgress = WrapDictionary(value);
        }

        private ConcurrentDictionary<string, List<long>> _assetTrackerFailedIds = new ConcurrentDictionary<string, List<long>>();
        public IDictionary<string, List<long>> AssetTrackerFailedIds
        {
            get => _assetTrackerFailedIds;
            set => _assetTrackerFailedIds = WrapDictionary(value);
        }

        private ConcurrentDictionary<string, List<long>> _assetTrackerFoundIds = new ConcurrentDictionary<string, List<long>>();
        public IDictionary<string, List<long>> AssetTrackerFoundIds
        {
            get => _assetTrackerFoundIds;
            set => _assetTrackerFoundIds = WrapDictionary(value);
        }

        private ConcurrentDictionary<string, Dictionary<long, string>> _assetTrackerUrlOverrides = new ConcurrentDictionary<string, Dictionary<long, string>>();
        public IDictionary<string, Dictionary<long, string>> AssetTrackerUrlOverrides
        {
            get => _assetTrackerUrlOverrides;
            set => _assetTrackerUrlOverrides = WrapDictionary(value);
        }

        private ConcurrentDictionary<string, List<long>> _assetTrackerUserRemovedIds = new ConcurrentDictionary<string, List<long>>();
        public IDictionary<string, List<long>> AssetTrackerUserRemovedIds
        {
            get => _assetTrackerUserRemovedIds;
            set => _assetTrackerUserRemovedIds = WrapDictionary(value);
        }

        private IList<string> _favoritePaths = new SafeList<string>();
        public IList<string> FavoritePaths
        {
            get => _favoritePaths;
            set => _favoritePaths = WrapList(value);
        }

        private IList<string> _searchHistory = new SafeList<string>();
        public IList<string> SearchHistory
        {
            get => _searchHistory;
            set => _searchHistory = WrapList(value);
        }

        private IList<AudioPlaylistPack> _audioPlaylists = new SafeList<AudioPlaylistPack>();
        public IList<AudioPlaylistPack> AudioPlaylists
        {
            get => _audioPlaylists;
            set => _audioPlaylists = WrapList(value);
        }

        public ApiSettings ApiSettings { get; set; }

        public event EventHandler ConfigurationSaved;

        private const string ConfigFilePath = "config.json";
        private static readonly SemaphoreSlim _saveSemaphore = new SemaphoreSlim(1, 1);

        private void SaveInternal()
        {
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(ConfigFilePath, json);
        }

        private async Task SaveInternalAsync()
        {
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            await File.WriteAllTextAsync(ConfigFilePath, json);
        }

        public void Save()
        {
            _saveSemaphore.Wait();
            try
            {
                SaveInternal();
            }
            finally
            {
                _saveSemaphore.Release();
            }
            ConfigurationSaved?.Invoke(this, EventArgs.Empty);
        }

        public async Task SaveAsync()
        {
            await _saveSemaphore.WaitAsync();
            try
            {
                await SaveInternalAsync();
            }
            finally
            {
                _saveSemaphore.Release();
            }
            ConfigurationSaved?.Invoke(this, EventArgs.Empty);
        }

        public static AppSettings LoadSettings()
        {
            _saveSemaphore.Wait();
            try
            {
                if (!File.Exists(ConfigFilePath))
                {
                    var defaultSettings = GetDefaultSettings();
                    defaultSettings.SaveInternal();
                    return defaultSettings;
                }

                var json = File.ReadAllText(ConfigFilePath);
                var settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? GetDefaultSettings();

                bool needsResave = false;

                settings.MonitoredAssets ??= new SafeList<MonitoredAsset>();
                settings.DiffHistory ??= new SafeList<HistoryEntry>();
                settings.AssetTrackerProgress ??= new ConcurrentDictionary<string, long>();
                settings.AssetTrackerFailedIds ??= new ConcurrentDictionary<string, List<long>>();
                settings.AssetTrackerFoundIds ??= new ConcurrentDictionary<string, List<long>>();
                settings.AssetTrackerUrlOverrides ??= new ConcurrentDictionary<string, Dictionary<long, string>>();
                settings.AssetTrackerUserRemovedIds ??= new ConcurrentDictionary<string, List<long>>();
                settings.FavoritePaths ??= new SafeList<string>();
                settings.SearchHistory ??= new SafeList<string>();
                settings.AudioPlaylists ??= new SafeList<AudioPlaylistPack>();

                // Robustly initialize and heal ApiSettings
                if (settings.ApiSettings == null)
                {
                    settings.ApiSettings = GetDefaultSettings().ApiSettings;
                    needsResave = true;
                }
                else
                {
                    settings.ApiSettings.Connection ??= new ConnectionInfo();
                    settings.ApiSettings.Token ??= new TokenInfo();
                }

                if (needsResave)
                {
                    settings.SaveInternal();
                }

                return settings;
            }
            finally
            {
                _saveSemaphore.Release();
            }
        }

        public static AppSettings GetDefaultSettings()
        {
            return new AppSettings
            {
                SyncHashesWithCDTB = true,
                EnableExtraction = false,
                OrganizeExtractedAssets = false,
                ReportGeneration = new ReportGenerationSettings
                {
                    Enabled = false,
                    FilterNew = false,
                    FilterModified = false,
                    FilterRenamed = false,
                    FilterRemoved = false
                },
                AssetWatcherUpdates = false,
                AssetTrackerTimer = false,
                SaveJsonHistory = false,
                SaveWadComparisonHistory = false,
                BackgroundUpdates = false,
                CheckPbeStatus = false,
                MinimizeToTrayOnClose = false,
                UpdateCheckFrequency = 10,
                AssetTrackerFrequency = 60,
                PbeStatusFrequency = 10,
                LolPbeDirectory = null,
                LolLiveDirectory = null,
                DefaultExtractedSelectDirectory = null,
                CustomFloorTexturePath = null,
                AudioExportFormat = AudioExportFormat.Ogg,
                ImageExportFormat = ImageExportFormat.Original,
                DataExportFormat = DataExportFormat.Original,
                LastPbeStatusMessage = null,
                LastPbeCheckTime = null,
                PreferredClient = PreferredClient.PBE,
                PreferredDirectory = PreferredDirectory.All,
                HashesSizes = new ConcurrentDictionary<string, long>(),
                MonitoredAssets = new SafeList<MonitoredAsset>(),
                DiffHistory = new SafeList<HistoryEntry>(),
                AssetTrackerProgress = new ConcurrentDictionary<string, long>(),
                AssetTrackerFailedIds = new ConcurrentDictionary<string, List<long>>(),
                AssetTrackerFoundIds = new ConcurrentDictionary<string, List<long>>(),
                AssetTrackerUrlOverrides = new ConcurrentDictionary<string, Dictionary<long, string>>(),
                AssetTrackerUserRemovedIds = new ConcurrentDictionary<string, List<long>>(),
                FavoritePaths = new SafeList<string>(),
                SearchHistory = new SafeList<string>(),
                AudioPlaylists = new SafeList<AudioPlaylistPack>(),
                ApiSettings = new ApiSettings
                {
                    Connection = new ConnectionInfo(),
                    Token = new TokenInfo(),
                    UsePbeForApi = false
                },
            };
        }

        public static void SaveSettings(AppSettings settings)
        {
            settings.Save();
        }

        public static async Task SaveSettingsAsync(AppSettings settings)
        {
            await settings.SaveAsync();
        }

        public void ResetToDefaults()
        {
            var defaultSettings = GetDefaultSettings();

            AssetWatcherUpdates = defaultSettings.AssetWatcherUpdates;
            EnableExtraction = defaultSettings.EnableExtraction;
            OrganizeExtractedAssets = defaultSettings.OrganizeExtractedAssets;
            ReportGeneration = defaultSettings.ReportGeneration;
            LolPbeDirectory = defaultSettings.LolPbeDirectory;
            LolLiveDirectory = defaultSettings.LolLiveDirectory;
            DefaultExtractedSelectDirectory = defaultSettings.DefaultExtractedSelectDirectory;
            CustomFloorTexturePath = defaultSettings.CustomFloorTexturePath;
            AudioExportFormat = defaultSettings.AudioExportFormat;
            ImageExportFormat = defaultSettings.ImageExportFormat;
            SaveJsonHistory = defaultSettings.SaveJsonHistory;
            SaveWadComparisonHistory = defaultSettings.SaveWadComparisonHistory;
            BackgroundUpdates = defaultSettings.BackgroundUpdates;
            CheckPbeStatus = defaultSettings.CheckPbeStatus;
            MinimizeToTrayOnClose = defaultSettings.MinimizeToTrayOnClose;
            LastPbeStatusMessage = defaultSettings.LastPbeStatusMessage;
            PreferredClient = defaultSettings.PreferredClient;
            PreferredDirectory = defaultSettings.PreferredDirectory;
            UpdateCheckFrequency = defaultSettings.UpdateCheckFrequency;
            PbeStatusFrequency = defaultSettings.PbeStatusFrequency;
            MonitoredAssets = defaultSettings.MonitoredAssets;
            DiffHistory = defaultSettings.DiffHistory;
            AssetTrackerTimer = defaultSettings.AssetTrackerTimer;
            AssetTrackerFrequency = defaultSettings.AssetTrackerFrequency;
            AssetTrackerFoundIds = defaultSettings.AssetTrackerFoundIds;
            AssetTrackerFailedIds = defaultSettings.AssetTrackerFailedIds;
            AssetTrackerProgress = defaultSettings.AssetTrackerProgress;
            AssetTrackerUrlOverrides = defaultSettings.AssetTrackerUrlOverrides;
            AssetTrackerUserRemovedIds = defaultSettings.AssetTrackerUserRemovedIds;
            FavoritePaths = defaultSettings.FavoritePaths;
            SearchHistory = defaultSettings.SearchHistory;
            AudioPlaylists = defaultSettings.AudioPlaylists;
            ApiSettings = defaultSettings.ApiSettings;
            SyncHashesWithCDTB = defaultSettings.SyncHashesWithCDTB;
            // HashesSizes is intentionally not reset to preserve local cache state.
        }
    }

    public class SafeList<T> : IList<T>
    {
        private readonly List<T> _list = new List<T>();
        private readonly object _lock = new object();
        public SafeList() {}
        public SafeList(IEnumerable<T> col) => _list.AddRange(col ?? Enumerable.Empty<T>());
        public T this[int i] { get { lock(_lock) return _list[i]; } set { lock(_lock) _list[i] = value; } }
        public int Count { get { lock(_lock) return _list.Count; } }
        public bool IsReadOnly => false;
        public void Add(T item) { lock(_lock) _list.Add(item); }
        public void Clear() { lock(_lock) _list.Clear(); }
        public bool Contains(T item) { lock(_lock) return _list.Contains(item); }
        public void CopyTo(T[] array, int index) { lock(_lock) _list.CopyTo(array, index); }
        public int IndexOf(T item) { lock(_lock) return _list.IndexOf(item); }
        public void Insert(int index, T item) { lock(_lock) _list.Insert(index, item); }
        public bool Remove(T item) { lock(_lock) return _list.Remove(item); }
        public void RemoveAt(int index) { lock(_lock) _list.RemoveAt(index); }
        public IEnumerator<T> GetEnumerator() { lock(_lock) return new List<T>(_list).GetEnumerator(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
