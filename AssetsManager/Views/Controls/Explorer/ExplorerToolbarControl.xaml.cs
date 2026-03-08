using System.Windows;
using System.Windows.Controls;
using AssetsManager.Views.Models.Explorer;

namespace AssetsManager.Views.Controls.Explorer
{
    public partial class ExplorerToolbarControl : UserControl
    {
        public ExplorerToolbarModel ViewModel => DataContext as ExplorerToolbarModel;

        public ExplorerToolbarControl()
        {
            InitializeComponent();
        }

        private void SearchToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (SearchToggleButton.IsChecked == true)
            {
                // Delay focus slightly to allow the animation to start and the control to become visible
                Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    SearchTextBox.Focus();
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private void CollapseToContainerButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ParentExplorer?.HandleCollapseToContainer();
        }

        private void LoadComparisonButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ParentExplorer?.HandleLoadComparison();
        }

        private void SwitchModeButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ParentExplorer?.HandleSwitchMode();
        }

        private void ImageMergerButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ParentExplorer?.HandleImageMergerClicked();
        }
    }
}
