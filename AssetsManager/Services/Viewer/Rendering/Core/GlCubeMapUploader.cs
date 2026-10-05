using AssetsManager.Services.Viewer.Resources;
using System;
using AssetsManager.Services.Viewer.Vfx.Resources;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.Core
{
    /// <summary>
    /// Uploads a decoded six-face DDS cube. Shared by the preview sky, VFX reflections and map
    /// environment reflections so every cube keeps LTK's layout.
    /// </summary>
    internal static class GlCubeMapUploader
    {
        /// <param name="srgb">
        /// True for display colour (the sky); false for cubes read by translated game shaders, which
        /// decode colour themselves like LTK's NoColorSpace textures.
        /// </param>
        /// <param name="mipmaps">
        /// Generates the mip chain for shaders that pick a level by glossiness (<c>textureLod</c> on ENV_CUBE).
        /// </param>
        /// <returns>The cube texture, or 0 when the data is not a complete cube.</returns>
        internal static uint Upload(GL gl, CubeMapData cube, bool srgb, bool mipmaps = false)
        {
            ArgumentNullException.ThrowIfNull(gl);
            if (cube?.IsValid != true)
                return 0;

            uint texture = gl.GenTexture();
            gl.BindTexture(TextureTarget.TextureCubeMap, texture);
            for (int face = 0; face < 6; face++)
            {
                gl.TexImage2D(
                    (TextureTarget)((int)TextureTarget.TextureCubeMapPositiveX + face),
                    0,
                    srgb ? InternalFormat.Srgb8Alpha8 : InternalFormat.Rgba8,
                    (uint)cube.Width,
                    (uint)cube.Height,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    new ReadOnlySpan<byte>(cube.Faces[face]));
            }

            // LTK uploads the six DDS faces in file order, without mipmaps or a face flip.
            if (mipmaps)
                gl.GenerateMipmap(TextureTarget.TextureCubeMap);
            gl.TexParameter(
                TextureTarget.TextureCubeMap,
                TextureParameterName.TextureMinFilter,
                (int)(mipmaps ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear));
            gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
            gl.BindTexture(TextureTarget.TextureCubeMap, 0);
            return texture;
        }
    }
}
