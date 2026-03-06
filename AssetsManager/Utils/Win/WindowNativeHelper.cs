using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AssetsManager.Utils.Win
{
    public static class WindowNativeHelper
    {
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWCP_ROUND = 2;

        public const int WM_ERASEBKGND = 0x0014;
        public const int WM_NCCALCSIZE = 0x0083;

        [DllImport("dwmapi.dll", PreserveSig = false)]
        private static extern void DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static void ApplyDwmRoundedCorners(IntPtr hwnd)
        {
            try
            {
                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch { }
        }

        public static void SetDwmBorderColor(IntPtr hwnd, Color color)
        {
            try
            {
                // Convert WPF Color to Win32 BGR color
                int bgrColor = (color.B << 16) | (color.G << 8) | color.R;
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref bgrColor, sizeof(int));
            }
            catch { }
        }

        public static IntPtr HandleWindowMessage(int msg, IntPtr wParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_ERASEBKGND:
                    handled = true;
                    return new IntPtr(1);

                case WM_NCCALCSIZE:
                    if (wParam == new IntPtr(1))
                    {
                        handled = true;
                        return IntPtr.Zero;
                    }
                    break;
            }
            return IntPtr.Zero;
        }
    }
}