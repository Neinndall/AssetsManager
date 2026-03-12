using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AssetsManager.Services.Core;
using AssetsManager.Services.Formatting;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Wad;

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
            JsonDiffControl.ViewModel.IsBatchMode = false;
            JsonDiffControl.Visibility = Visibility.Visible;
            await JsonDiffControl.LoadAndDisplayDiffAsync(oldText, newText, oldFileName, newFileName);
        }

        public async Task LoadAndDisplayBatchDiffAsync(
            List<SerializableChunkDiff> items, 
            int startIndex, 
            string oldPbePath, 
            string newPbePath,
            Func<SerializableChunkDiff, string, string, Task<(string oldText, string newText)>> loadDataFunc)
        {
            var vm = JsonDiffControl.ViewModel;
            vm.BatchItems = items;
            vm.OldPbePath = oldPbePath;
            vm.NewPbePath = newPbePath;
            vm.LoadDataFunc = loadDataFunc;
            
            vm.IsBatchMode = true;
            vm.TotalFilesCount = items.Count;
            vm.CurrentFileIndex = startIndex + 1;

            await LoadCurrentBatchItemAsync();
        }

        private async Task LoadCurrentBatchItemAsync()
        {
            var vm = JsonDiffControl.ViewModel;
            if (vm.BatchItems == null || vm.BatchItems.Count == 0 || vm.LoadDataFunc == null) return;

            var currentItem = vm.BatchItems[vm.CurrentFileIndex - 1];
            
            var (oldText, newText) = await vm.LoadDataFunc(currentItem, vm.OldPbePath, vm.NewPbePath);
            
            await JsonDiffControl.LoadAndDisplayDiffAsync(oldText, newText, currentItem.OldPath, currentItem.NewPath);
            JsonDiffControl.FocusFirstDifference();
            JsonDiffControl.RefreshGuidePosition();
        }

        public async void BtnPrevFile_Click(object sender, RoutedEventArgs e)
        {
            var vm = JsonDiffControl.ViewModel;
            if (vm.CurrentFileIndex > 1)
            {
                vm.CurrentFileIndex--;
                await LoadCurrentBatchItemAsync();
            }
        }

        public async void BtnNextFile_Click(object sender, RoutedEventArgs e)
        {
            var vm = JsonDiffControl.ViewModel;
            if (vm.CurrentFileIndex < vm.TotalFilesCount)
            {
                vm.CurrentFileIndex++;
                await LoadCurrentBatchItemAsync();
            }
        }
    }
}
