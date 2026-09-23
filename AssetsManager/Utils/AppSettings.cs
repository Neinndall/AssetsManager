using System;
using System.IO;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Audio;

namespace AssetsManager.Utils
{
    public class AppSettings : INotifyPropertyChanged
    {
        public bool SyncHashesWithCDTB { get; set; }
        public bool EnableExtraction { get; set; } 
        public bool OrganizeExtractedAssets { get; set; }
        public ReportGenerationSettings ReportGeneration { get; set; } = new();
        public StudioParametersSettings StudioParameters { get; set; } = new();
        public VfxStudioSettings VfxStudio { get; set; } = new();
        public bool AssetWatcherUpdates { get; set; }
        public bool AssetTrackerTimer { get; set; }
        public bool SaveJsonHistory { get; set; }
        public bool SaveWadComparisonHistory { get; set; }
        public bool BackgroundUpdates { get; set; }
        public bool CheckPbeStatus { get; set; }
        public bool NewsUpdates { get; set; }
        public bool MinimizeToTrayOnClose { get; set; }

        public int UpdateCheckFrequency { get; set; }
        public int AssetTrackerFrequency { get; set; }
        public int PbeStatusFrequency { get; set; }
        public int NewsUpdateFrequency { get; set; }

        public string LolPbeDirectory { get; set; }
        public string LolLiveDirectory { get; set; }
        public string DefaultExtractedSelectDirectory { get; set; }
        public string LastPbeStatusMessage { get; set; }
        public string LastPbeCheckTime { get; set; }
        public PreferredClient PreferredClient { get; set; } = PreferredClient.PBE;
        public PreferredDirectory PreferredDirectory { get; set; } = PreferredDirectory.All;
        private string _customGroundLogoPath = string.Empty;
        private double _groundLogoScale = 1.0;
        private double _groundLogoOpacity = 1.0;

        public string CustomGroundLogoPath
        {
            get => _customGroundLogoPath;
            set => SetGroundLogoProperty(ref _customGroundLogoPath, value);
        }

        public double GroundLogoScale
        {
            get => _groundLogoScale;
            set => SetGroundLogoProperty(ref _groundLogoScale, value);
        }

        public double GroundLogoOpacity
        {
            get => _groundLogoOpacity;
            set => SetGroundLogoProperty(ref _groundLogoOpacity, value);
        }

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

        private ConcurrentDictionary<string, List<long>> _assetTrackerUserRemovedIds = new ConcurrentDictionary<string, List<long>>();
        public IDictionary<string, List<long>> AssetTrackerUserRemovedIds
        {
            get => _assetTrackerUserRemovedIds;
            set => _assetTrackerUserRemovedIds = WrapDictionary(value);
        }

