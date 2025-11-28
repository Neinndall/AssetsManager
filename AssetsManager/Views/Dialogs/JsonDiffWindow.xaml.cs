using System;
using System.Threading.Tasks;
using System.Windows;
using AssetsManager.Services.Core;
using AssetsManager.Services.Formatting;

namespace AssetsManager.Views.Dialogs
{
    public partial class JsonDiffWindow : Window
    {
        public JsonDiffWindow(CustomMessageBoxService customMessageBoxService, JsonFormattingService jsonFormattingService)
        {
            InitializeComponent();
            JsonDiffControl.CustomMessageBoxService = customMessageBoxService;
            JsonDiffControl.JsonFormattingService = jsonFormattingService;
            JsonDiffControl.ComparisonFinished += JsonDiffControl_ComparisonFinished;

            // Start invisible to prevent visual jump
            Visibility = Visibility.Hidden;
            Loaded += JsonDiffWindow_Loaded;
            Closed += OnWindowClosed;
        }

        private void JsonDiffControl_ComparisonFinished(object sender, bool success)
        {
            if (success)
            {
                Close();
            }
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            JsonDiffControl.ComparisonFinished -= JsonDiffControl_ComparisonFinished;
            JsonDiffControl.Dispose();
        }

        private void JsonDiffWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= JsonDiffWindow_Loaded;

            // Scroll to the first difference while the window is still invisible.
            JsonDiffControl.FocusFirstDifference();

            // Now make the window visible. It will appear already scrolled.
            Visibility = Visibility.Visible;

            // Defer the guide refresh until after this rendering pass is complete.
            // This ensures the panel has its final dimensions to calculate the guide position correctly.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                JsonDiffControl.RefreshGuidePosition();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        // Used by DiffViewService when the file content is already processed and available in memory.
        public async Task LoadAndDisplayDiffAsync(string oldText, string newText, string oldFileName, string newFileName)
        {
            JsonDiffControl.Visibility = Visibility.Visible;
            await JsonDiffControl.LoadAndDisplayDiffAsync(oldText, newText, oldFileName, newFileName);
        }
    }
}
