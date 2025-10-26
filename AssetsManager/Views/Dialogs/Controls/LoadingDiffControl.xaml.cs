using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows;

namespace AssetsManager.Views.Dialogs.Controls
{
    public partial class LoadingDiffControl : UserControl
    {
        private readonly Storyboard _loadingAnimation;

        public LoadingDiffControl()
        {
            InitializeComponent();

            var originalStoryboard = (Storyboard)this.TryFindResource("SpinningIconAnimation");
            if (originalStoryboard != null)
            {
                _loadingAnimation = originalStoryboard.Clone();
                Storyboard.SetTarget(_loadingAnimation, ProgressIcon);
            }

            this.Unloaded += LoadingDiffControl_Unloaded;
        }

        private void LoadingDiffControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _loadingAnimation?.Stop();
        }

        public void ShowLoading(bool show)
        {
            Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
            {
                _loadingAnimation?.Begin();
            }
            else
            {
                _loadingAnimation?.Stop();
            }
        }
    }
}