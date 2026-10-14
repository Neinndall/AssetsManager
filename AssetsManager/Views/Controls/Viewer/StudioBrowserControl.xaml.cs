using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioBrowserControl : UserControl
    {
        internal StudioControl Owner { get; set; }

        public StudioBrowserControl() => InitializeComponent();

        private void AddSkinToScene_Click(object sender, RoutedEventArgs e) => Owner?.AddSkinToScene_Click(sender, e);
        private void BrowseRoot_Click(object sender, RoutedEventArgs e) => Owner?.BrowseRoot_Click(sender, e);
        private void BrowserItem_Expanded(object sender, RoutedEventArgs e) => Owner?.BrowserItem_Expanded(sender, e);
        private void CloseProject_Click(object sender, RoutedEventArgs e) => Owner?.CloseProject_Click(sender, e);
        private void ExitStudio_Click(object sender, RoutedEventArgs e) => Owner?.ExitStudio_Click(sender, e);
        private void MapBrowserEye_Click(object sender, RoutedEventArgs e) => Owner?.MapBrowserEye_Click(sender, e);
        private void MapBrowserEye_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Owner?.MapBrowserEye_PreviewMouseLeftButtonDown(sender, e);
        private void OpenChromaLibrary_Click(object sender, RoutedEventArgs e) => Owner?.OpenChromaLibrary_Click(sender, e);
        private void OpenSkinInNewTab_Click(object sender, RoutedEventArgs e) => Owner?.OpenSkinInNewTab_Click(sender, e);
        private void SearchQuery_TextChanged(object sender, TextChangedEventArgs e) => Owner?.SearchQuery_TextChanged(sender, e);
        private void StudioBrowser_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => Owner?.StudioBrowser_SelectedItemChanged(sender, e);
        private void StudioOptions_Click(object sender, RoutedEventArgs e) => Owner?.StudioOptions_Click(sender, e);
    }
}
