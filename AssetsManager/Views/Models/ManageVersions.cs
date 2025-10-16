using AssetsManager.Services.Core;
using AssetsManager.Services.Versions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace AssetsManager.Views.Models
{
    public class ManageVersions : INotifyPropertyChanged
    {
        private readonly VersionService _versionService;
        private readonly LogService _logService;
        private const int PageSize = 10;

        private List<VersionFileInfo> _allLeagueClientVersions;
        private List<VersionFileInfo> _allLoLGameClientVersions;

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<VersionFileInfo> LeagueClientVersions { get; set; }
        public ObservableCollection<VersionFileInfo> LoLGameClientVersions { get; set; }

        private int _leagueClientCurrentPage = 1;
        public int LeagueClientCurrentPage
        {
            get => _leagueClientCurrentPage;
            set
            {
                if (_leagueClientCurrentPage != value)
                {
                    _leagueClientCurrentPage = value;
                    OnPropertyChanged(nameof(LeagueClientCurrentPage));
                    OnPropertyChanged(nameof(CanGoToPrevLeagueClientPage));
                    OnPropertyChanged(nameof(CanGoToNextLeagueClientPage));
                    UpdateLeagueClientPagedView();
                }
            }
        }

        private int _leagueClientTotalPages;
        public int LeagueClientTotalPages
        {
            get => _leagueClientTotalPages;
            private set
            {
                if (_leagueClientTotalPages != value)
                {
                    _leagueClientTotalPages = value;
                    OnPropertyChanged(nameof(LeagueClientTotalPages));
                    OnPropertyChanged(nameof(CanGoToPrevLeagueClientPage));
                    OnPropertyChanged(nameof(CanGoToNextLeagueClientPage));
                }
            }
        }

        public bool CanGoToPrevLeagueClientPage => LeagueClientCurrentPage > 1;
        public bool CanGoToNextLeagueClientPage => LeagueClientCurrentPage < LeagueClientTotalPages;

        private int _loLGameClientCurrentPage = 1;
        public int LoLGameClientCurrentPage
        {
            get => _loLGameClientCurrentPage;
            set
            {
                if (_loLGameClientCurrentPage != value)
                {
                    _loLGameClientCurrentPage = value;
                    OnPropertyChanged(nameof(LoLGameClientCurrentPage));
                    OnPropertyChanged(nameof(CanGoToPrevLoLGameClientPage));
                    OnPropertyChanged(nameof(CanGoToNextLoLGameClientPage));
                    UpdateLoLGameClientPagedView();
                }
            }
        }

        private int _loLGameClientTotalPages;
        public int LoLGameClientTotalPages
        {
            get => _loLGameClientTotalPages;
            private set
            {
                if (_loLGameClientTotalPages != value)
                {
                    _loLGameClientTotalPages = value;
                    OnPropertyChanged(nameof(LoLGameClientTotalPages));
                    OnPropertyChanged(nameof(CanGoToPrevLoLGameClientPage));
                    OnPropertyChanged(nameof(CanGoToNextLoLGameClientPage));
                }
            }
        }

        public bool CanGoToPrevLoLGameClientPage => LoLGameClientCurrentPage > 1;
        public bool CanGoToNextLoLGameClientPage => LoLGameClientCurrentPage < LoLGameClientTotalPages;

        public ObservableCollection<LocaleOption> AvailableLocales { get; set; }

        public ManageVersions(VersionService versionService, LogService logService)
        {
            _versionService = versionService;
            _logService = logService;

            _allLeagueClientVersions = new List<VersionFileInfo>();
            _allLoLGameClientVersions = new List<VersionFileInfo>();

            LeagueClientVersions = new ObservableCollection<VersionFileInfo>();
            LoLGameClientVersions = new ObservableCollection<VersionFileInfo>();

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

                _allLeagueClientVersions = allFiles.Where(f => f.Category == "league-client").ToList();
                var gameClientCategories = new[] { "lol-game-client" };
                _allLoLGameClientVersions = allFiles.Where(f => gameClientCategories.Contains(f.Category)).ToList();

                LeagueClientCurrentPage = 1;
                LoLGameClientCurrentPage = 1;

                UpdateLeagueClientPagedView();
                UpdateLoLGameClientPagedView();
            }
        }

        private void UpdateLeagueClientPagedView()
        {
            LeagueClientTotalPages = (int)Math.Ceiling(_allLeagueClientVersions.Count / (double)PageSize);
            var pagedItems = _allLeagueClientVersions.Skip((LeagueClientCurrentPage - 1) * PageSize).Take(PageSize);

            LeagueClientVersions.Clear();
            foreach (var item in pagedItems)
            {
                LeagueClientVersions.Add(item);
            }
        }

        private void UpdateLoLGameClientPagedView()
        {
            LoLGameClientTotalPages = (int)Math.Ceiling(_allLoLGameClientVersions.Count / (double)PageSize);
            var pagedItems = _allLoLGameClientVersions.Skip((LoLGameClientCurrentPage - 1) * PageSize).Take(PageSize);

            LoLGameClientVersions.Clear();
            foreach (var item in pagedItems)
            {
                LoLGameClientVersions.Add(item);
            }
        }

        public void NextLeagueClientPage()
        {
            if (CanGoToNextLeagueClientPage)
            {
                LeagueClientCurrentPage++;
            }
        }

        public void PrevLeagueClientPage()
        {
            if (CanGoToPrevLeagueClientPage)
            {
                LeagueClientCurrentPage--;
            }
        }

        public void NextLoLGameClientPage()
        {
            if (CanGoToNextLoLGameClientPage)
            {
                LoLGameClientCurrentPage++;
            }
        }

        public void PrevLoLGameClientPage()
        {
            if (CanGoToPrevLoLGameClientPage)
            {
                LoLGameClientCurrentPage--;
            }
        }

        public void DeleteVersions(IEnumerable<VersionFileInfo> versionsToDelete)
        {
            if (versionsToDelete == null || !versionsToDelete.Any()) return;

            if (_versionService.DeleteVersionFiles(versionsToDelete))
            {
                foreach (var versionFile in versionsToDelete.ToList())
                {
                    _allLeagueClientVersions.Remove(versionFile);
                    _allLoLGameClientVersions.Remove(versionFile);
                }
                // Recalculate total pages and update views after deletion
                UpdateLeagueClientPagedView();
                UpdateLoLGameClientPagedView();
            }
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
