using System;
using System.Runtime.InteropServices;

namespace AssetsManager.Shaders
{
    /// <summary>
    /// Managed ABI for the dxbc-spirv SM5 compiler used by the game-shader preview path.
    /// The native shim is built from the exact dxbc-spirv/SPIRV-Headers revisions recorded
    /// under native/dxbc-spv and exposes only compile/free entry points.
    /// </summary>
    internal static class DxbcSpirvCompiler
    {
        private const string Library = "dxbc_spv_lol.dll";
        private const uint SpirvMagic = 0x07230203;

        [DllImport(Library, EntryPoint = "dxbc_spv_lol_compile", CallingConvention = CallingConvention.Cdecl)]
        private static extern int CompileNative(
            IntPtr data,
            nuint size,
            out IntPtr words,
            out nuint wordCount,
            out IntPtr error);

        [DllImport(Library, EntryPoint = "dxbc_spv_lol_free", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FreeNative(IntPtr pointer);

        internal static unsafe uint[] Compile(byte[] dxbc)
        {
            if (dxbc == null || dxbc.Length == 0)
                throw new InvalidOperationException("DXBC bytecode is empty.");

            fixed (byte* source = dxbc)
            {
                IntPtr words = IntPtr.Zero;
                IntPtr error = IntPtr.Zero;
                nuint wordCount = 0;
                try
                {
                    int status = CompileNative(
                        (IntPtr)source,
                        checked((nuint)dxbc.Length),
                        out words,
                        out wordCount,
                        out error);
                    if (status != 0)
                    {
                        string message = error == IntPtr.Zero
                            ? "dxbc-spirv failed without a message."
                            : Marshal.PtrToStringUTF8(error);
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(message)
                                ? "dxbc-spirv failed without a message."
                                : $"dxbc-spirv: {message.Trim()}");
                    }

                    if (words == IntPtr.Zero || wordCount == 0 || wordCount > int.MaxValue)
                        throw new InvalidOperationException("dxbc-spirv produced no SPIR-V module.");

                    int count = checked((int)wordCount);
                    var result = new uint[count];
                    new ReadOnlySpan<uint>(words.ToPointer(), count).CopyTo(result);
                    if (result.Length < 5 || result[0] != SpirvMagic)
                        throw new InvalidOperationException("dxbc-spirv produced an invalid SPIR-V module.");
                    return result;
                }
                finally
                {
                    if (words != IntPtr.Zero)
                        FreeNative(words);
                    if (error != IntPtr.Zero)
                        FreeNative(error);
                }
            }
        }
    }
}
