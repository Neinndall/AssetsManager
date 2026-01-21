using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Views.Models.Home;
using AssetsManager.Utils;

namespace AssetsManager.Views
{
    public partial class HomeWindow : UserControl
    {
        private readonly HomeModel _model;
        public event Action<string> NavigationRequested;

        public HomeWindow(AppSettings appSettings)
        {
            InitializeComponent();
            
            _model = new HomeModel(appSettings);
            DataContext = _model;
            
            Unloaded += HomeWindow_Unloaded;
        }

        private void HomeWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            _model.Cleanup();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.CommandParameter is string destination)
            {
                NavigationRequested?.Invoke(destination);
            }
        }
    }
}
