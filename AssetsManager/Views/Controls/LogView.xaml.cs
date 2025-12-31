using System.Windows.Controls;
using System.Windows;
using Material.Icons;
using System;

using System.Windows.Media.Animation;

namespace AssetsManager.Views.Controls
{
    public partial class LogView : UserControl
    {
        public RichTextBox LogRichTextBox => richTextBoxLogs;

        public event EventHandler ToggleLogSizeRequested;
        public event EventHandler LogExpandedManually;

        public LogView()
        {
            InitializeComponent();
            this.SizeChanged += LogView_SizeChanged;
        }

        private void LogView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // If the user manually resizes the log view (e.g. using the GridSplitter) while it is collapsed
            if (richTextBoxLogs.Visibility == Visibility.Collapsed && e.NewSize.Height > 60)
            {
                richTextBoxLogs.Visibility = Visibility.Visible;
                ToggleIcon.Kind = MaterialIconKind.ChevronDown;
                LogExpandedManually?.Invoke(this, EventArgs.Empty);
            }
        }

        private void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            richTextBoxLogs.Document.Blocks.Clear();
        }

        private void ClearStatusBar_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                mainWindow.ClearStatusBar();
            }
        }

        private void ToggleLogSize_Click(object sender, RoutedEventArgs e)
        {
            if (richTextBoxLogs.Visibility == Visibility.Visible)
            {
                richTextBoxLogs.Visibility = Visibility.Collapsed;
                ToggleIcon.Kind = MaterialIconKind.ChevronUp;
            }
            else
            {
                richTextBoxLogs.Visibility = Visibility.Visible;
                ToggleIcon.Kind = MaterialIconKind.ChevronDown;
            }

            ToggleLogSizeRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
