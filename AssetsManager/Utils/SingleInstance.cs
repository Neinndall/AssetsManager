using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AssetsManager.Utils
{
    public static class SingleInstance
    {
        private static Mutex _mutex;
        public const string AppId = "AssetsManager";
        public static readonly uint WM_SHOW_APP = RegisterWindowMessage("AssetsManager_ShowApp");

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("shell32.dll", SetLastError = true)]
        public static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);

        public static bool EnsureSingleInstance()
        {
            const string appName = "AssetsManager";
            bool createdNew;

            _mutex = new Mutex(true, appName, out createdNew);

            if (!createdNew)
            {
                // Notify the existing instance to show itself
                PostMessage(HWND_BROADCAST, WM_SHOW_APP, IntPtr.Zero, IntPtr.Zero);
                return false;
            }

            return true; // This is the first instance
        }
    }
}
