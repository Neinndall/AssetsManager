using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Views.Models.Home;
using AssetsManager.Utils;
using AssetsManager.Views.Dialogs;

namespace AssetsManager.Views
{
    public partial class HomeWindow : UserControl
    {
        public MainWindow ParentWindow { get; set; }
        private readonly HomeModel _model;
        private readonly IServiceProvider _serviceProvider;

        public HomeWindow(IServiceProvider serviceProvider)
        {
            InitializeComponent();
            
            _serviceProvider = serviceProvider;
            _model = new HomeModel(
                serviceProvider.GetRequiredService<AppSettings>(), 
                serviceProvider.GetRequiredService<DirectoriesCreator>()
            );
            DataContext = _model;
            
            Unloaded += HomeWindow_Unloaded;
        }

        private void HomeWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            Unloaded -= HomeWindow_Unloaded;
            _model.Dispose();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.CommandParameter is string destination)
            {
                if (ParentWindow != null)
                {
                    string sidebarTag = destination;
                    if (destination.StartsWith("Explorer_")) sidebarTag = "Explorer";

                    ParentWindow.Sidebar.SelectNavigationItem(sidebarTag);
                    ParentWindow.OnSidebarNavigationRequested(destination);
                }
            }
        }

        private void Notepad_Click(object sender, RoutedEventArgs e)
        {
            var notepadWindow = _serviceProvider.GetRequiredService<NotepadWindow>();
            notepadWindow.Owner = Window.GetWindow(this);
            notepadWindow.Show();
        }

        private void AudioPlayer_Click(object sender, RoutedEventArgs e)
        {
            var audioPlayerWindow = _serviceProvider.GetRequiredService<AudioPlayerWindow>();
            audioPlayerWindow.Owner = Window.GetWindow(this);
            audioPlayerWindow.Show();
        }

        private void Converter_Click(object sender, RoutedEventArgs e)
        {
            var converterWindow = _serviceProvider.GetRequiredService<ConverterWindow>();
            converterWindow.Owner = Window.GetWindow(this);
            converterWindow.Show();
        }
    }
}
