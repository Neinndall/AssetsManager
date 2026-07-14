using System.Windows.Controls;
using AssetsManager.Views.Models.Help;

namespace AssetsManager.Views.Help
{
    public partial class AboutView : UserControl
    {
        private readonly AboutModel _viewModel;

        public AboutView()
        {
            InitializeComponent();
            _viewModel = new AboutModel();
            DataContext = _viewModel;
        }
    }
}
