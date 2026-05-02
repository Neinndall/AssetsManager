using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Services.Champions;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Champions;

namespace AssetsManager.Views
{
    public partial class StatsWindow : UserControl
    {
        private readonly StatsViewModel _viewModel;
        public StatsViewModel ViewModel => _viewModel;
        public MainWindow ParentWindow { get; set; }

        public StatsWindow()
        {
            InitializeComponent();
            
            // Following the project standard: Containers instantiate their own ViewModels
            // but we still use the service provider for dependency injection of services
            var championDataService = App.ServiceProvider.GetRequiredService<ChampionDataService>();
            var logService = App.ServiceProvider.GetRequiredService<LogService>();
            
            _viewModel = new StatsViewModel(championDataService, logService);
            DataContext = _viewModel;
        }

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                await ViewModel.LoadChampionsAsync();
            }
        }
    }
}
