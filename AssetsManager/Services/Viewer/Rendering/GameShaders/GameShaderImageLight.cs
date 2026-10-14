using AssetsManager.Services.Viewer.Resources;
using System;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Services.Viewer.Vfx.Resources;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.GameShaders
{
    /// <summary>
    /// The preview sky as the environment game shaders light and reflect from: <c>IBL_CUBEMAP</c> for PBR
    /// (the translator emulates DXBC cube arrays with six 2D layers per cube, so the sky is cube 0, sRGB-decoded
    /// to linear radiance) and <c>ENV_CUBE</c> for gamma-space reflective materials (a raw cube map). Both are
    /// mipmapped for the glossiness levels the shaders read.
    /// </summary>
    internal sealed class GameShaderImageLight : IDisposable
    {
        private readonly GL _gl;
        private CubeMapData _source;
        private uint _texture;
        private CubeMapData _cubeSource;
        private uint _cube;

        internal GameShaderImageLight(GL gl) => _gl = gl;

        /// <returns>The array texture of <paramref name="cube"/>, uploaded once per cube, or 0 without one.</returns>
        internal uint Resolve(CubeMapData cube)
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

        /// <returns>The sky as a raw mipmapped cube map for ENV_CUBE, uploaded once per cube, or 0 without one.</returns>
        internal uint ResolveCube(CubeMapData cube)
        {
            if (cube?.IsValid != true)
                return 0;
            if (ReferenceEquals(cube, _cubeSource) && _cube != 0)
                return _cube;

            ReleaseCube();
            _cube = GlCubeMapUploader.Upload(_gl, cube, srgb: false, mipmaps: true);
            _cubeSource = cube;
            return _cube;
        }

        private void Release()
        {
            if (_texture != 0)
                _gl.DeleteTexture(_texture);
            _texture = 0;
            _source = null;
        }

        private void ReleaseCube()
        {
            if (_cube != 0)
                _gl.DeleteTexture(_cube);
            _cube = 0;
            _cubeSource = null;
        }

        public void Dispose()
        {
            Release();
            ReleaseCube();
        }
    }
}
