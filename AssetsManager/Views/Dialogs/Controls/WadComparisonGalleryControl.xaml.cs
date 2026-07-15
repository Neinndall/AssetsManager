using System;
using System.Windows;
using System.Windows.Controls;
using AssetsManager.Views.Models.Wad;

namespace AssetsManager.Views.Dialogs.Controls
{
    public partial class WadComparisonGalleryControl : UserControl
    {
        public event Action<SerializableChunkDiff, bool> ItemVisibilityChanged;

        public WadComparisonGalleryControl()
        {
            InitializeComponent();
        }

        private void GalleryItem_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ListBoxItem { DataContext: SerializableChunkDiff item })
            {
                ItemVisibilityChanged?.Invoke(item, true);
            }
        }

        private void GalleryItem_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is ListBoxItem { DataContext: SerializableChunkDiff item })
            {
                ItemVisibilityChanged?.Invoke(item, false);
            }
        }
    }
}
