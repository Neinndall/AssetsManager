using System.Windows.Controls;
using System.Windows;

namespace AssetsManager.Views.Dialogs.Controls
{
    public partial class LoadingDiffControl : UserControl
    {
        public LoadingDiffControl()
        {
            InitializeComponent();
        }

        public void ShowLoading(bool show)
        {
            Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}