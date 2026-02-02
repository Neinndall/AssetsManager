using System;
using System.Collections.ObjectModel;
using System.Windows;
using AssetsManager.Views.Models.Shared;
using AssetsManager.Views.Dialogs;
using System.Linq;

namespace AssetsManager.Services.Explorer
{
    public class ImageMergerService
    {
        private static ImageMergerService _instance;
        public static ImageMergerService Instance => _instance ?? (_instance = new ImageMergerService());

        public ObservableCollection<ImageMergerItem> Items { get; } = new ObservableCollection<ImageMergerItem>();

        private ImageMergerWindow _activeWindow;

        private ImageMergerService() { }

        public void AddItem(ImageMergerItem item)
        {
            if (Items.Any(i => i.Path == item.Path)) return;
            
            Application.Current.Dispatcher.Invoke(() => {
                Items.Add(item);
                ShowWindow();
            });
        }

        public void ShowWindow()
        {
            if (_activeWindow != null && _activeWindow.IsLoaded)
            {
                _activeWindow.Activate();
                if (_activeWindow.WindowState == WindowState.Minimized)
                    _activeWindow.WindowState = WindowState.Normal;
                return;
            }

            _activeWindow = new ImageMergerWindow();
            _activeWindow.Owner = Application.Current.MainWindow;
            _activeWindow.Show();
        }

        public void Clear()
        {
            Items.Clear();
        }
    }
}
