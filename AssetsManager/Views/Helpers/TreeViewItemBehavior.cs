using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Collections;
using System.Linq;
using AssetsManager.Views.Models.Explorer;

namespace AssetsManager.Views.Helpers
{
    public static class TreeViewItemBehavior
    {
        public static readonly DependencyProperty SingleClickExpandProperty =
            DependencyProperty.RegisterAttached("SingleClickExpand", typeof(bool), typeof(TreeViewItemBehavior), new UIPropertyMetadata(false, OnSingleClickExpandChanged));

        public static bool GetSingleClickExpand(DependencyObject obj) => (bool)obj.GetValue(SingleClickExpandProperty);
        public static void SetSingleClickExpand(DependencyObject obj, bool value) => obj.SetValue(SingleClickExpandProperty, value);

        private static void OnSingleClickExpandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TreeViewItem item)
            {
                if ((bool)e.NewValue) item.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
                else item.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            }
        }

        private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Ignore if clicking on the expander toggle button
            if (sender is not TreeViewItem item || e.OriginalSource is System.Windows.Controls.Primitives.ToggleButton) return;

            // Ensure we are clicking on THIS item's header, not a child's header
            var container = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
            if (item != container || item.DataContext is not FileSystemNodeModel model) return;

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                // CTRL + Click: Toggle multi-selection without changing native selection/expansion
                model.IsMultiSelected = !model.IsMultiSelected;
                e.Handled = true;
            }
            else
            {
                // Normal Click: Clear all multi-selections
                var treeView = FindAncestor<TreeView>(item);
                if (treeView != null) ClearAllMultiSelected(treeView.ItemsSource);

                // Handle single click expand logic
                if (item.HasItems)
                {
                    item.IsSelected = true;
                    item.IsExpanded = !item.IsExpanded;
                    e.Handled = true;
                }
            }
        }

        private static void ClearAllMultiSelected(IEnumerable nodes)
        {
            if (nodes == null) return;
            foreach (var node in nodes.OfType<FileSystemNodeModel>())
            {
                node.IsMultiSelected = false;
                ClearAllMultiSelected(node.Children);
            }
        }

        private static T FindAncestor<T>(DependencyObject obj) where T : DependencyObject
        {
            while (obj != null && obj is not T)
            {
                obj = (obj is Visual || obj is System.Windows.Media.Media3D.Visual3D) 
                    ? VisualTreeHelper.GetParent(obj) 
                    : LogicalTreeHelper.GetParent(obj);
            }
            return obj as T;
        }
    }
}