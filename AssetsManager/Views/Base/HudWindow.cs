using System;
using System.Windows;
using System.Windows.Interop;
using AssetsManager.Utils.Win;
using Material.Icons;

namespace AssetsManager.Views.Base
{
    /// <summary>
    /// Base class for all HUD-styled windows.
    /// Centralizes native Win32 interop, rounded corners, and title bar logic.
    /// </summary>
    public class HudWindow : Window
    {
        // ──────────────────────────────────────────────────────────────────────
        // Dependency Properties
        // ──────────────────────────────────────────────────────────────────────

        public static readonly DependencyProperty HeaderTitleProperty =
            DependencyProperty.Register("HeaderTitle", typeof(string), typeof(HudWindow), new PropertyMetadata(""));

        public static readonly DependencyProperty HeaderIconProperty =
            DependencyProperty.Register("HeaderIcon", typeof(MaterialIconKind), typeof(HudWindow), new PropertyMetadata(MaterialIconKind.WindowMaximize));

        public string HeaderTitle
        {
            get => (string)GetValue(HeaderTitleProperty);
            set => SetValue(HeaderTitleProperty, value);
        }

        public MaterialIconKind HeaderIcon
        {
            get => (MaterialIconKind)GetValue(HeaderIconProperty);
            set => SetValue(HeaderIconProperty, value);
        }

        // ──────────────────────────────────────────────────────────────────────
        // Lifecycle
        // ──────────────────────────────────────────────────────────────────────

        static HudWindow()
        {
            // Tells WPF to look for the style in generic.xaml or a merged dictionary
            DefaultStyleKeyProperty.OverrideMetadata(typeof(HudWindow), new FrameworkPropertyMetadata(typeof(HudWindow)));
        }

        public HudWindow()
        {
            // Standard HUD defaults
            WindowStyle = WindowStyle.SingleBorderWindow;
            AllowsTransparency = false;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var hwnd = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProc);

            // Apply Win11 native rounded corners
            WindowNativeHelper.ApplyDwmRoundedCorners(hwnd);
        }

        /// <summary>
        /// Base WndProc. Can be overridden in derived windows (like MainWindow for Tray logic).
        /// </summary>
        protected virtual IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Handle common HUD window messages (anti-flicker, NC calc, etc.)
            return WindowNativeHelper.HandleWindowMessage(msg, wParam, ref handled);
        }

        // ──────────────────────────────────────────────────────────────────────
        // Window Commands (Standardized)
        // ──────────────────────────────────────────────────────────────────────

        internal void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
        internal void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
        internal void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }
    }
}
