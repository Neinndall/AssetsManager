using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Versions;

namespace AssetsManager.Views.Models
{
    public class ManageVersions : INotifyPropertyChanged
    {
        private readonly VersionService _versionService;
        private readonly LogService _logService;

        public List<VersionFileInfo> AllLeagueClientVersions { get; private set; }
        public List<VersionFileInfo> AllLoLGameClientVersions { get; private set; }

        public event PropertyChangedEventHandler PropertyChanged;

        public PaginationModel<VersionFileInfo> LeagueClientPaginator { get; }
        public PaginationModel<VersionFileInfo> LoLGameClientPaginator { get; }

        public ObservableCollection<LocaleOption> AvailableLocales { get; set; }

        public ManageVersions(VersionService versionService, LogService logService)
        {
            _versionService = versionService;
            _logService = logService;

            AllLeagueClientVersions = new List<VersionFileInfo>();
            AllLoLGameClientVersions = new List<VersionFileInfo>();

            LeagueClientPaginator = new PaginationModel<VersionFileInfo>();
            LoLGameClientPaginator = new PaginationModel<VersionFileInfo>();

            AvailableLocales = new ObservableCollection<LocaleOption>
            {
                new LocaleOption { Code = "es_ES", IsSelected = false },
                new LocaleOption { Code = "es_MX", IsSelected = false },
                new LocaleOption { Code = "en_US", IsSelected = false },
                new LocaleOption { Code = "tr_TR", IsSelected = false }
            };
        }

        public async Task LoadVersionFilesAsync()
        {
            if (_versionService != null)
            {
                var allFiles = await _versionService.GetVersionFilesAsync();

                AllLeagueClientVersions = allFiles.Where(f => f.Category == "league-client").ToList();
                var gameClientCategories = new[] { "lol-game-client" };
                AllLoLGameClientVersions = allFiles.Where(f => gameClientCategories.Contains(f.Category)).ToList();

                LeagueClientPaginator.SetFullList(AllLeagueClientVersions);
                LoLGameClientPaginator.SetFullList(AllLoLGameClientVersions);
            }
        }

        public void DeleteVersions(IEnumerable<VersionFileInfo> versionsToDelete)
        {
            if (versionsToDelete == null || !versionsToDelete.Any()) return;

            if (_versionService.DeleteVersionFiles(versionsToDelete))
            {
                foreach (var versionFile in versionsToDelete.ToList())
                {
                    AllLeagueClientVersions.Remove(versionFile);
                    AllLoLGameClientVersions.Remove(versionFile);
                }
                // Recalculate total pages and update views after deletion
                LeagueClientPaginator.SetFullList(AllLeagueClientVersions);
                LoLGameClientPaginator.SetFullList(AllLoLGameClientVersions);
            }
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
