using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AssetsManager.Views.Helpers
{
    /// <summary>
    /// Orquestador universal de interacción y selección. 
    /// Centraliza la lógica de selección múltiple (CTRL) y acciones primarias (Navegación).
    /// </summary>
    public static class SelectionBehavior
    {
        #region Dependency Properties & Events

        public static readonly DependencyProperty SingleClickExpandProperty =
            DependencyProperty.RegisterAttached("SingleClickExpand", typeof(bool), typeof(SelectionBehavior), new UIPropertyMetadata(false, OnSingleClickExpandChanged));

        public static readonly DependencyProperty PreserveSelectionOnRightClickProperty =
            DependencyProperty.RegisterAttached("PreserveSelectionOnRightClick", typeof(bool), typeof(SelectionBehavior), new UIPropertyMetadata(false, OnPreserveSelectionOnRightClickChanged));

        public static readonly DependencyProperty EnableUnifiedSelectionProperty =
            DependencyProperty.RegisterAttached("EnableUnifiedSelection", typeof(bool), typeof(SelectionBehavior), new UIPropertyMetadata(false, OnEnableUnifiedSelectionChanged));

        // Evento que se dispara cuando el usuario realiza una acción primaria (clic sin modificadores)
        public static readonly RoutedEvent PrimaryActionEvent = 
            EventManager.RegisterRoutedEvent("PrimaryAction", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SelectionBehavior));

        public static void AddPrimaryActionHandler(DependencyObject d, RoutedEventHandler handler) => ((UIElement)d).AddHandler(PrimaryActionEvent, handler);
        public static void RemovePrimaryActionHandler(DependencyObject d, RoutedEventHandler handler) => ((UIElement)d).RemoveHandler(PrimaryActionEvent, handler);

        public static bool GetSingleClickExpand(DependencyObject obj) => (bool)obj.GetValue(SingleClickExpandProperty);
        public static void SetSingleClickExpand(DependencyObject obj, bool value) => obj.SetValue(SingleClickExpandProperty, value);

        public static bool GetPreserveSelectionOnRightClick(DependencyObject obj) => (bool)obj.GetValue(PreserveSelectionOnRightClickProperty);
        public static void SetPreserveSelectionOnRightClick(DependencyObject obj, bool value) => obj.SetValue(PreserveSelectionOnRightClickProperty, value);

        public static bool GetEnableUnifiedSelection(DependencyObject obj) => (bool)obj.GetValue(EnableUnifiedSelectionProperty);
        public static void SetEnableUnifiedSelection(DependencyObject obj, bool value) => obj.SetValue(EnableUnifiedSelectionProperty, value);

        #endregion

        #region Event Handlers Registration

        private static void OnSingleClickExpandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TreeViewItem item)
            {
                if ((bool)e.NewValue) item.PreviewMouseLeftButtonDown += OnItemPreviewMouseLeftButtonDown;
                else item.PreviewMouseLeftButtonDown -= OnItemPreviewMouseLeftButtonDown;
            }
        }

        private static void OnPreserveSelectionOnRightClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FrameworkElement item)
            {
                if ((bool)e.NewValue) item.PreviewMouseRightButtonDown += OnItemPreviewMouseRightButtonDown;
                else item.PreviewMouseRightButtonDown -= OnItemPreviewMouseRightButtonDown;
            }
        }

        private static void OnEnableUnifiedSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FrameworkElement item)
            {
                if ((bool)e.NewValue) item.PreviewMouseLeftButtonDown += OnItemPreviewMouseLeftButtonDown;
                else item.PreviewMouseLeftButtonDown -= OnItemPreviewMouseLeftButtonDown;
            }
        }

        #endregion

        private static void OnItemPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement item)
            {
                if (item is TreeViewItem && FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) != item) return;

                if (GetIsMultiSelected(item.DataContext) || IsItemSelected(item)) return;

                SetItemSelected(item, true);
                e.Handled = true;
            }
        }

        private static void OnItemPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement item || e.OriginalSource is ToggleButton) return;
            if (item is TreeViewItem && FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) != item) return;

            // 1. Lógica de Selección Múltiple (CTRL)
            if (InteractionHelper.IsMultiSelectIntent())
            {
                bool current = GetIsMultiSelected(item.DataContext);
                if (SetIsMultiSelected(item.DataContext, !current))
                {
                    e.Handled = true;
                    return;
                }
            }

            // 2. Lógica de Acción Primaria / Navegación
            if (InteractionHelper.IsPrimaryActionIntent())
            {
                var parentControl = ItemsControl.ItemsControlFromItemContainer(item);
                if (parentControl?.ItemsSource != null)
                {
                    ClearAllMultiSelected(parentControl.ItemsSource);
                }

                // Disparar el evento de acción primaria para que el control sepa que debe "ejecutar" el nodo
                item.RaiseEvent(new RoutedEventArgs(PrimaryActionEvent, item));

                if (item is TreeViewItem tvi && tvi.HasItems)
                {
                    tvi.IsExpanded = !tvi.IsExpanded;
                    tvi.IsSelected = true;
                    tvi.Focus();
                    e.Handled = true;
                }
            }
        }

        #region Helpers

        private static bool IsItemSelected(FrameworkElement container)
        {
            if (container is ListBoxItem lbi) return lbi.IsSelected;
            if (container is TreeViewItem tvi) return tvi.IsSelected;
            return false;
        }

        private static void SetItemSelected(FrameworkElement container, bool value)
        {
            if (container is ListBoxItem lbi) lbi.IsSelected = value;
            else if (container is TreeViewItem tvi) tvi.IsSelected = value;
            if (value) container.Focus();
        }

        private static bool GetIsMultiSelected(object dc)
        {
            if (dc == null) return false;
            var prop = dc.GetType().GetProperty("IsMultiSelected");
            return prop != null && (bool)prop.GetValue(dc);
        }

        private static bool SetIsMultiSelected(object dc, bool value)
        {
            if (dc == null) return false;
            var prop = dc.GetType().GetProperty("IsMultiSelected");
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(dc, value);
                return true;
            }
            return false;
        }

        private static void ClearAllMultiSelected(IEnumerable nodes)
        {
            if (nodes == null) return;
            foreach (var node in nodes)
            {
                if (node == null) continue;
                var type = node.GetType();
                var multiProp = type.GetProperty("IsMultiSelected");
                if (multiProp != null && (bool)multiProp.GetValue(node)) multiProp.SetValue(node, false);

                var childrenNames = new[] { "Children", "Types", "Diffs" };
                foreach (var name in childrenNames)
                {
                    var childProp = type.GetProperty(name);
                    if (childProp != null && typeof(IEnumerable).IsAssignableFrom(childProp.PropertyType))
                    {
                        if (childProp.GetValue(node) is IEnumerable children) ClearAllMultiSelected(children);
                        break;
                    }
                }
            }
        }

        private static T FindAncestor<T>(DependencyObject obj) where T : DependencyObject
        {
            while (obj != null && obj is not T)
            {
                obj = VisualTreeHelper.GetParent(obj);
            }
            return obj as T;
        }

        #endregion
    }
}
