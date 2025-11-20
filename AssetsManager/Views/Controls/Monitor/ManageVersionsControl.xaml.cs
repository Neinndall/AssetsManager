using AssetsManager.Services.Core;
using AssetsManager.Services.Versions;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Versions;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace AssetsManager.Views.Controls.Monitor
{
    public partial class ManageVersionsControl : UserControl
    {
        public VersionService VersionService { get; set; }
        public LogService LogService { get; set; }
        public AppSettings AppSettings { get; set; }
        public CustomMessageBoxService CustomMessageBoxService { get; set; }
        public TaskCancellationManager TaskCancellationManager { get; set; }
        private ManageVersions _viewModel;

        public ManageVersionsControl()
        {
            InitializeComponent();
            this.Loaded += ManageVersionsControl_Loaded;
            this.Unloaded += ManageVersionsControl_Unloaded;
            LeagueClientVersionsListView.SelectionChanged += ListView_SelectionChanged;
            LoLGameClientVersionsListView.SelectionChanged += ListView_SelectionChanged;
        }

        private async void ManageVersionsControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null && VersionService != null && LogService != null)
            {
                _viewModel = new ManageVersions(VersionService, LogService);
                this.DataContext = _viewModel;
                await _viewModel.LoadVersionFilesAsync();
            }
        }

        private async void FetchVersions_Click(object sender, RoutedEventArgs e)
        {
            if (VersionService != null && LogService != null)
            {
                await VersionService.FetchAllVersionsAsync();
                if (_viewModel != null)
                {
                    await _viewModel.LoadVersionFilesAsync();
                }
            }
            else
            {
                CustomMessageBoxService.ShowError("Error", "Services not initialized.", Window.GetWindow(this));
            }
        }

        private async void GetLeagueClient_Click(object sender, RoutedEventArgs e)
        {
            var selectedVersions = _viewModel?.AllLeagueClientVersions.Where(v => v.IsSelected).ToList();
            if (selectedVersions == null || !selectedVersions.Any())
            {
                CustomMessageBoxService.ShowWarning("Warning", "Please select a League Client version from the list first.", Window.GetWindow(this));
                return;
            }
            if (selectedVersions.Count > 1)
            {
                CustomMessageBoxService.ShowWarning("Warning", "Please select only one League Client version at a time for this action.", Window.GetWindow(this));
                return;
            }
            var selectedVersion = selectedVersions.Single();

            if (string.IsNullOrEmpty(AppSettings.LolPbeDirectory))
            {
                CustomMessageBoxService.ShowError("Error", "League of Legends directory is not configured. Please set it in Settings > Default Paths.", Window.GetWindow(this));
                return;
            }

            var locales = _viewModel.AvailableLocales
                .Where(l => l.IsSelected)
                .Select(l => l.Code)
                .ToList();

            if (locales.Count == 0)
            {
                CustomMessageBoxService.ShowWarning("Warning", "Please select at least one locale to download.", Window.GetWindow(this));
                return;
            }

            var cancellationToken = TaskCancellationManager.PrepareNewOperation();
            await VersionService.DownloadPluginsAsync(selectedVersion.Content, AppSettings.LolPbeDirectory, locales, cancellationToken);
        }

        private async void GetLoLGameClient_Click(object sender, RoutedEventArgs e)
        {
            var selectedVersions = _viewModel?.AllLoLGameClientVersions.Where(v => v.IsSelected).ToList();
            if (selectedVersions == null || !selectedVersions.Any())
            {
                CustomMessageBoxService.ShowWarning("Warning", "Please select a LoL Game Client version from the list first.", Window.GetWindow(this));
                return;
            }
            if (selectedVersions.Count > 1)
            {
                CustomMessageBoxService.ShowWarning("Warning", "Please select only one LoL Game Client version at a time for this action.", Window.GetWindow(this));
                return;
            }
            var selectedVersion = selectedVersions.Single();

            if (string.IsNullOrEmpty(AppSettings.LolPbeDirectory))
            {
                CustomMessageBoxService.ShowError("Error", "League of Legends directory is not configured. Please set it in Settings > Default Paths.", Window.GetWindow(this));
                return;
            }
            var locales = _viewModel.AvailableLocales
                .Where(l => l.IsSelected)
                .Select(l => l.Code)
                .ToList();

            if (locales.Count == 0)
            {
                CustomMessageBoxService.ShowWarning("Warning", "Please select at least one locale to download.", Window.GetWindow(this));
                return;
            }

            var cancellationToken = TaskCancellationManager.PrepareNewOperation();
            await VersionService.DownloadGameClientAsync(selectedVersion.Content, AppSettings.LolPbeDirectory, locales, cancellationToken);
        }

        private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Empty event handler to allow multiple selections through CheckBoxes
            // The selection is bound to the IsSelected property of the VersionFileInfo model
        }

        private void DeleteSelectedVersions_Click(object sender, RoutedEventArgs e)
        {
            var selectedVersions = _viewModel.AllLeagueClientVersions.Where(v => v.IsSelected).ToList();
            selectedVersions.AddRange(_viewModel.AllLoLGameClientVersions.Where(v => v.IsSelected));

            if (!selectedVersions.Any())
            {
                CustomMessageBoxService.ShowWarning("Warning", "No versions selected to delete.", Window.GetWindow(this));
                return;
            }

            var result = CustomMessageBoxService.ShowYesNo("Delete Versions", $"Are you sure you want to delete {selectedVersions.Count} selected version file(s)?", Window.GetWindow(this));
            if (result == true)
            {
                _viewModel.DeleteVersions(selectedVersions);
            }
        }

        private void PrevLeagueClientPage_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.LeagueClientPaginator.PreviousPage();
        }

        private void NextLeagueClientPage_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.LeagueClientPaginator.NextPage();
        }

        private void PrevLoLGameClientPage_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.LoLGameClientPaginator.PreviousPage();
        }

        private void NextLoLGameClientPage_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.LoLGameClientPaginator.NextPage();
        }

        private void ManageVersionsControl_Unloaded(object sender, RoutedEventArgs e)
        {
            LeagueClientVersionsListView.SelectionChanged -= ListView_SelectionChanged;
            LoLGameClientVersionsListView.SelectionChanged -= ListView_SelectionChanged;
        }
    }
}
