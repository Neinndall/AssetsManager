using System.Windows.Input;

namespace AssetsManager.Utils
{
    /// <summary>
    /// Centralized shortcut keys and commands for the application.
    /// </summary>
    public static class ShortKeys
    {
        // Text Comparison / Diff Navigation
        public static readonly RoutedUICommand NextDifference = new RoutedUICommand(
            "Next Difference", "NextDifference", typeof(ShortKeys), 
            new InputGestureCollection { new KeyGesture(Key.NumPad2, ModifierKeys.Shift) });

        public static readonly RoutedUICommand PreviousDifference = new RoutedUICommand(
            "Previous Difference", "PreviousDifference", typeof(ShortKeys), 
            new InputGestureCollection { new KeyGesture(Key.NumPad8, ModifierKeys.Shift) });

        // Future shortcuts can be added here (e.g., Search, Refresh, etc.)
    }
}
