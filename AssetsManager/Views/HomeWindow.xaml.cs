using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Views.Models;
using AssetsManager.Utils;
using AssetsManager.Services.Monitor;

namespace AssetsManager.Views
{
    public partial class HomeWindow : UserControl
    {
        private readonly HomeViewModel _viewModel;
        public event Action<string> NavigationRequested;

        public HomeWindow(AppSettings appSettings)
        {
            InitializeComponent();
            
            _viewModel = new HomeViewModel(appSettings);
            DataContext = _viewModel;
            
            Unloaded += HomeWindow_Unloaded;
        }

        private void HomeWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            _viewModel.Cleanup();
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
