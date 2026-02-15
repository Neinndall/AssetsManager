using System;
using System.Collections.ObjectModel;
using System.Windows;
using AssetsManager.Views.Models.Shared;
using AssetsManager.Views.Dialogs;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using AssetsManager.Utils.Framework;

namespace AssetsManager.Services.Explorer
{
    public class ImageMergerService
    {
        private readonly IServiceProvider _serviceProvider;
        private ImageMergerWindow _activeWindow;

        public ObservableRangeCollection<ImageMergerItem> Items { get; } = new ObservableRangeCollection<ImageMergerItem>();

        public ImageMergerService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

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

            _activeWindow = _serviceProvider.GetRequiredService<ImageMergerWindow>();
            _activeWindow.Owner = Application.Current.MainWindow;
            _activeWindow.Show();
        }

        public void Clear()
        {
            Items.Clear();
        }
    }
}
