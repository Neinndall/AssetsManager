using System.Windows.Input;

namespace AssetsManager.Views.Helpers
{
    /// <summary>
    /// Centralizes user interaction logic to ensure consistent behavior 
    /// between TreeViews, Grids, and other selectable components.
    /// </summary>
    public static class InteractionHelper
    {
        /// <summary>
        /// Determines if the current key modifiers indicate a multi-selection intent (Control key).
        /// </summary>
        public static bool IsMultiSelectIntent()
        {
            return Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        }

        /// <summary>
        /// Determines if the current click should trigger a primary action (navigation/open).
        /// </summary>
        public static bool IsPrimaryActionIntent()
        {
            return !IsMultiSelectIntent();
        }
    }
}
