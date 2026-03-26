using System.Windows.Controls;
using System.Windows;
using AssetsManager.Views.Models.Dialogs.Controls;

namespace AssetsManager.Views.Dialogs.Controls
{
    public partial class LoadingDiffControl : UserControl
    {
        private readonly LoadingDiffModel _viewModel;

        public LoadingDiffModel ViewModel => _viewModel;

        public LoadingDiffControl()
        {
            InitializeComponent();
            _viewModel = new LoadingDiffModel();
            DataContext = _viewModel;
        }

        public void ShowLoading(bool show)
        {
            Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
            {
                _viewModel.Reset();
                _viewModel.IsBusy = true;
            }
            else
            {
                _viewModel.IsBusy = false;
            }
        }

        public void SetProgress(double value)
        {
            _viewModel.ProgressValue = value;
        }

        public void SetDescription(string text)
        {
            _viewModel.Description = text;
        }

        public void SetState(DiffLoadingState state)
        {
            _viewModel.SetState(state);
        }
    }
}