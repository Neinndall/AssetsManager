namespace AssetsManager.Tests.Support
{
    /// <summary>
    /// A hidden window with a desktop OpenGL core context current on the calling thread, for GPU checks of
    /// translated game shaders without the viewer.
    /// </summary>
    internal sealed class HiddenWglContext : System.IDisposable
    {
        private const uint ClassOwnDeviceContext = 0x0020;
        private const uint WindowExToolWindow = 0x00000080;
        private const uint WindowPopup = 0x80000000;
        private const uint PixelFormatDrawToWindow = 0x00000004;
        private const uint PixelFormatSupportOpenGl = 0x00000020;
        private const uint PixelFormatDoubleBuffer = 0x00000001;
        private const byte PixelTypeRgba = 0;
        private const sbyte MainPlane = 0;
        private const int WglContextMajorVersion = 0x2091;
        private const int WglContextMinorVersion = 0x2092;
        private const int WglContextProfileMask = 0x9126;
        private const int WglContextCoreProfileBit = 0x00000001;

        private readonly string _className = "AssetsManager.ParticleShaderCompile." + System.Guid.NewGuid().ToString("N");
        private readonly System.IntPtr _instance = GetModuleHandle(null);
        private readonly WindowProcedure _windowProcedure = DefWindowProc;
        private System.IntPtr _window;
        private System.IntPtr _deviceContext;
        private System.IntPtr _legacyContext;
        private System.IntPtr _renderContext;
        private bool _disposed;

        internal HiddenWglContext()
        {
            try
            {
                CreateWindowAndContext();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal System.IntPtr GetProcAddress(string name)
        {
            System.IntPtr address = WglGetProcAddress(name);
            long value = address.ToInt64();
            return address == System.IntPtr.Zero || value is 1 or 2 or 3 or -1
                ? NativeGetProcAddress(GetModuleHandle("opengl32.dll"), name)
                : address;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            WglMakeCurrent(System.IntPtr.Zero, System.IntPtr.Zero);
            if (_renderContext != System.IntPtr.Zero)
                WglDeleteContext(_renderContext);
            if (_legacyContext != System.IntPtr.Zero)
                WglDeleteContext(_legacyContext);
            if (_deviceContext != System.IntPtr.Zero && _window != System.IntPtr.Zero)
                ReleaseDC(_window, _deviceContext);
            if (_window != System.IntPtr.Zero)
                DestroyWindow(_window);
            if (!string.IsNullOrWhiteSpace(_className))
                UnregisterClass(_className, _instance);
        }

        private void CreateWindowAndContext()
        {
            var windowClass = new WindowClassEx
            {
                Size = checked((uint)System.Runtime.InteropServices.Marshal.SizeOf<WindowClassEx>()),
                Style = ClassOwnDeviceContext,
                WindowProcedure = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_windowProcedure),
                Instance = _instance,
                ClassName = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(_className)
            };
            try
            {
                if (RegisterClassEx(ref windowClass) == 0)
                    ThrowLastWin32Error("RegisterClassExW");
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(windowClass.ClassName);
            }

            _window = CreateWindowEx(
                WindowExToolWindow,
                _className,
                "AssetsManager shader validation",
                WindowPopup,
                -32000,
                -32000,
                1,
                1,
                System.IntPtr.Zero,
                System.IntPtr.Zero,
                _instance,
                System.IntPtr.Zero);
            if (_window == System.IntPtr.Zero)
                ThrowLastWin32Error("CreateWindowExW");

            _deviceContext = GetDC(_window);
            if (_deviceContext == System.IntPtr.Zero)
                ThrowLastWin32Error("GetDC");

            var descriptor = new PixelFormatDescriptor
            {
                Size = checked((ushort)System.Runtime.InteropServices.Marshal.SizeOf<PixelFormatDescriptor>()),
                Version = 1,
                Flags = PixelFormatDrawToWindow | PixelFormatSupportOpenGl | PixelFormatDoubleBuffer,
                PixelType = PixelTypeRgba,
                ColorBits = 32,
                AlphaBits = 8,
                DepthBits = 24,
                StencilBits = 8,
                LayerType = MainPlane
            };
            int format = ChoosePixelFormat(_deviceContext, ref descriptor);
            if (format == 0)
                ThrowLastWin32Error("ChoosePixelFormat");
            if (!SetPixelFormat(_deviceContext, format, ref descriptor))
                ThrowLastWin32Error("SetPixelFormat");

            _legacyContext = WglCreateContext(_deviceContext);
            if (_legacyContext == System.IntPtr.Zero)
                ThrowLastWin32Error("wglCreateContext");
            if (!WglMakeCurrent(_deviceContext, _legacyContext))
                ThrowLastWin32Error("wglMakeCurrent(legacy)");

            System.IntPtr createContextAddress = GetProcAddress("wglCreateContextAttribsARB");
            if (createContextAddress == System.IntPtr.Zero)
                throw new System.InvalidOperationException("WGL_ARB_create_context is unavailable.");
            var createContext = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<WglCreateContextAttribs>(
                createContextAddress);
            int[] attributes =
            {
                WglContextMajorVersion, 3,
                WglContextMinorVersion, 3,
                WglContextProfileMask, WglContextCoreProfileBit,
                0
            };
            _renderContext = createContext(_deviceContext, System.IntPtr.Zero, attributes);
            if (_renderContext == System.IntPtr.Zero)
                throw new System.InvalidOperationException("WGL failed to create an OpenGL 3.3 core context.");
            if (!WglMakeCurrent(_deviceContext, _renderContext))
                ThrowLastWin32Error("wglMakeCurrent(core)");
            WglDeleteContext(_legacyContext);
            _legacyContext = System.IntPtr.Zero;
        }

        private static void ThrowLastWin32Error(string operation) =>
            throw new System.ComponentModel.Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                operation + " failed.");

        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
        private delegate System.IntPtr WindowProcedure(
            System.IntPtr window,
            uint message,
            System.IntPtr wParam,
            System.IntPtr lParam);

        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
        private delegate System.IntPtr WglCreateContextAttribs(
            System.IntPtr deviceContext,
            System.IntPtr shareContext,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPArray)] int[] attributes);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct WindowClassEx
        {
            internal uint Size;
            internal uint Style;
            internal System.IntPtr WindowProcedure;
            internal int ClassExtra;
            internal int WindowExtra;
            internal System.IntPtr Instance;
            internal System.IntPtr Icon;
            internal System.IntPtr Cursor;
            internal System.IntPtr Background;
            internal System.IntPtr MenuName;
            internal System.IntPtr ClassName;
            internal System.IntPtr SmallIcon;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct PixelFormatDescriptor
        {
            internal ushort Size;
            internal ushort Version;
            internal uint Flags;
            internal byte PixelType;
            internal byte ColorBits;
            internal byte RedBits;
            internal byte RedShift;
            internal byte GreenBits;
            internal byte GreenShift;
            internal byte BlueBits;
            internal byte BlueShift;
            internal byte AlphaBits;
            internal byte AlphaShift;
            internal byte AccumBits;
            internal byte AccumRedBits;
            internal byte AccumGreenBits;
            internal byte AccumBlueBits;
            internal byte AccumAlphaBits;
            internal byte DepthBits;
            internal byte StencilBits;
            internal byte AuxiliaryBuffers;
            internal sbyte LayerType;
            internal byte Reserved;
            internal uint LayerMask;
            internal uint VisibleMask;
            internal uint DamageMask;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern System.IntPtr GetModuleHandle(string moduleName);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern System.IntPtr NativeGetProcAddress(System.IntPtr module, string procName);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool UnregisterClass(string className, System.IntPtr instance);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "DefWindowProcW", SetLastError = true)]
        private static extern System.IntPtr DefWindowProc(
            System.IntPtr window,
            uint message,
            System.IntPtr wParam,
            System.IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern System.IntPtr CreateWindowEx(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            System.IntPtr parent,
            System.IntPtr menu,
            System.IntPtr instance,
            System.IntPtr parameter);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetDC", SetLastError = true)]
        private static extern System.IntPtr GetDC(System.IntPtr window);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "ReleaseDC", SetLastError = true)]
        private static extern int ReleaseDC(System.IntPtr window, System.IntPtr deviceContext);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool DestroyWindow(System.IntPtr window);

        [System.Runtime.InteropServices.DllImport("gdi32.dll", EntryPoint = "ChoosePixelFormat", SetLastError = true)]
        private static extern int ChoosePixelFormat(System.IntPtr deviceContext, ref PixelFormatDescriptor descriptor);

        [System.Runtime.InteropServices.DllImport("gdi32.dll", EntryPoint = "SetPixelFormat", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetPixelFormat(
            System.IntPtr deviceContext,
            int format,
            ref PixelFormatDescriptor descriptor);

        [System.Runtime.InteropServices.DllImport("opengl32.dll", EntryPoint = "wglCreateContext", SetLastError = true)]
        private static extern System.IntPtr WglCreateContext(System.IntPtr deviceContext);

        [System.Runtime.InteropServices.DllImport("opengl32.dll", EntryPoint = "wglDeleteContext", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool WglDeleteContext(System.IntPtr context);

        [System.Runtime.InteropServices.DllImport("opengl32.dll", EntryPoint = "wglMakeCurrent", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool WglMakeCurrent(System.IntPtr deviceContext, System.IntPtr context);

        [System.Runtime.InteropServices.DllImport("opengl32.dll", EntryPoint = "wglGetProcAddress", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern System.IntPtr WglGetProcAddress(string procName);
    }
}
