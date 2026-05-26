using System;
using System.IO;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
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
        public Dictionary<string, long> HashesSizes { get; set; }
        public AudioExportFormat AudioExportFormat { get; set; } = AudioExportFormat.Ogg;
        public ImageExportFormat ImageExportFormat { get; set; } = ImageExportFormat.Original;
        public DataExportFormat DataExportFormat { get; set; } = DataExportFormat.Original;

        // New structure for monitored assets (Local WADs/Plugins)
        public List<MonitoredAsset> MonitoredAssets { get; set; }
        public List<HistoryEntry> DiffHistory { get; set; }
        public Dictionary<string, long> AssetTrackerProgress { get; set; }
        public Dictionary<string, List<long>> AssetTrackerFailedIds { get; set; }
        public Dictionary<string, List<long>> AssetTrackerFoundIds { get; set; }
        public Dictionary<string, Dictionary<long, string>> AssetTrackerUrlOverrides { get; set; }

        public Dictionary<string, List<long>> AssetTrackerUserRemovedIds { get; set; }
        public List<string> FavoritePaths { get; set; }
        public List<string> SearchHistory { get; set; }
        public List<AudioPlaylistPack> AudioPlaylists { get; set; }

        public ApiSettings ApiSettings { get; set; }

        public event EventHandler ConfigurationSaved;

        private const string ConfigFilePath = "config.json";
        private static readonly object _saveLock = new object();

        public void Save()
        {
            SaveSettings(this);
            ConfigurationSaved?.Invoke(this, EventArgs.Empty);
        }

        public static AppSettings LoadSettings()
        {
            lock (_saveLock)
            {
                if (!File.Exists(ConfigFilePath))
                {
                    var defaultSettings = GetDefaultSettings();
                    SaveSettings(defaultSettings);
                    return defaultSettings;
                }

                var json = File.ReadAllText(ConfigFilePath);
                var settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? GetDefaultSettings();

                var jObject = JObject.Parse(json);
                bool needsResave = false;

                if (needsResave)
                {
                    SaveSettings(settings);
                }

                settings.MonitoredAssets ??= new List<MonitoredAsset>();
                settings.DiffHistory ??= new List<HistoryEntry>();
                settings.AssetTrackerProgress ??= new Dictionary<string, long>();
                settings.AssetTrackerFailedIds ??= new Dictionary<string, List<long>>();
                settings.AssetTrackerFoundIds ??= new Dictionary<string, List<long>>();
                settings.AssetTrackerUrlOverrides ??= new Dictionary<string, Dictionary<long, string>>();
                settings.AssetTrackerUserRemovedIds ??= new Dictionary<string, List<long>>();
                settings.FavoritePaths ??= new List<string>();
                settings.SearchHistory ??= new List<string>();
                settings.AudioPlaylists ??= new List<AudioPlaylistPack>();

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
                    SaveSettings(settings);
                }

                return settings;
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
                HashesSizes = new Dictionary<string, long>(),
                MonitoredAssets = new List<MonitoredAsset>(),
                DiffHistory = new List<HistoryEntry>(),
                AssetTrackerProgress = new Dictionary<string, long>(),
                AssetTrackerFailedIds = new Dictionary<string, List<long>>(),
                AssetTrackerFoundIds = new Dictionary<string, List<long>>(),
                AssetTrackerUrlOverrides = new Dictionary<string, Dictionary<long, string>>(),
                AssetTrackerUserRemovedIds = new Dictionary<string, List<long>>(),
                FavoritePaths = new List<string>(),
                SearchHistory = new List<string>(),
                AudioPlaylists = new List<AudioPlaylistPack>(),
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
            lock (_saveLock)
            {
                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(ConfigFilePath, json);
            }
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
}
