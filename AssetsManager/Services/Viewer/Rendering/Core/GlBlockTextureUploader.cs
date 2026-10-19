using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AssetsManager.Services.Viewer.Resources.Textures;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.Core;

/// <summary>Uploads authored S3TC blocks when the current context supports the requested sampling space.</summary>
internal static class GlBlockTextureUploader
{
    private static readonly ConditionalWeakTable<GL, HashSet<uint>> Supported = new();

    internal static uint Format(bool bc3, bool srgb) =>
        srgb ? (bc3 ? 0x8C4Fu : 0x8C4Du) : (bc3 ? 0x83F3u : 0x83F1u);

    internal static bool TryUpload(GL gl, BlockTextureData blocks, bool srgb, bool array = false)
    {
        if (blocks == null) return false;
        uint format = Format(blocks.IsBc3, srgb);
        var supported = Supported.GetValue(gl, static context =>
        {
            context.GetInteger(GLEnum.NumCompressedTextureFormats, out int count);
            var formats = new int[count];
            if (count > 0) context.GetInteger(GLEnum.CompressedTextureFormats, formats.AsSpan());
            var result = new HashSet<uint>();
            foreach (int item in formats) result.Add((uint)item);
            // Some drivers list only the original RGB S3TC formats here. The extension also guarantees RGBA.
            if (context.IsExtensionPresent("GL_EXT_texture_compression_s3tc"))
            {
                result.Add(Format(false, false)); result.Add(Format(true, false));
                if (!GlShaderCompiler.UsesEmbeddedProfile(context) ||
                    context.IsExtensionPresent("GL_EXT_texture_compression_s3tc_srgb"))
                {
                    result.Add(Format(false, true)); result.Add(Format(true, true));
                }
            }
            return result;
        });
        if (!supported.Contains(format)) return false;
        for (int level = 0; level < blocks.Levels.Count; level++)
        {
            var mip = blocks.Levels[level];
            if (array)
                gl.CompressedTexImage3D(TextureTarget.Texture2DArray, level, (InternalFormat)format,
                    (uint)mip.Width, (uint)mip.Height, 1, 0, (uint)mip.Data.Length, mip.Data.AsSpan());
            else
                gl.CompressedTexImage2D(TextureTarget.Texture2D, level, (InternalFormat)format,
                    (uint)mip.Width, (uint)mip.Height, 0, (uint)mip.Data.Length, mip.Data.AsSpan());
        }
        return true;
    }
}
