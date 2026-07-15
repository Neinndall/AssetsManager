using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AssetsManager.Views.Models.Wad;

namespace AssetsManager.Views.Dialogs.Controls
{
    public partial class WadComparisonGalleryControl : UserControl
    {
        private readonly HashSet<SerializableChunkDiff> _realizedItems = new();
        private bool _refreshQueued;

        public event Action<SerializableChunkDiff, bool> ItemVisibilityChanged;

        public WadComparisonGalleryControl()
        {
            InitializeComponent();
        }

        private void GalleryItem_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ListBoxItem { DataContext: SerializableChunkDiff item })
            {
                _realizedItems.Add(item);
                ItemVisibilityChanged?.Invoke(item, true);
            }
        }

        private void GalleryItem_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is ListBoxItem { DataContext: SerializableChunkDiff item })
            {
                _realizedItems.Remove(item);
                ItemVisibilityChanged?.Invoke(item, false);
            }
        }

        public void LoadRealizedItems()
        {
            var currentItems = new HashSet<SerializableChunkDiff>();
            CollectRealizedItems(GalleryListBox, currentItems);

            foreach (var previousItem in _realizedItems)
            {
                if (!currentItems.Contains(previousItem))
                {
                    ItemVisibilityChanged?.Invoke(previousItem, false);
                }
            }

            foreach (var currentItem in currentItems)
            {
                ItemVisibilityChanged?.Invoke(currentItem, true);
            }

            _realizedItems.Clear();
            _realizedItems.UnionWith(currentItems);
        }

        private void GalleryListBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_refreshQueued) return;

            _refreshQueued = true;
            Dispatcher.InvokeAsync(() =>
            {
                _refreshQueued = false;
                LoadRealizedItems();
            }, DispatcherPriority.Loaded);
        }

        private static void CollectRealizedItems(
            DependencyObject parent,
            HashSet<SerializableChunkDiff> realizedItems)
        {
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is ListBoxItem { IsLoaded: true, DataContext: SerializableChunkDiff item })
                {
                    realizedItems.Add(item);
                    continue;
                }

                CollectRealizedItems(child, realizedItems);
            }
        }
    }
}
