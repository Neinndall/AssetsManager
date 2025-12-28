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

        public LogView()
        {
            InitializeComponent();
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
    }
}
