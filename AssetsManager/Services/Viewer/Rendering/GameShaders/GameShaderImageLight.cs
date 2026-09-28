using System;
using AssetsManager.Services.Viewer.Vfx.Resources;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.GameShaders
{
    /// <summary>
    /// The environment cube PBR shaders light from (<c>IBL_CUBEMAP</c>). The translator emulates DXBC cube
    /// arrays with a 2D array of six layers per cube, so the preview sky is uploaded as cube 0 in that layout,
    /// sRGB-decoded to linear radiance and mipmapped for the roughness and diffuse levels the shaders read.
    /// </summary>
    internal sealed class GameShaderImageLight : IDisposable
    {
        private readonly GL _gl;
        private VfxCubeMapData _source;
        private uint _texture;

        internal GameShaderImageLight(GL gl) => _gl = gl;

        /// <returns>The array texture of <paramref name="cube"/>, uploaded once per cube, or 0 without one.</returns>
        internal uint Resolve(VfxCubeMapData cube)
        {
            if (cube?.IsValid != true)
                return 0;
            if (ReferenceEquals(cube, _source) && _texture != 0)
                return _texture;

            Release();
            _texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2DArray, _texture);
            _gl.TexImage3D(
                TextureTarget.Texture2DArray,
                0,
                InternalFormat.Srgb8Alpha8,
                (uint)cube.Width,
                (uint)cube.Height,
                6,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                ReadOnlySpan<byte>.Empty);
            for (int face = 0; face < 6; face++)
            {
                // DDS faces are +X, -X, +Y, -Y, +Z, -Z top row first, the layer order and orientation
                // dxbcCubeArrayLod addresses.
                _gl.TexSubImage3D(
                    TextureTarget.Texture2DArray,
                    0,
                    0,
                    0,
                    face,
                    (uint)cube.Width,
                    (uint)cube.Height,
                    1,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    new ReadOnlySpan<byte>(cube.Faces[face]));
            }
            _gl.GenerateMipmap(TextureTarget.Texture2DArray);
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.BindTexture(TextureTarget.Texture2DArray, 0);
            _source = cube;
            return _texture;
        }

        private void Release()
        {
            if (_texture != 0)
                _gl.DeleteTexture(_texture);
            _texture = 0;
            _source = null;
        }

        public void Dispose() => Release();
    }
}
