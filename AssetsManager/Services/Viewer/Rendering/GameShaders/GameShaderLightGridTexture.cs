using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.GameShaders
{
    /// <summary>
    /// The one-texel <c>LIGHT_GRID_TEXTURE</c> PBR character shaders sample: layers 0-5 hold the ambient cube
    /// (+X, -X, +Y, -Y, +Z, -Z) the character stands in and layer 6 the sun visibility, fully lit here.
    /// </summary>
    internal sealed class GameShaderLightGridTexture : IDisposable
    {
        internal const int Layers = 7;

        private readonly GL _gl;
        private readonly float[] _texels = new float[Layers * 4];
        private uint _texture;

        internal GameShaderLightGridTexture(GL gl) => _gl = gl;

        /// <summary>Writes the ambient cube into the texture and returns it.</summary>
        internal uint Update(ReadOnlySpan<Vector3> cube)
        {
            Fill(cube, _texels);
            if (_texture == 0)
            {
                _texture = _gl.GenTexture();
                _gl.BindTexture(TextureTarget.Texture2DArray, _texture);
                _gl.TexImage3D(
                    TextureTarget.Texture2DArray,
                    0,
                    InternalFormat.Rgba16f,
                    1,
                    1,
                    Layers,
                    0,
                    PixelFormat.Rgba,
                    PixelType.Float,
                    new ReadOnlySpan<float>(_texels));
                _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            }
            else
            {
                _gl.BindTexture(TextureTarget.Texture2DArray, _texture);
                _gl.TexSubImage3D(
                    TextureTarget.Texture2DArray,
                    0,
                    0,
                    0,
                    0,
                    1,
                    1,
                    Layers,
                    PixelFormat.Rgba,
                    PixelType.Float,
                    new ReadOnlySpan<float>(_texels));
            }
            _gl.BindTexture(TextureTarget.Texture2DArray, 0);
            return _texture;
        }

        internal static void Fill(ReadOnlySpan<Vector3> cube, Span<float> texels)
        {
            for (int face = 0; face < 6; face++)
            {
                Vector3 color = face < cube.Length ? cube[face] : Vector3.Zero;
                texels[face * 4] = color.X;
                texels[face * 4 + 1] = color.Y;
                texels[face * 4 + 2] = color.Z;
                texels[face * 4 + 3] = 1f;
            }
            texels[24] = texels[25] = texels[26] = texels[27] = 1f;
        }

        public void Dispose()
        {
            if (_texture != 0)
                _gl.DeleteTexture(_texture);
            _texture = 0;
        }
    }
}
