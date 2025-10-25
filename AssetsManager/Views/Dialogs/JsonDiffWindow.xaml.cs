using System;
using System.Threading.Tasks;
using System.Windows;
using AssetsManager.Services.Core;

namespace AssetsManager.Views.Dialogs
{
    public partial class JsonDiffWindow : Window
    {
        public JsonDiffWindow(CustomMessageBoxService customMessageBoxService)
        {
            InitializeComponent();
            JsonDiffControl.CustomMessageBoxService = customMessageBoxService;
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

            // Make the window visible first.
            Visibility = Visibility.Visible;

            // Defer focusing the first difference until after the layout has been updated.
            // This ensures all controls have their correct sizes for calculation.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                JsonDiffControl.FocusFirstDifference();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // Used by DiffViewService when the file content is already processed and available in memory.
        public async Task LoadAndDisplayDiffAsync(string oldText, string newText, string oldFileName, string newFileName)
        {
            JsonDiffControl.Visibility = Visibility.Visible;
            await JsonDiffControl.LoadAndDisplayDiffAsync(oldText, newText, oldFileName, newFileName);
        }
    }
}