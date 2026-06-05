using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Effects;
using System.Windows.Threading;
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

        public void SetBatchIndex(int currentFile, int totalFiles)
        {
            LoadingControl.SetBatchIndex(currentFile, totalFiles);
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

        // Atomic Handover: removes the owner BlurEffect and closes the loading window
        // in a single dispatcher pass at ContextIdle priority, so the new window is
        // already painted on screen and the visual swap is seamless (no flash of
        // the closing loading card while the result window is appearing).
        public void BeginAtomicHandover()
        {
            if (Owner != null)
            {
                Owner.Effect = null;
            }

            Dispatcher.BeginInvoke(new System.Action(Close), DispatcherPriority.ContextIdle);
        }
    }
}
