using System.Windows.Controls;
using System.Windows;
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

        private void StudioSkybox_Checked(object sender, RoutedEventArgs e)
        {
            if (swStudioTransparentBackground.IsChecked == true)
                swStudioTransparentBackground.IsChecked = false;
        }

        private void StudioTransparentBackground_Checked(object sender, RoutedEventArgs e)
        {
            if (swStudioSkybox.IsChecked == true)
                swStudioSkybox.IsChecked = false;
        }

    }
}
