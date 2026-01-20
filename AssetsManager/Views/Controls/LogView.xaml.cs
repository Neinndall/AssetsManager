using System.Windows.Controls;
using System.Windows;
using Material.Icons;
using System;
using AssetsManager.Views.Models.Controls;

namespace AssetsManager.Views.Controls
{
    public partial class LogView : UserControl
    {
        public LogViewModel ViewModel { get; } = new LogViewModel();

        public RichTextBox LogRichTextBox => richTextBoxLogs;

        public event EventHandler ToggleLogSizeRequested;
        public event EventHandler LogExpandedManually;
        public event EventHandler ClearStatusBarRequested;
        public event EventHandler NotificationClicked;

        // Dependency Property for StatusText (to sync Clear Button state)
        public static readonly DependencyProperty StatusTextProperty =
            DependencyProperty.Register("StatusText", typeof(string), typeof(LogView), new PropertyMetadata(null, OnStatusTextChanged));

        public string StatusText
        {
            get { return (string)GetValue(StatusTextProperty); }
            set { SetValue(StatusTextProperty, value); }
        }

        private static void OnStatusTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LogView logView)
            {
                // Logic: If there is text, status is active.
                logView.ViewModel.HasActiveStatus = !string.IsNullOrEmpty((string)e.NewValue);
            }
        }

        public LogView()
        {
            InitializeComponent();
            DataContext = ViewModel;
            this.SizeChanged += LogView_SizeChanged;
        }

        private void LogView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Update icon and visibility based on manual resize (GridSplitter)
            if (e.NewSize.Height <= 45)
            {
                ViewModel.SetLogVisibility(false);
            }
            else
            {
                if (!ViewModel.IsLogVisible)
                {
                    ViewModel.SetLogVisibility(true);
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
            ClearStatusBarRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ToggleLogSize_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ToggleLog();
            ToggleLogSizeRequested?.Invoke(this, EventArgs.Empty);
        }
        
        private void NotificationButton_Click(object sender, RoutedEventArgs e)
        {
            NotificationClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}