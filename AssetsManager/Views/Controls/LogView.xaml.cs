using System.Windows.Controls;
using System.Windows;
using Material.Icons;
using System;

namespace AssetsManager.Views.Controls
{
    public partial class LogView : UserControl
    {
        public RichTextBox LogRichTextBox => richTextBoxLogs;

        public event EventHandler ToggleLogSizeRequested;
        public event EventHandler LogExpandedManually;

        // Dependency Property for Notification Count
        public static readonly DependencyProperty NotificationCountProperty =
            DependencyProperty.Register("NotificationCount", typeof(int), typeof(LogView), new PropertyMetadata(0));

        public int NotificationCount
        {
            get { return (int)GetValue(NotificationCountProperty); }
            set { SetValue(NotificationCountProperty, value); }
        }

        public LogView()
        {
            InitializeComponent();
            this.SizeChanged += LogView_SizeChanged;
        }

        private void LogView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Update icon and visibility based on manual resize (GridSplitter)
            if (e.NewSize.Height <= 45)
            {
                if (ToggleIcon.Kind != MaterialIconKind.ChevronUp)
                {
                    ToggleIcon.Kind = MaterialIconKind.ChevronUp;
                    richTextBoxLogs.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                if (ToggleIcon.Kind != MaterialIconKind.ChevronDown)
                {
                    ToggleIcon.Kind = MaterialIconKind.ChevronDown;
                    richTextBoxLogs.Visibility = Visibility.Visible;
                    LogExpandedManually?.Invoke(this, EventArgs.Empty);
                }
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