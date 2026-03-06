using System.Windows;
using AssetsManager.Views.Base;

namespace AssetsManager.Themes
{
    public partial class HudWindowStyles : ResourceDictionary
    {
        public HudWindowStyles()
        {
            InitializeComponent();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow((DependencyObject)sender) as HudWindow;
            window?.CloseButton_Click(sender, e);
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow((DependencyObject)sender) as HudWindow;
            window?.MinimizeButton_Click(sender, e);
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow((DependencyObject)sender) as HudWindow;
            window?.MaximizeButton_Click(sender, e);
        }
    }
}
