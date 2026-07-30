using System;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AssetsManager.Views.Helpers
{
    /// <summary>
    /// Orquestador universal de interacción y selección. 
    /// Centraliza la lógica de selección múltiple y acciones primarias (Navegación).
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

        private static readonly DependencyProperty RangeAnchorProperty =
            DependencyProperty.RegisterAttached("RangeAnchor", typeof(object), typeof(SelectionBehavior));

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
                if ((bool)e.NewValue)
                {
                    item.PreviewMouseLeftButtonDown += OnItemPreviewMouseLeftButtonDown;
                    AttachSelectionSynchronization(item);
                }
                else
                {
                    item.PreviewMouseLeftButtonDown -= OnItemPreviewMouseLeftButtonDown;
                    DetachSelectionSynchronization(item);
                }
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
            if (d is ListBoxItem item)
            {
                if ((bool)e.NewValue)
                {
                    item.PreviewMouseLeftButtonDown += OnItemPreviewMouseLeftButtonDown;
                    AttachSelectionSynchronization(item);
                }
                else
                {
                    item.PreviewMouseLeftButtonDown -= OnItemPreviewMouseLeftButtonDown;
                    DetachSelectionSynchronization(item);
                }
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

            // TreeView owns its CTRL toggle; ListBox keeps native Extended selection.
            if (item is TreeViewItem treeItem && IsRangeSelectIntent())
            {
                TreeView tree = FindAncestor<TreeView>(treeItem);
                var anchor = tree?.GetValue(RangeAnchorProperty) as ISelectableTreeNode;
                if (treeItem.DataContext is ISelectableTreeNode target &&
                    SelectTreeRange(
                        tree?.ItemsSource,
                        anchor,
                        target,
                        IsMultiSelectIntent(),
                        out bool usedAnchor))
                {
                    if (!usedAnchor) tree?.SetValue(RangeAnchorProperty, target);
                    SetItemSelected(treeItem, true);
                    e.Handled = true;
                }
                return;
            }

            if (IsMultiSelectIntent())
            {
                if (item is TreeViewItem)
                {
                    bool current = GetIsMultiSelected(item.DataContext);
                    if (SetIsMultiSelected(item.DataContext, !current))
                    {
                        FindAncestor<TreeView>(item)?.SetValue(RangeAnchorProperty, item.DataContext);
                        e.Handled = true;
                        return;
                    }
                }
            }

            // SHIFT ranges are handled by ListBox.SelectionMode=Extended.
            if (IsPrimaryActionIntent())
            {
                var rootControl = (ItemsControl)FindAncestor<TreeView>(item) ?? FindAncestor<ListBox>(item);
                if (rootControl?.ItemsSource != null)
                {
                    ClearAllMultiSelected(rootControl.ItemsSource);
                }

                if (item is TreeViewItem)
                    FindAncestor<TreeView>(item)?.SetValue(RangeAnchorProperty, item.DataContext);

                if (item is ListBoxItem listBoxItem)
                {
                    listBoxItem.Dispatcher.BeginInvoke(
                        DispatcherPriority.Input,
                        new Action(() => listBoxItem.RaiseEvent(
                            new RoutedEventArgs(PrimaryActionEvent, listBoxItem))));
                }
                else
                {
                    item.RaiseEvent(new RoutedEventArgs(PrimaryActionEvent, item));
                }

                if (item is TreeViewItem tvi)
                {
                    ApplyPrimaryTreeAction(tvi);
                    e.Handled = true;
                }
            }
        }

        internal static bool SelectTreeRange(
            IEnumerable roots,
            ISelectableTreeNode anchor,
            ISelectableTreeNode target,
            bool additive,
            out bool usedAnchor)
        {
            usedAnchor = false;
            if (roots == null || target == null) return false;
            var visible = new System.Collections.Generic.List<ISelectableTreeNode>();
            CollectVisibleTreeNodes(roots, visible);
            int targetIndex = visible.IndexOf(target);
            if (targetIndex < 0) return false;

            int anchorIndex = anchor == null ? -1 : visible.IndexOf(anchor);
            if (!additive) ClearAllMultiSelected(roots);
            if (anchorIndex < 0)
            {
                target.IsMultiSelected = true;
                return true;
            }

            usedAnchor = true;
            int start = Math.Min(anchorIndex, targetIndex);
            int end = Math.Max(anchorIndex, targetIndex);
            for (int index = start; index <= end; index++)
                visible[index].IsMultiSelected = true;
            return true;
        }

        private static void CollectVisibleTreeNodes(
            IEnumerable nodes,
            System.Collections.Generic.ICollection<ISelectableTreeNode> visible)
        {
            foreach (object item in nodes)
            {
                if (item is not ISelectableTreeNode node || !node.IsSelectionVisible) continue;
                visible.Add(node);
                if (node.IsExpanded && node.SelectionChildren != null)
                    CollectVisibleTreeNodes(node.SelectionChildren, visible);
            }
        }

        internal static void ApplyPrimaryTreeAction(TreeViewItem item)
        {
            if (item.HasItems)
            {
                item.IsExpanded = !item.IsExpanded;
            }

            item.IsSelected = true;
            SynchronizeSelectionState(item.DataContext, true);
            item.Focus();
        }

        private static void AttachSelectionSynchronization(FrameworkElement item)
        {
            item.AddHandler(Selector.SelectedEvent, new RoutedEventHandler(OnItemSelected));
            item.AddHandler(Selector.UnselectedEvent, new RoutedEventHandler(OnItemUnselected));
            item.DataContextChanged += OnItemDataContextChanged;
        }

        private static void DetachSelectionSynchronization(FrameworkElement item)
        {
            item.RemoveHandler(Selector.SelectedEvent, new RoutedEventHandler(OnItemSelected));
            item.RemoveHandler(Selector.UnselectedEvent, new RoutedEventHandler(OnItemUnselected));
            item.DataContextChanged -= OnItemDataContextChanged;
        }

        private static void OnItemSelected(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(sender, e.OriginalSource) && sender is FrameworkElement item)
            {
                SynchronizeSelectionState(item.DataContext, true);
            }
        }

        private static void OnItemUnselected(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(sender, e.OriginalSource) && sender is FrameworkElement item)
            {
                SynchronizeSelectionState(item.DataContext, false);
            }
        }

        private static void OnItemDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            SynchronizeSelectionState(e.OldValue, false);
            if (sender is FrameworkElement item)
            {
                SynchronizeSelectionState(e.NewValue, IsItemSelected(item));
            }
        }

        internal static void SynchronizeSelectionState(object dataContext, bool isSelected)
        {
            if (dataContext is ISelectable selectable)
            {
                selectable.IsSelected = isSelected;
            }
        }

        #region Helpers

        public static bool IsMultiSelectIntent()
        {
            return Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        }

        public static bool IsRangeSelectIntent()
        {
            return IsRangeSelectIntent(Keyboard.Modifiers);
        }

        public static bool IsPrimaryActionIntent()
        {
            return IsPrimaryActionIntent(Keyboard.Modifiers);
        }

        internal static bool IsRangeSelectIntent(ModifierKeys modifiers) =>
            modifiers.HasFlag(ModifierKeys.Shift);

        internal static bool IsPrimaryActionIntent(ModifierKeys modifiers) =>
            !modifiers.HasFlag(ModifierKeys.Control) &&
            !modifiers.HasFlag(ModifierKeys.Shift);

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
            SynchronizeSelectionState(container.DataContext, value);
            if (value) container.Focus();
        }

        private static bool GetIsMultiSelected(object dc)
        {
            return dc is IMultiSelectable ms && ms.IsMultiSelected;
        }

        private static bool SetIsMultiSelected(object dc, bool value)
        {
            if (dc is IMultiSelectable ms)
            {
                ms.IsMultiSelected = value;
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

                if (node is IMultiSelectable ms)
                {
                    if (ms.IsMultiSelected) ms.IsMultiSelected = false;
                }

                if (node is ISelectableTreeNode treeNode && treeNode.SelectionChildren != null)
                {
                    ClearAllMultiSelected(treeNode.SelectionChildren);
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
