using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AssetsManager.Utils.Win
{
    /// <summary>
    /// Utility class to handle Win32/DWM interop for modern HUD windows.
    /// Manages rounded corners (Win11), anti-flicker, and seamless resizing.
    /// </summary>
    public static class WindowNativeHelper
    {
        // ──────────────────────────────────────────────────────────────────────
        // Win32 / DWM Constants
        // ──────────────────────────────────────────────────────────────────────

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;          // Rounded corners (Win11)
        private const int DWMWCP_DONOTROUND = 1;     // Square corners

        public const int WM_ERASEBKGND = 0x0014;
        public const int WM_NCCALCSIZE = 0x0083;

        [DllImport("dwmapi.dll", PreserveSig = false)]
        private static extern void DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        // ──────────────────────────────────────────────────────────────────────
        // Public Methods
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Applies DWM rounded corners via DWMWA_WINDOW_CORNER_PREFERENCE (Windows 11+).
        /// Silently ignored on earlier OS versions.
        /// </summary>
        public static void ApplyDwmRoundedCorners(IntPtr hwnd)
        {
            try
            {
                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch
            {
                // Not Windows 11 or DWM not available — ignore; WPF CornerRadius handles the visual.
            }
        }

        /// <summary>
        /// Handles common window messages for fluid HUD behavior.
        /// Call this from your WndProc.
        /// </summary>
        public static IntPtr HandleWindowMessage(int msg, IntPtr wParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_ERASEBKGND:
                    // Prevent white flash before WPF draws its own content.
                    handled = true;
                    return new IntPtr(1);

                case WM_NCCALCSIZE:
                    // Eliminates the hidden NC border that causes resize flickering artifacts
                    // when using WindowStyle=SingleBorderWindow with WindowChrome.
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
