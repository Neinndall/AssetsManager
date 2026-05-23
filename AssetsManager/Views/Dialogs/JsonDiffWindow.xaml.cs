using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AssetsManager.Services.Core;
using AssetsManager.Services.Formatting;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Wad;
using AssetsManager.Views.Models.Dialogs.Controls;

namespace AssetsManager.Views.Dialogs
{
    public partial class JsonDiffWindow : HudWindow
    {
        public LoadingDiffWindow LoadingWindow { get; set; }

        public JsonDiffWindow(CustomMessageBoxService customMessageBoxService, JsonFormatterService jsonFormatterService)
        {
            InitializeComponent();
            JsonDiffControl.CustomMessageBoxService = customMessageBoxService;
            JsonDiffControl.JsonFormatterService = jsonFormatterService;
            JsonDiffControl.ParentWindow = this;

            // Start invisible to prevent visual jump and transparency gaps
            Visibility = Visibility.Hidden;
            Loaded += JsonDiffWindow_Loaded;
            Closed += OnWindowClosed;
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            JsonDiffControl.Dispose();
        }

        private async void JsonDiffWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= JsonDiffWindow_Loaded;

            // 0. Visual Satisfaction: Show the bar as 100% ready while we do the final internal rendering
            if (LoadingWindow != null)
            {
                LoadingWindow.SetState(DiffLoadingState.Ready);
            }

            // 1. Scroll to the first difference while still invisible
            JsonDiffControl.FocusFirstDifference();

            // 2. IMPORTANT: Wait for the UI to process the initial rendering of the heavy text
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            // 3. Now make the window visible and bring to front
            this.Visibility = Visibility.Visible;

            // 4. Smooth Handover: Close loading window now that we are visible
            if (LoadingWindow != null)
            {
                LoadingWindow.Close();
                LoadingWindow = null;
            }

            // 5. Final adjustments (Guide, etc)
            await Dispatcher.InvokeAsync(() =>
            {
                JsonDiffControl.RefreshGuidePosition();
            }, DispatcherPriority.Input);
        }

        // Used by DiffViewService when the file content is already processed and available in memory.
        public async Task LoadAndDisplayDiffAsync(string oldText, string newText, string oldFileName, string newFileName, LoadingDiffWindow loadingWindow = null)
        {
            LoadingWindow = loadingWindow; // Take custody of the loading window
            JsonDiffControl.ViewModel.IsBatchMode = false;
            await JsonDiffControl.LoadAndDisplayDiffAsync(oldText, newText, oldFileName, newFileName);
        }

        public async Task LoadAndDisplayBatchDiffAsync(
            List<SerializableChunkDiff> items, 
            int startIndex, 
            string oldPbePath, 
            string newPbePath,
            Func<SerializableChunkDiff, string, string, Task<(string oldText, string newText)>> loadDataFunc,
            LoadingDiffWindow loadingWindow = null)
        {
            LoadingWindow = loadingWindow; // Take custody
            await JsonDiffControl.LoadAndDisplayBatchDiffAsync(items, startIndex, oldPbePath, newPbePath, loadDataFunc);
        }
    }
}
