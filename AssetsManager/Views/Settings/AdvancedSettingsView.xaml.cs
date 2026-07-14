using System.Windows.Controls;
using AssetsManager.Views.Models.Shared;

namespace AssetsManager.Views.Settings
{
    public partial class AdvancedSettingsView : UserControl
    {
        public SettingsModel ViewModel => DataContext as SettingsModel;

        public AdvancedSettingsView()
        {
            InitializeComponent();
        }
    }
}
