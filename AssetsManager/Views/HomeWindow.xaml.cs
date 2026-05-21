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

        public HomeWindow(AppSettings appSettings, DirectoriesCreator directoriesCreator)
        {
            InitializeComponent();
            
            _model = new HomeModel(appSettings, directoriesCreator);
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
            var notepadWindow = App.ServiceProvider.GetRequiredService<NotepadWindow>();
            notepadWindow.Owner = Window.GetWindow(this);
            notepadWindow.Show();
        }

        private void AudioPlayer_Click(object sender, RoutedEventArgs e)
        {
            var audioPlayerWindow = App.ServiceProvider.GetRequiredService<AudioPlayerWindow>();
            audioPlayerWindow.Owner = Window.GetWindow(this);
            audioPlayerWindow.Show();
        }

        private void Converter_Click(object sender, RoutedEventArgs e)
        {
            var converterWindow = App.ServiceProvider.GetRequiredService<ConverterWindow>();
            converterWindow.Owner = Window.GetWindow(this);
            converterWindow.Show();
        }
    }
}
