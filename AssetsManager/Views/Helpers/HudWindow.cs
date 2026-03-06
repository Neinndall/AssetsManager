using System;
using System.Windows;
using System.Windows.Interop;
using AssetsManager.Utils.Win;
using Material.Icons;

namespace AssetsManager.Views.Helpers
{
    /// <summary>
    /// Helper base class for all HUD-styled windows.
    /// Centralizes native Win32 interop, rounded corners, and title bar properties.
    /// Title bar buttons (Min/Max/Close) are handled via SystemCommands in the ControlTemplate.
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
            DefaultStyleKeyProperty.OverrideMetadata(typeof(HudWindow), new FrameworkPropertyMetadata(typeof(HudWindow)));
        }

        public HudWindow()
        {
            // WindowStyle controlado en el .xaml (HudWindowStyles.xaml)
            AllowsTransparency = false;

            // Register SystemCommands handlers for this window
            this.CommandBindings.Add(new System.Windows.Input.CommandBinding(SystemCommands.CloseWindowCommand, (s, e) => SystemCommands.CloseWindow(this)));
            this.CommandBindings.Add(new System.Windows.Input.CommandBinding(SystemCommands.MaximizeWindowCommand, (s, e) => SystemCommands.MaximizeWindow(this)));
            this.CommandBindings.Add(new System.Windows.Input.CommandBinding(SystemCommands.MinimizeWindowCommand, (s, e) => SystemCommands.MinimizeWindow(this)));
            this.CommandBindings.Add(new System.Windows.Input.CommandBinding(SystemCommands.RestoreWindowCommand, (s, e) => SystemCommands.RestoreWindow(this)));
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
    }
}
