using System;
using System.IO;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using AssetsManager.Views.Models.Monitor;
using AssetsManager.Views.Models.Shared;
using AssetsManager.Views.Models.Settings;

namespace AssetsManager.Utils
{
    public class AppSettings
    {
        public bool SyncHashesWithCDTB { get; set; }
        public bool EnableExtraction { get; set; } 
        public bool OrganizeExtractedAssets { get; set; }
        public ReportGenerationSettings ReportGeneration { get; set; }
        public bool CheckJsonDataUpdates { get; set; }
        public bool AssetTrackerTimer { get; set; }
        public bool SaveDiffHistory { get; set; }
        public bool BackgroundUpdates { get; set; }
        public bool CheckPbeStatus { get; set; }
        public bool MinimizeToTrayOnClose { get; set; }

        public int UpdateCheckFrequency { get; set; }
        public int AssetTrackerFrequency { get; set; }
        public int PbeStatusFrequency { get; set; }

        public string NewHashesPath { get; set; }
        public string OldHashesPath { get; set; }
        public string LolPbeDirectory { get; set; }
        public string LolLiveDirectory { get; set; }
        public string DefaultExtractedSelectDirectory { get; set; }
        public string LastPbeStatusMessage { get; set; }
        public string LastPbeCheckTime { get; set; }
        public string CustomFloorTexturePath { get; set; } = string.Empty;
        public Dictionary<string, long> HashesSizes { get; set; }
        public AudioExportFormat AudioExportFormat { get; set; } = AudioExportFormat.Ogg;

        // Dictionary for File Watcher
        public Dictionary<string, DateTime> JsonDataModificationDates { get; set; }

        // New structure for monitored files and directories
        public List<string> MonitoredJsonFiles { get; set; }
        public List<JsonDiffHistoryEntry> DiffHistory { get; set; }
        public Dictionary<string, long> AssetTrackerProgress { get; set; }
        public Dictionary<string, List<long>> AssetTrackerFailedIds { get; set; }
        public Dictionary<string, List<long>> AssetTrackerFoundIds { get; set; }
        public Dictionary<string, Dictionary<long, string>> AssetTrackerUrlOverrides { get; set; }

        public Dictionary<string, List<long>> AssetTrackerUserRemovedIds { get; set; }
        public List<string> FavoritePaths { get; set; }

        public ApiSettings ApiSettings { get; set; }

        private const string ConfigFilePath = "config.json";

        public static AppSettings LoadSettings()
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

            if (jObject.TryGetValue("MonitoredJsonFiles", out var monitoredFilesToken) &&
                monitoredFilesToken is JArray monitoredFilesArray)
            {
                var newUrls = new List<string>();
                var newDates = settings.JsonDataModificationDates ?? new Dictionary<string, DateTime>();

                foreach (var token in monitoredFilesArray)
                {
                    if (token is JObject itemObject &&
                        itemObject.TryGetValue("Url", StringComparison.OrdinalIgnoreCase, out var urlToken))
                    {
                        string url = urlToken.ToString();
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            newUrls.Add(url);

                            if (itemObject.TryGetValue("LastUpdated", StringComparison.OrdinalIgnoreCase, out var dateToken) &&
                                DateTime.TryParse(dateToken.ToString(), out var date))
                            {
                                newDates[url] = date;
                            }

                            needsResave = true;
                        }
                    }
                    else if (token.Type == JTokenType.String)
                    {
                        newUrls.Add(token.ToString());
                    }
                }

                settings.MonitoredJsonFiles = newUrls.Distinct().ToList();
                settings.JsonDataModificationDates = newDates;
            }

            if (needsResave)
            {
                SaveSettings(settings);
            }

