using System.Windows.Controls;
using AssetsManager.Views.Models.Shared;

namespace AssetsManager.Views.Settings
{
    public partial class GeneralSettingsView : UserControl
    {
        public SettingsModel ViewModel => DataContext as SettingsModel;

        public GeneralSettingsView()
        {
            InitializeComponent();
        }
    }
}
