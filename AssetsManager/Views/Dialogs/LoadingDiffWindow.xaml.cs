using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Effects;
using AssetsManager.Views.Models.Dialogs.Controls;

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

        public void SetProgress(double value)
        {
            LoadingControl.SetProgress(value);
        }

        public void SetDescription(string text)
        {
            LoadingControl.SetDescription(text);
        }

        public void SetState(DiffLoadingState state)
        {
            LoadingControl.SetState(state);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadingControl.ShowLoading(true);
            if (Owner != null)
            {
                Owner.Effect = new BlurEffect { Radius = 5 };
            }
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            LoadingControl.ShowLoading(false);
            if (Owner != null)
            {
                Owner.Effect = null;
            }
            Loaded -= OnLoaded;
            Closing -= OnClosing;
        }
    }
}
