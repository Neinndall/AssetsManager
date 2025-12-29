using System;
using System.Windows;
using System.Windows.Controls;

namespace AssetsManager.Views
{
    public partial class HomeWindow : UserControl
    {
        public event Action<string> NavigationRequested;

        public HomeWindow()
        {
            InitializeComponent();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string destination)
            {
                NavigationRequested?.Invoke(destination);
            }
        }
    }
}