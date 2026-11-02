using AssetsManager.Services.Viewer.Resources;
using System;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Services.Viewer.Vfx.Resources;
using Silk.NET.OpenGL;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

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
        private uint _previewSky;
        private MapSunData _previewSun;

        internal GameShaderImageLight(GL gl) => _gl = gl;

        internal uint Resolve(CubeMapData cube, MapSunData sun)
        {
            if (cube?.IsValid == true) return Resolve(cube);
            if (_previewSky != 0 && Equals(sun, _previewSun)) return _previewSky;
            if (_previewSky != 0) _gl.DeleteTexture(_previewSky);
            _previewSky = _gl.GenTexture();
            _previewSun = sun;
            _gl.BindTexture(TextureTarget.Texture2DArray, _previewSky);
            _gl.TexImage3D(TextureTarget.Texture2DArray, 0, InternalFormat.Rgba16f,
                16, 16, 6, 0, PixelFormat.Rgba, PixelType.Float, new ReadOnlySpan<float>(SkyTexels(sun)));
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            return _previewSky;
        }

        /// <summary>Preview substitute for a map probe; retains authored sky, horizon and ground hues.</summary>
        internal static float[] SkyTexels(MapSunData sun, int size = 16)
        {
            Vector3 Rgb(Vector4 color) => new(color.X, color.Y, color.Z);
            Vector3 sky = sun == null ? Vector3.One : Rgb(sun.SkyColor);
            Vector3 ground = sun == null ? Vector3.One : Rgb(sun.GroundColor);
            Vector3 horizon = sun == null ? Vector3.One : Rgb(sun.HorizonColor);
            float ambientStrength = MathF.Max(sun?.SkyScale ?? 0f, 0f);
            float totalStrength = ambientStrength + MathF.Max(sun?.Intensity ?? 0f, 0f);
            float ambient = sun == null ? 0.6f : totalStrength > 0f ? ambientStrength / totalStrength : 0.5f;
            float[] texels = new float[size * size * 6 * 4];
            int at = 0;
            for (int face = 0; face < 6; face++)
            for (int row = 0; row < size; row++)
            for (int column = 0; column < size; column++)
            {
                float u = (column + 0.5f) / size * 2f - 1f, v = (row + 0.5f) / size * 2f - 1f;
                Vector3 direction = face switch
                {
                    0 => new(1, -v, -u), 1 => new(-1, -v, u), 2 => new(u, 1, v),
                    3 => new(u, -1, -v), 4 => new(u, -v, 1), _ => new(-u, -v, -1)
                };
                float height = Vector3.Normalize(direction).Y;
                Vector3 color = Vector3.Lerp(horizon, height > 0 ? sky : ground, MathF.Abs(height));
                texels[at++] = MathF.Pow(MathF.Max(0, color.X), 2.2f) * ambient * 0.25f;
                texels[at++] = MathF.Pow(MathF.Max(0, color.Y), 2.2f) * ambient * 0.25f;
                texels[at++] = MathF.Pow(MathF.Max(0, color.Z), 2.2f) * ambient * 0.25f;
                texels[at++] = 1;
            }
            return texels;
        }

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
            if (_previewSky != 0) _gl.DeleteTexture(_previewSky);
            _previewSky = 0;
            Release();
            ReleaseCube();
        }
    }
}
