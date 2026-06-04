using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AssetsManager.Views.Helpers
{
    /// <summary>
    /// Attached property that routes wheel events to a root <see cref="ScrollViewer"/>
    /// via tunneling, while gracefully yielding to nested scrollable areas and open
    /// ComboBox dropdowns so they can scroll natively.
    /// </summary>
    public static class ScrollBubbleHelper
    {
        public static readonly DependencyProperty BubbleScrollProperty =
            DependencyProperty.RegisterAttached(
                "BubbleScroll",
                typeof(bool),
                typeof(ScrollBubbleHelper),
                new PropertyMetadata(false, OnBubbleScrollChanged));

        public static bool GetBubbleScroll(DependencyObject obj) => (bool)obj.GetValue(BubbleScrollProperty);
        public static void SetBubbleScroll(DependencyObject obj, bool value) => obj.SetValue(BubbleScrollProperty, value);

        private static void OnBubbleScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ScrollViewer sv) return;

            if ((bool)e.NewValue)
            {
                sv.AddHandler(ScrollViewer.PreviewMouseWheelEvent,
                    new MouseWheelEventHandler(ScrollViewer_PreviewMouseWheel),
                    handledEventsToo: true);
            }
            else
            {
                sv.RemoveHandler(ScrollViewer.PreviewMouseWheelEvent,
                    new MouseWheelEventHandler(ScrollViewer_PreviewMouseWheel));
            }
        }

        private static void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer outerSv) return;

            // If any ComboBox in the main visual tree has its dropdown open, let the popup
            // handle the wheel for its own internal ScrollViewer. ComboBox dropdowns live
            // in a separate HwndSource, so checking e.OriginalSource is unreliable: the
            // routed event can still reach the main tree. We instead walk the tree from
            // the outer ScrollViewer and check ComboBox.IsDropDownOpen directly.
            if (HasOpenComboBoxDropdown(outerSv))
            {
                return;
            }

            // If the wheel originated over a nested ScrollViewer that can still scroll in
            // the wheel direction, do NOT mark Handled. The inner ScrollViewer's built-in
            // MouseWheel handler will fire in the bubbling phase and scroll the inner
            // with its native sensitivity formula (which feels more natural than our
            // manual pixel-delta calculation). The inner handler also marks Handled, so
            // the outer ScrollViewer's bubble handler will short-circuit and not scroll.
            if (CanInnerScrollViewerScroll(outerSv, e))
            {
                return;
            }

            // No inner scrollable area (or it's at its limit): scroll the outer.
            // Match WPF's native scroll feel: ~3 lines per wheel notch (configurable via
            // SystemParameters.WheelScrollLines). 16px per line matches the default line
            // height used by ScrollViewer's internal scrolling.
            int lines = Math.Max(1, SystemParameters.WheelScrollLines);
            double pixelDelta = (e.Delta / 120.0) * lines * 16.0;
            double newOffset = outerSv.VerticalOffset - pixelDelta;
            newOffset = Math.Max(0, Math.Min(newOffset, outerSv.ScrollableHeight));
            outerSv.ScrollToVerticalOffset(newOffset);
            e.Handled = true;
        }

        private static bool HasOpenComboBoxDropdown(DependencyObject root)
        {
            if (root == null) return false;

            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ComboBox cb && cb.IsDropDownOpen)
                {
                    return true;
                }
                if (HasOpenComboBoxDropdown(child))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Walks the visual tree upward from <see cref="MouseWheelEventArgs.OriginalSource"/>
        /// to find a <see cref="ScrollViewer"/> that is not the outer one and that can still
        /// scroll in the wheel's direction. Returns true if such an inner viewer exists;
        /// the caller should then return without marking Handled so the inner's built-in
        /// bubble handler can scroll it natively.
        /// </summary>
        private static bool CanInnerScrollViewerScroll(ScrollViewer outerSv, MouseWheelEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source) return false;

            DependencyObject current = source;
            while (current != null)
            {
                if (ReferenceEquals(current, outerSv)) break;

                if (current is ScrollViewer innerSv)
                {
                    if (e.Delta < 0)
                    {
                        return innerSv.VerticalOffset < innerSv.ScrollableHeight - 0.5;
                    }
                    else
                    {
                        return innerSv.VerticalOffset > 0.5;
                    }
                }

                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }
    }
}
