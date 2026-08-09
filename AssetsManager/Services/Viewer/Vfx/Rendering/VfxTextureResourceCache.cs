using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    internal sealed class VfxTextureResourceCache : IDisposable
    {
        private readonly GL _gl;
        private readonly List<uint> _ownedTextures = new();

        internal VfxTextureResourceCache(GL gl)
        {
            _gl = gl;
            FallbackTransparentTexture = CreateFallbackTexture();
            _ownedTextures.Add(FallbackTransparentTexture);
        }

        internal uint FallbackTransparentTexture { get; }

        internal uint Upload(byte[] bgra, int width, int height)
        {
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba8,
                (uint)width,
                (uint)height,
                0,
                PixelFormat.Bgra,
                PixelType.UnsignedByte,
                new ReadOnlySpan<byte>(bgra));
            _gl.GenerateMipmap(TextureTarget.Texture2D);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _ownedTextures.Add(texture);
            return texture;
        }

        internal void Clear()
        {
            foreach (uint texture in _ownedTextures)
            {
                if (texture != FallbackTransparentTexture)
                    _gl.DeleteTexture(texture);
            }

            _ownedTextures.Clear();
            _ownedTextures.Add(FallbackTransparentTexture);
        }

        public void Dispose()
        {
            foreach (uint texture in _ownedTextures)
                _gl.DeleteTexture(texture);
            _ownedTextures.Clear();
        }

        private uint CreateFallbackTexture()
        {
            byte[] transparentPixel = { 0, 0, 0, 0 };
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba8,
                1,
                1,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                new ReadOnlySpan<byte>(transparentPixel));
            _gl.GenerateMipmap(TextureTarget.Texture2D);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            return texture;
        }
    }
}
