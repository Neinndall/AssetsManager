using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AssetsManager.Services.Core;
using AssetsManager.Services.Formatting;
using AssetsManager.Views.Helpers;

namespace AssetsManager.Views.Dialogs
{
    public partial class JsonDiffWindow : HudWindow
    {
        public JsonDiffWindow(CustomMessageBoxService customMessageBoxService, JsonFormatterService jsonFormatterService)
        {
            InitializeComponent();
            JsonDiffControl.CustomMessageBoxService = customMessageBoxService;
            JsonDiffControl.JsonFormatterService = jsonFormatterService;
            JsonDiffControl.ParentWindow = this;

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