            settings.MonitoredJsonFiles ??= new List<string>();
            settings.JsonDataModificationDates ??= new Dictionary<string, DateTime>();
            settings.DiffHistory ??= new List<JsonDiffHistoryEntry>();
            settings.AssetTrackerProgress ??= new Dictionary<string, long>();
            settings.AssetTrackerFailedIds ??= new Dictionary<string, List<long>>();
            settings.AssetTrackerFoundIds ??= new Dictionary<string, List<long>>();
            settings.AssetTrackerUrlOverrides ??= new Dictionary<string, Dictionary<long, string>>();
            settings.AssetTrackerUserRemovedIds ??= new Dictionary<string, List<long>>();
            settings.FavoritePaths ??= new List<string>();

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
                CheckJsonDataUpdates = false,
                AssetTrackerTimer = false,
                SaveDiffHistory = false,
                BackgroundUpdates = false,
                CheckPbeStatus = false,
                MinimizeToTrayOnClose = false,
                UpdateCheckFrequency = 10,
                AssetTrackerFrequency = 60,
                PbeStatusFrequency = 10,
                NewHashesPath = null,
                OldHashesPath = null,
                LolPbeDirectory = null,
                LolLiveDirectory = null,
                DefaultExtractedSelectDirectory = null,
                CustomFloorTexturePath = null,
                AudioExportFormat = AudioExportFormat.Ogg,
                LastPbeStatusMessage = null,
                LastPbeCheckTime = null,
                HashesSizes = new Dictionary<string, long>(),
                JsonDataModificationDates = new Dictionary<string, DateTime>(),
                MonitoredJsonFiles = new List<string>(),
                DiffHistory = new List<JsonDiffHistoryEntry>(),
                AssetTrackerProgress = new Dictionary<string, long>(),
                AssetTrackerFailedIds = new Dictionary<string, List<long>>(),
                AssetTrackerFoundIds = new Dictionary<string, List<long>>(),
                AssetTrackerUrlOverrides = new Dictionary<string, Dictionary<long, string>>(),
                AssetTrackerUserRemovedIds = new Dictionary<string, List<long>>(),
                FavoritePaths = new List<string>(),
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
            var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
            File.WriteAllText(ConfigFilePath, json);
        }

        public void ResetToDefaults()
        {
            var defaultSettings = GetDefaultSettings();

            CheckJsonDataUpdates = defaultSettings.CheckJsonDataUpdates;
            EnableExtraction = defaultSettings.EnableExtraction;
            OrganizeExtractedAssets = defaultSettings.OrganizeExtractedAssets;
            ReportGeneration = defaultSettings.ReportGeneration;
            NewHashesPath = defaultSettings.NewHashesPath;
            OldHashesPath = defaultSettings.OldHashesPath;
            LolPbeDirectory = defaultSettings.LolPbeDirectory;
            LolLiveDirectory = defaultSettings.LolLiveDirectory;
            DefaultExtractedSelectDirectory = defaultSettings.DefaultExtractedSelectDirectory;
            CustomFloorTexturePath = defaultSettings.CustomFloorTexturePath;
            AudioExportFormat = defaultSettings.AudioExportFormat;
            SaveDiffHistory = defaultSettings.SaveDiffHistory;
            BackgroundUpdates = defaultSettings.BackgroundUpdates;
            CheckPbeStatus = defaultSettings.CheckPbeStatus;
            MinimizeToTrayOnClose = defaultSettings.MinimizeToTrayOnClose;
            LastPbeStatusMessage = defaultSettings.LastPbeStatusMessage;
            UpdateCheckFrequency = defaultSettings.UpdateCheckFrequency;
            PbeStatusFrequency = defaultSettings.PbeStatusFrequency;
            JsonDataModificationDates = defaultSettings.JsonDataModificationDates;
            MonitoredJsonFiles = defaultSettings.MonitoredJsonFiles;
            DiffHistory = defaultSettings.DiffHistory;
            AssetTrackerTimer = defaultSettings.AssetTrackerTimer;
            AssetTrackerFrequency = defaultSettings.AssetTrackerFrequency;
            AssetTrackerFoundIds = defaultSettings.AssetTrackerFoundIds;
            AssetTrackerFailedIds = defaultSettings.AssetTrackerFailedIds;
            AssetTrackerProgress = defaultSettings.AssetTrackerProgress;
            AssetTrackerUrlOverrides = defaultSettings.AssetTrackerUrlOverrides;
            AssetTrackerUserRemovedIds = defaultSettings.AssetTrackerUserRemovedIds;
            FavoritePaths = defaultSettings.FavoritePaths;
            ApiSettings = defaultSettings.ApiSettings;
            // SyncHashesWithCDTB and HashesSizes are intentionally not reset.
        }
    }
}
