using System;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    internal sealed class VfxSceneCapture : IDisposable
    {
        private readonly GL _gl;
        private int _colorWidth;
        private int _colorHeight;
        private int _depthWidth;
        private int _depthHeight;

        internal VfxSceneCapture(GL gl)
        {
            _gl = gl;
        }

        internal uint ColorTexture { get; private set; }
        internal uint DepthTexture { get; private set; }
        internal int Width { get; private set; }
        internal int Height { get; private set; }

        internal void Capture(uint width, uint height, bool captureColor, bool captureDepth)
        {
            if (width == 0 || height == 0 || (!captureColor && !captureDepth))
                return;

            _gl.GetInteger(GLEnum.ActiveTexture, out int activeTexture);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.GetInteger(GLEnum.TextureBinding2D, out int textureBinding);

            if (captureColor)
            {
                bool created = ColorTexture == 0;
                if (created)
                {
                    ColorTexture = _gl.GenTexture();
                    _gl.BindTexture(TextureTarget.Texture2D, ColorTexture);
                    SetTextureParameters(TextureMinFilter.Linear, TextureMagFilter.Linear);
                }
                else
                {
                    _gl.BindTexture(TextureTarget.Texture2D, ColorTexture);
                }

                if (created || _colorWidth != (int)width || _colorHeight != (int)height)
                {
                    _gl.TexImage2D(
                        TextureTarget.Texture2D,
                        0,
                        InternalFormat.Rgba8,
                        width,
                        height,
                        0,
                        PixelFormat.Rgba,
                        PixelType.UnsignedByte,
                        ReadOnlySpan<byte>.Empty);
                }

                _gl.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, width, height);
                _colorWidth = (int)width;
                _colorHeight = (int)height;
            }

            if (captureDepth)
            {
                bool created = DepthTexture == 0;
                if (created)
                {
                    DepthTexture = _gl.GenTexture();
                    _gl.BindTexture(TextureTarget.Texture2D, DepthTexture);
                    SetTextureParameters(TextureMinFilter.Nearest, TextureMagFilter.Nearest);
                }
                else
                {
                    _gl.BindTexture(TextureTarget.Texture2D, DepthTexture);
                }

                if (created || _depthWidth != (int)width || _depthHeight != (int)height)
                {
                    _gl.TexImage2D(
                        TextureTarget.Texture2D,
                        0,
                        InternalFormat.DepthComponent24,
                        width,
                        height,
                        0,
                        PixelFormat.DepthComponent,
                        PixelType.Float,
                        ReadOnlySpan<byte>.Empty);
                }

                _gl.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 0, 0, width, height);
                _depthWidth = (int)width;
                _depthHeight = (int)height;
            }

            _gl.BindTexture(TextureTarget.Texture2D, (uint)textureBinding);
            _gl.ActiveTexture((TextureUnit)activeTexture);
            Width = (int)width;
            Height = (int)height;
        }

        public void Dispose()
        {
            if (ColorTexture != 0)
                _gl.DeleteTexture(ColorTexture);
            if (DepthTexture != 0)
                _gl.DeleteTexture(DepthTexture);

            ColorTexture = 0;
            DepthTexture = 0;
            Width = 0;
            Height = 0;
        }

        private void SetTextureParameters(TextureMinFilter minFilter, TextureMagFilter magFilter)
        {
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)minFilter);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)magFilter);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        }
    }
}
