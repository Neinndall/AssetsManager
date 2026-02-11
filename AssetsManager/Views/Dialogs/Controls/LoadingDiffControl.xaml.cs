using System.Windows.Controls;
using System.Windows;
using AssetsManager.Views.Models.Dialogs.Controls;

namespace AssetsManager.Views.Dialogs.Controls
{
    public partial class LoadingDiffControl : UserControl
    {
        public LoadingDiffModel ViewModel { get; } = new LoadingDiffModel();

        public LoadingDiffControl()
        {
            InitializeComponent();
            DataContext = ViewModel;
        }

        public void ShowLoading(bool show)
        {
            Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
            {
                ViewModel.Reset();
                ViewModel.IsBusy = true;
            }
            else
            {
                ViewModel.IsBusy = false;
            }
        }

        public void SetProgress(double value)
        {
            ViewModel.ProgressValue = value;
        }

        public void SetDescription(string text)
        {
            ViewModel.Description = text;
        }

        public void SetState(DiffLoadingState state)
        {
            ViewModel.SetState(state);
        }
    }
}