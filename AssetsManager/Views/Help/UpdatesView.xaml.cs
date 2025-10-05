using System.Windows.Controls;
using AssetsManager.Services.Updater;

namespace AssetsManager.Views.Help
{
    public partial class UpdatesView : UserControl
    {
        private readonly UpdateManager _updateManager;

        public UpdatesView(UpdateManager updateManager)
        {
            InitializeComponent();
            _updateManager = updateManager;
        }

        private async void buttonCheckUpdates_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            // Assuming the parent window is the owner for the update dialog
            var parentWindow = System.Windows.Window.GetWindow(this);
            await _updateManager.CheckForUpdatesAsync(parentWindow, true);
        }
    }
}
