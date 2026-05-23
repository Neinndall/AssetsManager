using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
                Dispatcher.InvokeAsync(() =>
                {
                    SearchTextBox.Focus();
                }, DispatcherPriority.Input);
            }
        }

        private void CollapseToContainerButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ParentExplorer?.HandleCollapseToContainer();
        }

        private void LoadResultsButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ParentExplorer?.HandleLoadResults();
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
