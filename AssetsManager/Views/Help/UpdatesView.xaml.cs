using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Info;
using AssetsManager.Services.Updater;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Help;

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
            _viewModel.CurrentVersion = ApplicationInfos.Version;
            UpdateModelState();

            // Subscribe to background update checks
            _updateCheckService.UpdatesFound += (s, e) => UpdateModelState();
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
