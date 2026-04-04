using System;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Views.Models.Library;
using Microsoft.Extensions.DependencyInjection;

namespace AssetsManager.Views
{
    public partial class LibraryWindow : UserControl
    {
        private readonly LibraryViewModel _viewModel;

        public LibraryWindow(LibraryViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;

            Loaded += LibraryWindow_Loaded;
        }

        private async void LibraryWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await _viewModel.LoadInitialDataAsync();
        }

        private async void RebuildIndex_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.RebuildIndexAsync();
        }

        private void CancelIndexing_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.CancelIndexing();
        }
    }
}
