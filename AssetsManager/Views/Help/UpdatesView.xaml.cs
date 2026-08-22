using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Services.Updater;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Help;
using AssetsManager.Views.Models.Notifications;
using AssetsManager.Views.Models.News;

namespace AssetsManager.Views.Help
{
    public partial class UpdatesView : UserControl
    {
        private readonly UpdateManager _updateManager;
        private readonly UpdateCheckService _updateCheckService;
        private readonly AppUpdatesModel _viewModel;

        public AppUpdatesModel ViewModel => _viewModel;

        public UpdatesView(UpdateManager updateManager, UpdateCheckService updateCheckService)
        {
            InitializeComponent();
            _viewModel = new AppUpdatesModel();
            DataContext = _viewModel;

            _updateManager = updateManager;
            _updateCheckService = updateCheckService;

            // Initialize Model Data
            _viewModel.CurrentVersion = VersionInfo.Version;
            UpdateModelState();

            Loaded += UpdatesView_Loaded;
            Unloaded += UpdatesView_Unloaded;
        }

        private void UpdatesView_Loaded(object sender, RoutedEventArgs e)
        {
            _updateCheckService.UpdatesFound += OnUpdatesFound;
        }

        private void UpdatesView_Unloaded(object sender, RoutedEventArgs e)
        {
            _updateCheckService.UpdatesFound -= OnUpdatesFound;
        }

        private void OnUpdatesFound(string message, string latestVersion, NotificationCategory category, string title, NewsItemModel newsItem)
        {
            UpdateModelState();
        }

        private void UpdateModelState()
        {
            // Execute on UI Thread if necessary, but event is usually emitted on UI thread 
            // via the background timer's callback in UpdateCheckService.
            var available = _updateCheckService.AvailableVersion;
            if (available != null)
            {
                _viewModel.IsUpdateAvailable = true;
                _viewModel.AvailableVersion = available.ToString();
            }
            else
            {
                _viewModel.IsUpdateAvailable = false;
                _viewModel.AvailableVersion = string.Empty;
            }
        }

        private void BtnOpenUpdateCenter_Click(object sender, RoutedEventArgs e)
        {
            var updatesWindow = App.ServiceProvider.GetRequiredService<Dialogs.CommitHistoryWindow>();
            updatesWindow.Initialize(_viewModel.CurrentVersion);
            updatesWindow.Owner = Application.Current.MainWindow;
            updatesWindow.ShowDialog();
        }

        private async void buttonInstallUpdate_Click(object sender, RoutedEventArgs e)
        {
            var parentWindow = Window.GetWindow(this);
            await _updateManager.CheckForUpdatesAsync(parentWindow, true);
        }
    }
}
