using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Versions;

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

        private void ListViewItem_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!(sender is ListViewItem item) || !(item.DataContext is VersionFileInfo clickedVersion)) return;

            // Handle right-click: prevent selection and stop the event from propagating.
            if (e.ChangedButton == System.Windows.Input.MouseButton.Right)
            {
                e.Handled = true;
                return;
            }

            // Handle left-click for selection logic
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                // If Ctrl is pressed, toggle selection (for multi-select delete)
                if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
                {
                    clickedVersion.IsSelected = !clickedVersion.IsSelected;
                }
                else // If Ctrl is not pressed, behave like a radio button (toggle)
                {
                    bool wasSelected = clickedVersion.IsSelected;

                    // 1. Deselect all items in both lists
                    foreach (var version in _viewModel.AllLeagueClientVersions)
                    {
                        version.IsSelected = false;
                    }
                    foreach (var version in _viewModel.AllLoLGameClientVersions)
                    {
                        version.IsSelected = false;
                    }

                    // 2. If the item was not already selected, select it.
                    //    If it was selected, the loop above has already deselected it.
                    if (!wasSelected)
                    {
                        clickedVersion.IsSelected = true;
                    }
                }
                e.Handled = true;
            }
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
                DeleteVersions(selectedVersions);
            }
        }

        private void DeleteVersions(IEnumerable<VersionFileInfo> versionsToDelete)
        {
            if (versionsToDelete == null || !versionsToDelete.Any()) return;

            if (VersionService.DeleteVersionFiles(versionsToDelete))
            {
                foreach (var versionFile in versionsToDelete.ToList())
                {
                    _viewModel.AllLeagueClientVersions.Remove(versionFile);
                    _viewModel.AllLoLGameClientVersions.Remove(versionFile);
                }
                // Recalculate total pages and update views after deletion
                _viewModel.LeagueClientPaginator.SetFullList(_viewModel.AllLeagueClientVersions);
                _viewModel.LoLGameClientPaginator.SetFullList(_viewModel.AllLoLGameClientVersions);
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
    }
}
