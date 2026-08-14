using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;

using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Shared;
using AssetsManager.Views.Models.Versions;

namespace AssetsManager.Views.Models.Versions
{
    public class TargetInstallationOption
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Path { get; set; }
        public bool IsMain { get; set; }
        public bool IsPbe { get; set; }
        public string Version { get; set; }
        public DateTime? CreationDate { get; set; }

        public string TargetSummary => $"Target: {DisplayName} ({(IsMain ? "MAIN" : "BACKUP")})";

        public override string ToString() => DisplayName;
    }

    public class ManageVersions : INotifyPropertyChanged
    {
        private readonly VersionService _versionService;
        private readonly LogService _logService;

        public List<VersionFileInfo> AllLeagueClientVersions { get; private set; }
        public List<VersionFileInfo> AllLoLGameClientVersions { get; private set; }

        public event PropertyChangedEventHandler PropertyChanged;

        public PaginationModel<VersionFileInfo> LeagueClientPaginator { get; }
        public PaginationModel<VersionFileInfo> LoLGameClientPaginator { get; }

        private IPaginationModel _paginator;
        public IPaginationModel Paginator
        {
            get => _paginator;
            private set
            {
                _paginator = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<LocaleOption> AvailableLocales { get; set; }
        public ObservableCollection<TargetInstallationOption> TargetInstallations { get; } = new();

        private TargetInstallationOption _selectedTargetInstallation;
        public TargetInstallationOption SelectedTargetInstallation
        {
            get => _selectedTargetInstallation;
            set
            {
                if (_selectedTargetInstallation != value)
                {
                    _selectedTargetInstallation = value;
                    OnPropertyChanged();
                }
            }
        }

        public ManageVersions(VersionService versionService, LogService logService)
        {
            _versionService = versionService;
            _logService = logService;

            AllLeagueClientVersions = new List<VersionFileInfo>();
            AllLoLGameClientVersions = new List<VersionFileInfo>();

            LeagueClientPaginator = new PaginationModel<VersionFileInfo>();
            LoLGameClientPaginator = new PaginationModel<VersionFileInfo>();

            _paginator = LeagueClientPaginator;

            AvailableLocales = new ObservableCollection<LocaleOption>
            {
                new LocaleOption { Code = "es_ES", IsSelected = false },
                new LocaleOption { Code = "es_MX", IsSelected = false },
                new LocaleOption { Code = "en_US", IsSelected = false },
                new LocaleOption { Code = "tr_TR", IsSelected = false }
            };
        }

        public void SetActiveTab(bool isGame)
        {
            Paginator = isGame ? (IPaginationModel)LoLGameClientPaginator : (IPaginationModel)LeagueClientPaginator;
        }

        public async Task LoadVersionFilesAsync(bool preservePage = false)
        {
            if (_versionService != null)
            {
                var allFiles = await _versionService.GetVersionFilesAsync();
                var sortedFiles = allFiles
                    .OrderByDescending(f => FormatUtils.ParseDate(f.Date, "dd/MM/yyyy HH:mm:ss"))
                    .ThenBy(f => f.FileName)
                    .ToList();

                AllLeagueClientVersions = sortedFiles.Where(f => f.Category == "league-client").ToList();
                var gameClientCategories = new[] { "lol-game-client" };
                AllLoLGameClientVersions = sortedFiles.Where(f => gameClientCategories.Contains(f.Category)).ToList();

                LeagueClientPaginator.SetFullList(AllLeagueClientVersions, preservePage);
                LoLGameClientPaginator.SetFullList(AllLoLGameClientVersions, preservePage);
            }
        }

        public void MarkNewFiles(ISet<string> knownFileNames)
        {
            var allFiles = AllLeagueClientVersions.Concat(AllLoLGameClientVersions);
            foreach (var file in allFiles)
            {
                file.IsNew = !knownFileNames.Contains(file.FileName);
            }
        }

        public async Task LoadTargetInstallationsAsync(BackupManager backupManager, AppSettings appSettings)
        {
            var previousSelectedPath = SelectedTargetInstallation?.Path;
            var preferredClient = appSettings?.PreferredClient ?? PreferredClient.PBE;
            var newInstallations = new List<TargetInstallationOption>();

            if (appSettings != null)
            {
                if (preferredClient == PreferredClient.PBE && !string.IsNullOrWhiteSpace(appSettings.LolPbeDirectory) && System.IO.Directory.Exists(appSettings.LolPbeDirectory))
                {
                    string pbeVer = _versionService != null ? await _versionService.GetGameVersionAsync(appSettings.LolPbeDirectory) : null;
                    DateTime? lastWriteTime = System.IO.Directory.GetLastWriteTime(appSettings.LolPbeDirectory);

                    newInstallations.Add(new TargetInstallationOption
                    {
                        Name = "League of Legends PBE",
                        DisplayName = "League of Legends PBE",
                        Path = appSettings.LolPbeDirectory,
                        IsMain = true,
                        IsPbe = true,
                        Version = !string.IsNullOrEmpty(pbeVer) ? $"v{pbeVer}" : "Active PBE",
                        CreationDate = lastWriteTime
                    });
                }
                else if (preferredClient == PreferredClient.LIVE && !string.IsNullOrWhiteSpace(appSettings.LolLiveDirectory) && System.IO.Directory.Exists(appSettings.LolLiveDirectory))
                {
                    string liveVer = _versionService != null ? await _versionService.GetGameVersionAsync(appSettings.LolLiveDirectory) : null;
                    DateTime? lastWriteTime = System.IO.Directory.GetLastWriteTime(appSettings.LolLiveDirectory);

                    newInstallations.Add(new TargetInstallationOption
                    {
                        Name = "League of Legends LIVE",
                        DisplayName = "League of Legends LIVE",
                        Path = appSettings.LolLiveDirectory,
                        IsMain = true,
                        IsPbe = false,
                        Version = !string.IsNullOrEmpty(liveVer) ? $"v{liveVer}" : "Active LIVE",
                        CreationDate = lastWriteTime
                    });
                }
            }

            if (backupManager != null)
            {
                try
                {
                    var backups = await backupManager.GetBackupsAsync(includeStorageMetrics: false, client: preferredClient);
                    foreach (var backup in backups)
                    {
                        if (backup.IsMainClient) continue;

                        newInstallations.Add(new TargetInstallationOption
                        {
                            Name = backup.Name,
                            DisplayName = backup.DisplayName,
                            Path = backup.Path,
                            IsMain = false,
                            IsPbe = backup.IsPbe,
                            Version = !string.IsNullOrEmpty(backup.Version) ? $"v{backup.Version}" : "Backup",
                            CreationDate = backup.CreationDate
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logService?.LogError(ex, "Failed to load backup installations for version management dropdown.");
                }
            }

            TargetInstallations.Clear();
            foreach (var item in newInstallations)
            {
                TargetInstallations.Add(item);
            }

            if (TargetInstallations.Count > 0)
            {
                SelectedTargetInstallation = TargetInstallations.FirstOrDefault(t => t.Path != null && t.Path.Equals(previousSelectedPath, StringComparison.OrdinalIgnoreCase))
                                           ?? TargetInstallations.FirstOrDefault(t => t.IsMain)
                                           ?? TargetInstallations.FirstOrDefault();
            }
            else
            {
                SelectedTargetInstallation = null;
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
