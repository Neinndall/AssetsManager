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
        private readonly LogService _logService;

        public JsonDiffWindow(CustomMessageBoxService customMessageBoxService, JsonFormatterService jsonFormatterService, LogService logService)
        {
            InitializeComponent();
            _logService = logService;
            JsonDiffControl.CustomMessageBoxService = customMessageBoxService;
            JsonDiffControl.JsonFormatterService = jsonFormatterService;
            JsonDiffControl.ParentWindow = this;

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

            // 1. Scroll to the first difference
            JsonDiffControl.FocusFirstDifference();

            // 2. IMPORTANT: Wait for the UI to process the initial rendering of the heavy text
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            // 3. Smooth Handover: Take focus first
            this.Activate();
            this.Focus();

            // 4. Final adjustments (Guide, etc)
            await Dispatcher.InvokeAsync(() =>
            {
                JsonDiffControl.RefreshGuidePosition();
            }, DispatcherPriority.Input);
        }

        // Used by DiffViewService when the file content is already processed and available in memory.
        public async Task LoadAndDisplayDiffAsync(string oldText, string newText, string oldFileName, string newFileName, Action<DiffLoadingState> onProgress = null)
        {
            JsonDiffControl.ViewModel.IsBatchMode = false;
            await JsonDiffControl.LoadAndDisplayDiffAsync(oldText, newText, oldFileName, newFileName, onProgress);
        }

        public async Task LoadAndDisplayPreloadedBatchAsync(List<(string oldText, string newText, string oldPath, string newPath)> items, int startIndex, Action<DiffLoadingState> onProgress = null)
        {
            await JsonDiffControl.LoadAndDisplayPreloadedBatchAsync(items, startIndex, onProgress);
        }
    }
}
