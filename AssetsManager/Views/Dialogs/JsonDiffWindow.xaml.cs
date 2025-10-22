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
            JsonDiffControl.ComparisonFinished += (sender, success) =>
            {
                if (success)
                {
                    Close();
                }
            };

            // Start invisible to prevent visual jump
            Visibility = Visibility.Hidden;
            Loaded += JsonDiffWindow_Loaded;
            Closed += OnWindowClosed;
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            JsonDiffControl.Dispose();
        }

        private void JsonDiffWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= JsonDiffWindow_Loaded;
            
            // Scroll to the first difference while the window is still invisible
            JsonDiffControl.FocusFirstDifference();

            // Now make the window visible in its final, correct state
            Visibility = Visibility.Visible;
        }

        // Used by DiffViewService when the file content is already processed and available in memory.
        public async Task LoadAndDisplayDiffAsync(string oldText, string newText, string oldFileName, string newFileName)
        {
            JsonDiffControl.Visibility = Visibility.Visible;
            await JsonDiffControl.LoadAndDisplayDiffAsync(oldText, newText, oldFileName, newFileName);
        }
    }
}