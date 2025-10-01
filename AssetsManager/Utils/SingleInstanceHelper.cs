using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace AssetsManager.Utils
{
    public static class SingleInstanceHelper
    {
        private static Mutex _mutex;
        public static readonly uint WM_SHOW_APP = RegisterWindowMessage("AssetsManager_ShowApp");

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);

        public static bool EnsureSingleInstance()
        {
            const string appName = "AssetsManager";
            bool createdNew;

            _mutex = new Mutex(true, appName, out createdNew);

            if (!createdNew)
            {
                PostMessage(HWND_BROADCAST, WM_SHOW_APP, IntPtr.Zero, IntPtr.Zero);
                return false; // Not the first instance
            }

            return true; // This is the first instance
        }

        public static void RegisterWindow(Window window, Action onShowRequest)
        {
            if (window == null || onShowRequest == null) return;

            var source = PresentationSource.FromVisual(window) as HwndSource;

            IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                if (msg == WM_SHOW_APP)
                {
                    onShowRequest();
                    handled = true;
                }
                return IntPtr.Zero;
            }

            source?.AddHook(WndProc);
        }
    }
}
