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

        public static readonly DependencyProperty ShowMinimizeButtonProperty =
            DependencyProperty.Register("ShowMinimizeButton", typeof(bool), typeof(HudWindow), new PropertyMetadata(true));

        public static readonly DependencyProperty ShowMaximizeButtonProperty =
            DependencyProperty.Register("ShowMaximizeButton", typeof(bool), typeof(HudWindow), new PropertyMetadata(true));

        public static readonly DependencyProperty MaximizedMarginProperty =
            DependencyProperty.Register("MaximizedMargin", typeof(Thickness), typeof(HudWindow), new PropertyMetadata(new Thickness(0)));

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

        public bool ShowMinimizeButton
        {
            get => (bool)GetValue(ShowMinimizeButtonProperty);
            set => SetValue(ShowMinimizeButtonProperty, value);
        }

        public bool ShowMaximizeButton
        {
            get => (bool)GetValue(ShowMaximizeButtonProperty);
            set => SetValue(ShowMaximizeButtonProperty, value);
        }

        public Thickness MaximizedMargin
        {
            get => (Thickness)GetValue(MaximizedMarginProperty);
            set => SetValue(MaximizedMarginProperty, value);
        }

        static HudWindow()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(HudWindow), new FrameworkPropertyMetadata(typeof(HudWindow)));
        }

        public HudWindow()
        {
            this.WindowStyle = WindowStyle.SingleBorderWindow;
            this.AllowsTransparency = false;

            this.StateChanged += HudWindow_StateChanged;

            this.CommandBindings.Add(new System.Windows.Input.CommandBinding(SystemCommands.CloseWindowCommand, (s, e) => SystemCommands.CloseWindow(this)));
            this.CommandBindings.Add(new System.Windows.Input.CommandBinding(SystemCommands.MaximizeWindowCommand, (s, e) => SystemCommands.MaximizeWindow(this)));
            this.CommandBindings.Add(new System.Windows.Input.CommandBinding(SystemCommands.MinimizeWindowCommand, (s, e) => SystemCommands.MinimizeWindow(this)));
            this.CommandBindings.Add(new System.Windows.Input.CommandBinding(SystemCommands.RestoreWindowCommand, (s, e) => SystemCommands.RestoreWindow(this)));
        }

        private void HudWindow_StateChanged(object sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
            {
                // Ajuste de precisión: 7px compensan el desbordamiento de WindowChrome
                // sin que la ventana parezca encogida hacia adentro.
                this.MaximizedMargin = new Thickness(7);
            }
            else
            {
                this.MaximizedMargin = new Thickness(0);
            }
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