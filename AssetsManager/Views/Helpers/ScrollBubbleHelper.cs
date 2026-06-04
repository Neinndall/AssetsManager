using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AssetsManager.Views.Helpers
{
    /// <summary>
    /// Attached property that forces every wheel event over a <see cref="ScrollViewer"/>'s
    /// descendants to scroll that viewer, never the inner controls.
    ///
    /// Uses the <b>tunneling</b> (Preview) phase so the event is intercepted before it
    /// reaches nested controls (Sliders, ComboBoxes, ListBoxes, inner ScrollViewers, etc.)
    /// that would otherwise mark it as handled and stop the outer from scrolling.
    ///
    /// Apply on a root ScrollViewer with <c>helpers:ScrollBubbleHelper.BubbleScroll="True"</c>
    /// and any wheel movement over its descendants will always scroll that viewer.
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
            if (sender is not ScrollViewer sv) return;

            if (e.Delta > 0) sv.LineUp();
            else sv.LineDown();
            e.Handled = true;
        }
    }
}
