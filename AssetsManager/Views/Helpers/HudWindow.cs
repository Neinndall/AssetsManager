using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AssetsManager.Utils.Win;
using Material.Icons;

namespace AssetsManager.Views.Helpers
{
    public class HudWindow : Window
    {
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

        static HudWindow()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(HudWindow), new FrameworkPropertyMetadata(typeof(HudWindow)));
        }

        public HudWindow()
        {
            this.WindowStyle = WindowStyle.SingleBorderWindow;
            this.AllowsTransparency = false;

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

            // 1. Apply native rounding (Fixes the square spikes)
            WindowNativeHelper.ApplyDwmRoundedCorners(hwnd);

            // 2. Set native border color to match our HUD theme (Fixes the grey glow)
            if (this.TryFindResource("BorderColor") is SolidColorBrush borderBrush)
            {
                WindowNativeHelper.SetDwmBorderColor(hwnd, borderBrush.Color);
            }
        }

        protected virtual IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            return WindowNativeHelper.HandleWindowMessage(msg, wParam, ref handled);
        }
    }
}