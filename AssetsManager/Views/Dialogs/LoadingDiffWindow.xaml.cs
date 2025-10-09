using System.ComponentModel;
using System.Windows;

namespace AssetsManager.Views.Dialogs
{
    public partial class LoadingDiffWindow : Window
    {
        public LoadingDiffWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadingControl.ShowLoading(true);
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            LoadingControl.ShowLoading(false);
        }
    }
}
