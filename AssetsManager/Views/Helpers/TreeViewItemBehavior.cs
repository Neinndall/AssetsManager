
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AssetsManager.Views.Helpers
{
    public static class TreeViewItemBehavior
    {
        public static bool GetSingleClickExpand(DependencyObject obj)
        {
            return (bool)obj.GetValue(SingleClickExpandProperty);
        }

        public static void SetSingleClickExpand(DependencyObject obj, bool value)
        {
            obj.SetValue(SingleClickExpandProperty, value);
        }

        public static readonly DependencyProperty SingleClickExpandProperty =
            DependencyProperty.RegisterAttached(
                "SingleClickExpand",
                typeof(bool),
                typeof(TreeViewItemBehavior),
                new UIPropertyMetadata(false, OnSingleClickExpandChanged));

        private static void OnSingleClickExpandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TreeViewItem item)
            {
                if ((bool)e.NewValue)
                {
                    item.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
                }
                else
                {
                    item.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
                }
            }
        }

        private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var item = sender as TreeViewItem;
            if (item == null) return;

            var clickedItemContainer = GetContainingTreeViewItem(e.OriginalSource as DependencyObject);

            if (item != clickedItemContainer)
            {
                return;
            }

            if (e.OriginalSource is System.Windows.Controls.Primitives.ToggleButton)
            {
                return;
            }

            if (item.HasItems)
            {
                item.IsSelected = true;
                item.IsExpanded = !item.IsExpanded;
                e.Handled = true;
            }
        }

        private static TreeViewItem GetContainingTreeViewItem(DependencyObject source)
        {
            while (source != null && !(source is TreeViewItem))
            {
                if (source is Visual || source is System.Windows.Media.Media3D.Visual3D)
                {
                    source = VisualTreeHelper.GetParent(source);
                }
                else
                {
                    source = LogicalTreeHelper.GetParent(source);
                }
            }
            return source as TreeViewItem;
        }
    }
}
