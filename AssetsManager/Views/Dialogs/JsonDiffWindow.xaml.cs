using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AssetsManager.Services.Core;
using AssetsManager.Services.Formatting;
using MahApps.Metro.Controls;

namespace AssetsManager.Views.Dialogs
{
    public partial class JsonDiffWindow : MetroWindow
    {
        public JsonDiffWindow(CustomMessageBoxService customMessageBoxService, JsonFormatterService jsonFormatterService)
        {
            InitializeComponent();
            JsonDiffControl.CustomMessageBoxService = customMessageBoxService;
            JsonDiffControl.JsonFormatterService = jsonFormatterService;
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

        private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) this.DragMove();
        }
    }
}
