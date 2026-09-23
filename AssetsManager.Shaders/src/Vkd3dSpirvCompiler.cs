using System;
using System.Runtime.InteropServices;

namespace AssetsManager.Shaders
{
    /// <summary>
    /// Thin managed ABI for the vkd3d-shader native carried by ShadowDusk.HLSL.
    /// NuGet owns native distribution; this project owns only the call contract.
    /// </summary>
    internal static class Vkd3dSpirvCompiler
    {
        private const string Vkd3dLibrary = "libvkd3d-shader-1.dll";
        private const uint SpirvMagic = 0x07230203;
        private const int SourceDxbcTpf = 1;
        private const int TargetSpirvBinary = 1;
        private const int LogErrorsOnly = 1;

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct ShaderCode
        {
            internal readonly IntPtr Code;
            internal readonly nuint Size;

            internal ShaderCode(IntPtr code, nuint size)
            {
                Code = code;
                Size = size;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct CompileInfo
        {
            internal readonly int Type;
            internal readonly IntPtr Next;
            internal readonly ShaderCode Source;
            internal readonly int SourceType;
            internal readonly int TargetType;
            internal readonly IntPtr Options;
            internal readonly uint OptionCount;
            internal readonly int LogLevel;
            internal readonly IntPtr SourceName;

            internal CompileInfo(ShaderCode source)
            {
                Type = 0;
                Next = IntPtr.Zero;
                Source = source;
                SourceType = SourceDxbcTpf;
                TargetType = TargetSpirvBinary;
                Options = IntPtr.Zero;
                OptionCount = 0;
                LogLevel = LogErrorsOnly;
                SourceName = IntPtr.Zero;
            }
        }

        [DllImport(Vkd3dLibrary, EntryPoint = "vkd3d_shader_compile", CallingConvention = CallingConvention.Cdecl)]
        private static extern int CompileNative(in CompileInfo info, out ShaderCode output, out IntPtr messages);

        [DllImport(Vkd3dLibrary, EntryPoint = "vkd3d_shader_free_shader_code", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FreeShaderCode(ref ShaderCode code);

        [DllImport(Vkd3dLibrary, EntryPoint = "vkd3d_shader_free_messages", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FreeMessages(IntPtr messages);

        internal static unsafe uint[] Compile(byte[] dxbc)
        {
            if (dxbc == null || dxbc.Length == 0)
                throw new InvalidOperationException("DXBC bytecode is empty.");

            fixed (byte* source = dxbc)
            {
                var info = new CompileInfo(new ShaderCode((IntPtr)source, checked((nuint)dxbc.Length)));
                int status = CompileNative(info, out ShaderCode output, out IntPtr messages);
                string diagnostics = messages == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(messages);
                if (messages != IntPtr.Zero)
                    FreeMessages(messages);

                if (status != 0)
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(diagnostics)
                            ? $"vkd3d-shader failed with status {status}."
                            : $"vkd3d-shader: {diagnostics.Trim()}");

                if (output.Code == IntPtr.Zero || output.Size == 0 || output.Size % sizeof(uint) != 0)
                {
                    if (output.Code != IntPtr.Zero)
                        FreeShaderCode(ref output);
                    throw new InvalidOperationException("vkd3d-shader produced no SPIR-V module.");
                }

                try
                {
                    int wordCount = checked((int)(output.Size / sizeof(uint)));
                    var result = new uint[wordCount];
                    new ReadOnlySpan<uint>(output.Code.ToPointer(), wordCount).CopyTo(result);
                    if (result.Length < 5 || result[0] != SpirvMagic)
                        throw new InvalidOperationException("vkd3d-shader produced an invalid SPIR-V module.");
                    return result;
                }
                finally
                {
                    FreeShaderCode(ref output);
                }
            }
        }
    }
}