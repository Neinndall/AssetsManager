using System;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.Core
{
    /// <summary>
    /// Uploads a decoded 2D texture as a one-layer texture array, for engine textures the game
    /// shaders sample as <c>Texture2DArray</c> but that ship as a single slice (map terrain paint).
    /// </summary>
    internal static class GlTextureArrayUploader
    {
        /// <returns>The raw (non-sRGB) array texture, clamped, with its authored or generated mip chain.</returns>
        internal static uint UploadSingleLayer(GL gl, MapTextureImage image)
        {
            ArgumentNullException.ThrowIfNull(gl);
            if (image?.MipLevels == null || image.MipLevels.Count == 0)
                return 0;

            uint texture = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2DArray, texture);
            for (int level = 0; level < image.MipLevels.Count; level++)
            {
                (int width, int height, byte[] pixels) = GlBitmapPixels.ToBgra32(image.MipLevels[level]);
                gl.TexImage3D(
                    TextureTarget.Texture2DArray,
                    level,
                    InternalFormat.Rgba8,
                    (uint)width,
                    (uint)height,
                    1,
                    0,
                    PixelFormat.Bgra,
                    PixelType.UnsignedByte,
                    new ReadOnlySpan<byte>(pixels));
            }

            if (!image.HasAuthoredMipChain)
                gl.GenerateMipmap(TextureTarget.Texture2DArray);
            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            gl.BindTexture(TextureTarget.Texture2DArray, 0);
            return texture;
        }
    }
}
