using System;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AssetsManager.Views.Models.Dialogs.Controls;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Models.Wad;

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
                // Limpiar selección múltiple en todo el árbol/lista
                var rootControl = (ItemsControl)FindAncestor<TreeView>(item) ?? FindAncestor<ListBox>(item);
                if (rootControl?.ItemsSource != null)
                {
                    ClearAllMultiSelected(rootControl.ItemsSource);
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
            if (dc is FileSystemNodeModel fsm) return fsm.IsMultiSelected;
            if (dc is WadGroupViewModel wgv) return wgv.IsMultiSelected;
            if (dc is DiffTypeGroupViewModel dtgv) return dtgv.IsMultiSelected;
            if (dc is SerializableChunkDiff scd) return scd.IsMultiSelected;
            return false;
        }

        private static bool SetIsMultiSelected(object dc, bool value)
        {
            if (dc is FileSystemNodeModel fsm) { fsm.IsMultiSelected = value; return true; }
            if (dc is WadGroupViewModel wgv) { wgv.IsMultiSelected = value; return true; }
            if (dc is DiffTypeGroupViewModel dtgv) { dtgv.IsMultiSelected = value; return true; }
            if (dc is SerializableChunkDiff scd) { scd.IsMultiSelected = value; return true; }
            return false;
        }

        private static void ClearAllMultiSelected(IEnumerable nodes)
        {
            if (nodes == null) return;
            foreach (var node in nodes)
            {
                if (node == null) continue;

                if (node is FileSystemNodeModel fsm)
                {
                    if (fsm.IsMultiSelected) fsm.IsMultiSelected = false;
                    if (fsm.HasLoadedChildren) ClearAllMultiSelected(fsm.Children);
                }
                else if (node is WadGroupViewModel wgv)
                {
                    if (wgv.IsMultiSelected) wgv.IsMultiSelected = false;
                    if (wgv.Types != null) ClearAllMultiSelected(wgv.Types);
                }
                else if (node is DiffTypeGroupViewModel dtgv)
                {
                    if (dtgv.IsMultiSelected) dtgv.IsMultiSelected = false;
                    if (dtgv.Diffs != null) ClearAllMultiSelected(dtgv.Diffs);
                }
                else if (node is SerializableChunkDiff scd)
                {
                    if (scd.IsMultiSelected) scd.IsMultiSelected = false;
                }
            }
        }

        private static T FindAncestor<T>(DependencyObject obj) where T : DependencyObject
        {
            while (obj != null && obj is not T)
            {
                if (obj is Visual || obj is System.Windows.Media.Media3D.Visual3D)
                {
                    obj = VisualTreeHelper.GetParent(obj);
                }
                else
                {
                    obj = LogicalTreeHelper.GetParent(obj);
                }
            }
            return obj as T;
        }

        #endregion
    }
}
