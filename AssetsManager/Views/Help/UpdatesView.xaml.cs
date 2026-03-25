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
        
        // The State Model
        public AppUpdatesModel Model { get; } = new AppUpdatesModel();

        public UpdatesView(UpdateManager updateManager, UpdateCheckService updateCheckService)
        {
            InitializeComponent();
            DataContext = Model;

            _updateManager = updateManager;
            _updateCheckService = updateCheckService;

            // Initialize Model Data
            Model.CurrentVersion = ApplicationInfos.Version;
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
                Model.IsUpdateAvailable = true;
                Model.AvailableVersion = available.ToString();
            }
            else
            {
                Model.IsUpdateAvailable = false;
                Model.AvailableVersion = string.Empty;
            }
        }

        private void BtnOpenUpdateCenter_Click(object sender, RoutedEventArgs e)
        {
            var updatesWindow = App.ServiceProvider.GetRequiredService<Dialogs.CommitHistoryWindow>();
            updatesWindow.Initialize(Model.CurrentVersion);
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
