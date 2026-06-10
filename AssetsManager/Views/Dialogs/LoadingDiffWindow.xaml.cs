using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using AssetsManager.Views.Models.Dialogs.Controls;

namespace AssetsManager.Views.Dialogs
{
    public partial class LoadingDiffWindow : Window
    {
        private readonly TaskCompletionSource<bool> _loadTcs = new TaskCompletionSource<bool>();
        public Task LoadTask => _loadTcs.Task;

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

        public async Task SetStateAndRenderAsync(DiffLoadingState state)
        {
            SetState(state);
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            
            // Adaptive Delay: Longer delay for individual files to guarantee readability (150ms).
            // Shorter delay for batch files (50ms) to ensure high throughput.
            bool isBatchState = state == DiffLoadingState.BatchLoadingFile || 
                               state == DiffLoadingState.BatchFormattingFile || 
                               state == DiffLoadingState.BatchFileReady;
            int delay = isBatchState ? 50 : 150;
            await Task.Delay(delay);
        }

        public void SetBatchIndex(int currentFile, int totalFiles, string fileType = "file")
        {
            LoadingControl.SetBatchIndex(currentFile, totalFiles, fileType);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadingControl.ShowLoading(true);
            if (Owner != null)
            {
                Owner.Effect = new BlurEffect { Radius = 5 };
            }
            _loadTcs.TrySetResult(true);
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
