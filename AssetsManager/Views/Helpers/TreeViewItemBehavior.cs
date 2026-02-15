using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Collections;
using System.Windows.Controls.Primitives;
using AssetsManager.Views.Models.Explorer;
using AssetsManager.Views.Helpers;

namespace AssetsManager.Views.Helpers
{
    /// <summary>
    /// Proporciona comportamientos adjuntos para mejorar la interacción con los controles TreeViewItem.
    /// Incluye soporte para expansión con un solo clic y selección múltiple inteligente.
    /// </summary>
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
            // 1. Ignorar clics en el botón de expansión (ToggleButton) para no interferir con el comportamiento nativo
            if (sender is not TreeViewItem item || e.OriginalSource is ToggleButton) return;

            // 2. Seguridad: Asegurar que el clic es en el encabezado de ESTE ítem y no en uno de sus hijos
            if (FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) != item) return;

            // 3. Lógica de Selección Múltiple (Usa el Helper Centralizado)
            if (InteractionHelper.IsMultiSelectIntent())
            {
                if (item.DataContext is FileSystemNodeModel model)
                {
                    model.IsMultiSelected = !model.IsMultiSelected;
                    e.Handled = true; // Bloqueamos la selección nativa para mantener nuestra multi-selección
                    return;
                }
            }

            // 4. Lógica de Navegación y Expansión (Clic Normal / Primary Action)
            if (InteractionHelper.IsPrimaryActionIntent())
            {
                // Limpiar estados de multi-selección solo si estamos en un árbol compatible
                var treeView = FindAncestor<TreeView>(item);
                if (treeView?.ItemsSource != null)
                {
                    ClearAllMultiSelected(treeView.ItemsSource);
                }

                // Si es un contenedor, alternamos su expansión
                if (item.HasItems)
                {
                    item.IsSelected = true;
                    item.IsExpanded = !item.IsExpanded;
                    e.Handled = true; // Marcamos como manejado para evitar que el TreeView haga su expansión estándar
                }
                // Si es un nodo hoja (archivo), dejamos que el evento fluya para que se seleccione y se abra/previsualice
            }
        }

        /// <summary>
        /// Limpia de forma recursiva el estado de multi-selección.
        /// Optimizado para salir de inmediato si el árbol no utiliza modelos compatibles.
        /// </summary>
        private static void ClearAllMultiSelected(IEnumerable nodes)
        {
            if (nodes == null) return;

            foreach (var node in nodes)
            {
                if (node is FileSystemNodeModel fileNode)
                {
                    // Solo actualizamos si es necesario para evitar notificaciones de cambio redundantes
                    if (fileNode.IsMultiSelected) fileNode.IsMultiSelected = false;
                    
                    if (fileNode.Children?.Count > 0)
                        ClearAllMultiSelected(fileNode.Children);
                }
                else
                {
                    // Si el primer nivel no es compatible, asumimos que el resto del árbol tampoco
                    // Esto evita procesar recursivamente árboles de resultados o logs
                    break;
                }
            }
        }

        /// <summary>
        /// Busca un ancestro del tipo especificado en el árbol visual o lógico.
        /// </summary>
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