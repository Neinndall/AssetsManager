using System.Windows;
using System.Windows.Controls;
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
        public UpdatesModel Model { get; } = new UpdatesModel();

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

        private async void buttonInstallUpdate_Click(object sender, RoutedEventArgs e)
        {
            var parentWindow = Window.GetWindow(this);
            await _updateManager.CheckForUpdatesAsync(parentWindow, true);
        }
    }
}