        private ConcurrentDictionary<string, Dictionary<long, AssetTrackerEntry>> _assetTrackerEntries = new ConcurrentDictionary<string, Dictionary<long, AssetTrackerEntry>>();
        public IDictionary<string, Dictionary<long, AssetTrackerEntry>> AssetTrackerEntries
        {
            get => _assetTrackerEntries;
            set => _assetTrackerEntries = WrapDictionary(value);
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

        public ApiSettings ApiSettings { get; set; } = new ApiSettings();

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler ConfigurationSaved;

        private void SetGroundLogoProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

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
                var jsonObject = JObject.Parse(json);
                var settings = jsonObject.ToObject<AppSettings>() ?? GetDefaultSettings();

                bool needsResave = false;

                // Preserve Viewer environment preferences across older config layouts.
                if (jsonObject["StudioParameters"] == null)
                {
                    if (jsonObject["ViewerEnvironment"] is JObject viewerEnvironmentJson)
                    {
                        settings.StudioParameters = new StudioParametersSettings
                        {
                            GroundVisible = viewerEnvironmentJson.Value<bool?>("GroundVisible") ?? false,
                            GridVisible = viewerEnvironmentJson.Value<bool?>("GridVisible") ?? true,
                            SkyboxVisible = viewerEnvironmentJson.Value<bool?>("SkyboxVisible") ?? false,
                            TransparentBackground = viewerEnvironmentJson.Value<bool?>("TransparentBackground") ?? false
                        };
                        needsResave = true;
                    }
                    else if (jsonObject["StudioGroundVisible"] != null ||
                             jsonObject["StudioGridVisible"] != null ||
                             jsonObject["StudioSkyboxVisible"] != null ||
                             jsonObject["StudioTransparentBackground"] != null)
                    {
                        settings.StudioParameters = new StudioParametersSettings
                        {
                            GroundVisible = jsonObject.Value<bool?>("StudioGroundVisible") ?? false,
                            GridVisible = jsonObject.Value<bool?>("StudioGridVisible") ?? true,
                            SkyboxVisible = jsonObject.Value<bool?>("StudioSkyboxVisible") ?? false,
                            TransparentBackground = jsonObject.Value<bool?>("StudioTransparentBackground") ?? false
                        };
                        needsResave = true;
                    }
                }

                // Ground, Grid and mesh display are shared Viewer preferences. VFX-specific state
                // retains only controls that do not exist in the normal Viewer.
                if (jsonObject["VfxStudio"] == null && jsonObject["StudioParameters"] is JObject studioJson &&
                    (studioJson["VfxCameraPreset"] != null || studioJson["VfxWireframeMode"] != null))
                {
                    string legacyWire = studioJson.Value<string>("VfxWireframeMode") ?? "Off";
                    settings.VfxStudio = new VfxStudioSettings
                    {
                        CameraPreset = studioJson.Value<string>("VfxCameraPreset") ?? "Game"
                    };
                    settings.StudioParameters ??= new StudioParametersSettings();
                    settings.StudioParameters.ViewMode = LegacyVfxViewMode(legacyWire);
                    settings.StudioParameters.WireOverlay = string.Equals(legacyWire, "Overlay", StringComparison.OrdinalIgnoreCase);
                    needsResave = true;
                }
                else if (jsonObject["VfxStudio"] is JObject vfxStudioJson &&
                         vfxStudioJson["ViewMode"] == null &&
                         vfxStudioJson["WireframeMode"] != null)
                {
                    string legacyWire = vfxStudioJson.Value<string>("WireframeMode") ?? "Off";
                    settings.StudioParameters ??= new StudioParametersSettings();
                    settings.StudioParameters.ViewMode = LegacyVfxViewMode(legacyWire);
                    settings.StudioParameters.WireOverlay = string.Equals(legacyWire, "Overlay", StringComparison.OrdinalIgnoreCase);
                    needsResave = true;
                }

                settings.StudioParameters ??= GetDefaultSettings().StudioParameters;
                settings.VfxStudio ??= GetDefaultSettings().VfxStudio;

                // ViewMode/WireOverlay lived under VfxStudio until the normal Viewer gained the
                // same controls. Migrate them once so Load Project, Chroma Library and VFX Studio
                // all read one authoritative display state. Shaders intentionally defaults off,
                // matching the reference viewport's previewShaders default.
                JObject sharedDisplayJson = jsonObject["StudioParameters"] as JObject;
                JObject previousVfxJson = jsonObject["VfxStudio"] as JObject;
                if (sharedDisplayJson?["ViewMode"] == null)
                {
                    string previousViewMode = previousVfxJson?.Value<string>("ViewMode");
                    if (!string.IsNullOrWhiteSpace(previousViewMode))
                        settings.StudioParameters.ViewMode = previousViewMode;
                    needsResave = true;
                }
                if (sharedDisplayJson?["WireOverlay"] == null)
                {
                    bool? previousWireOverlay = previousVfxJson?.Value<bool?>("WireOverlay");
                    if (previousWireOverlay.HasValue)
                        settings.StudioParameters.WireOverlay = previousWireOverlay.Value;
                    needsResave = true;
                }
                if (sharedDisplayJson?["ShadersEnabled"] == null)
                {
                    settings.StudioParameters.ShadersEnabled = false;
                    needsResave = true;
                }
                settings.MonitoredAssets ??= new SafeList<MonitoredAsset>();
                settings.DiffHistory ??= new SafeList<HistoryEntry>();
                settings.AssetTrackerUserRemovedIds ??= new ConcurrentDictionary<string, List<long>>();
                settings.AssetTrackerEntries ??= new ConcurrentDictionary<string, Dictionary<long, AssetTrackerEntry>>();
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

        private static string LegacyVfxViewMode(string legacyWireframeMode) =>
            legacyWireframeMode?.ToLowerInvariant() switch
            {
                "only" => "Wireframe",
                _ => "Lit"
            };

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
                NewsUpdates = false,
                MinimizeToTrayOnClose = false,
                UpdateCheckFrequency = 10,
                AssetTrackerFrequency = 60,
                PbeStatusFrequency = 10,
                NewsUpdateFrequency = 30,
                LolPbeDirectory = null,
                LolLiveDirectory = null,
                DefaultExtractedSelectDirectory = null,
                CustomGroundLogoPath = null,
                GroundLogoScale = 1.0,
                GroundLogoOpacity = 1.0,
                StudioParameters = new StudioParametersSettings
                {
                    GroundVisible = false,
                    GridVisible = true,
                    SkyboxVisible = false,
                    TransparentBackground = false,
                    ViewMode = "Lit",
                    WireOverlay = false,
                    ShadersEnabled = false
                },
                VfxStudio = new VfxStudioSettings
                {
                    CameraPreset = "Game",
                    StageVisible = false
                },
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
                AssetTrackerUserRemovedIds = new ConcurrentDictionary<string, List<long>>(),
                AssetTrackerEntries = new ConcurrentDictionary<string, Dictionary<long, AssetTrackerEntry>>(),
                FavoritePaths = new SafeList<string>(),
                SearchHistory = new SafeList<string>(),
                AudioPlaylists = new SafeList<AudioPlaylistPack>(),
                ApiSettings = new ApiSettings
                {
                    Connection = new ConnectionInfo(),
                    Token = new TokenInfo(),
                    ClientTarget = ApiClientTarget.PBE,
                    OfflineCachePersistence = true
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
            CustomGroundLogoPath = defaultSettings.CustomGroundLogoPath;
            GroundLogoScale = defaultSettings.GroundLogoScale;
            GroundLogoOpacity = defaultSettings.GroundLogoOpacity;
            StudioParameters = defaultSettings.StudioParameters;
            VfxStudio = defaultSettings.VfxStudio;
            AudioExportFormat = defaultSettings.AudioExportFormat;
            ImageExportFormat = defaultSettings.ImageExportFormat;
            SaveJsonHistory = defaultSettings.SaveJsonHistory;
            SaveWadComparisonHistory = defaultSettings.SaveWadComparisonHistory;
            BackgroundUpdates = defaultSettings.BackgroundUpdates;
            CheckPbeStatus = defaultSettings.CheckPbeStatus;
            NewsUpdates = defaultSettings.NewsUpdates;
            MinimizeToTrayOnClose = defaultSettings.MinimizeToTrayOnClose;
            LastPbeStatusMessage = defaultSettings.LastPbeStatusMessage;
            PreferredClient = defaultSettings.PreferredClient;
            PreferredDirectory = defaultSettings.PreferredDirectory;
            UpdateCheckFrequency = defaultSettings.UpdateCheckFrequency;
            PbeStatusFrequency = defaultSettings.PbeStatusFrequency;
            NewsUpdateFrequency = defaultSettings.NewsUpdateFrequency;
            MonitoredAssets = defaultSettings.MonitoredAssets;
            DiffHistory = defaultSettings.DiffHistory;
            AssetTrackerTimer = defaultSettings.AssetTrackerTimer;
            AssetTrackerFrequency = defaultSettings.AssetTrackerFrequency;
            AssetTrackerUserRemovedIds = defaultSettings.AssetTrackerUserRemovedIds;
            AssetTrackerEntries = defaultSettings.AssetTrackerEntries;
            FavoritePaths = defaultSettings.FavoritePaths;
            SearchHistory = defaultSettings.SearchHistory;
            AudioPlaylists = defaultSettings.AudioPlaylists;
            ApiSettings = defaultSettings.ApiSettings;
            SyncHashesWithCDTB = defaultSettings.SyncHashesWithCDTB;
            // HashesSizes is intentionally not reset to preserve local cache state.
        }
    }

    public class StudioParametersSettings
    {
        public bool GroundVisible { get; set; }
        public bool GridVisible { get; set; } = true;
        public bool SkyboxVisible { get; set; }
        public bool TransparentBackground { get; set; }
        public string ViewMode { get; set; } = "Lit";
        public bool WireOverlay { get; set; }
        public bool ShadersEnabled { get; set; }
    }

    public class VfxStudioSettings
    {
        public string CameraPreset { get; set; } = "Game";
        public bool StageVisible { get; set; }
    }

    public class ReportGenerationSettings
    {
        public bool Enabled { get; set; }
        public bool FilterNew { get; set; }
        public bool FilterModified { get; set; }
        public bool FilterRenamed { get; set; }
        public bool FilterRemoved { get; set; }
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
