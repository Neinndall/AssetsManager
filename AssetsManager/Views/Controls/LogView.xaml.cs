using System.Windows.Controls;
using System.Windows;
using System;
using System.Linq;
using AssetsManager.Views.Models.Controls;

namespace AssetsManager.Views.Controls
{
    public partial class LogView : UserControl
    {
        public MainWindow ParentWindow { get; set; }
        public LogViewModel ViewModel { get; } = new LogViewModel();

        public RichTextBox LogRichTextBox => richTextBoxLogs;

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
            if (e.NewSize.Height <= 45)
            {
                ViewModel.SetLogVisibility(false);
            }
            else
            {
                if (!ViewModel.IsLogVisible)
                {
                    ViewModel.SetLogVisibility(true);
                    if (ParentWindow != null)
                    {
                        ParentWindow.HandleLogExpandedManually();
                    }
                }
            }
        }

        private void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            richTextBoxLogs.Document.Blocks.Clear();
        }

        private void ClearStatusBar_Click(object sender, RoutedEventArgs e)
        {
            ParentWindow?.ClearStatusBar();
        }

        private void ToggleLogSize_Click(object sender, RoutedEventArgs e)
        {
            ParentWindow?.OnToggleLogSizeRequested(this, EventArgs.Empty);
        }
        
        private void NotificationButton_Click(object sender, RoutedEventArgs e)
        {
            ParentWindow?.OnNotificationHubRequested(this, EventArgs.Empty);
        }
    }
}
