using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Tests.Support
{
    /// <summary>
    /// Draws one submesh of a loaded champion skin in bind pose with a game material program into a float
    /// framebuffer, so checks can see discarded, non-finite, dark or blown-out output. The camera frames the
    /// submesh; blending is off, so only a discard leaves the clear colour.
    /// </summary>
    internal sealed class SkinSubmeshRenderer : IDisposable
    {
        internal sealed record Result(
            bool Bound,
            int Covered,
            int NonFinite,
            Vector4 Mean,
            float MaxComponent,
            IReadOnlyList<string> MissingTextures,
            float[] Pixels)
        {
            /// <summary>Rec. 709 luminance of the mean colour of the covered pixels.</summary>
            internal float Luminance => Mean.X * 0.2126f + Mean.Y * 0.7152f + Mean.Z * 0.0722f;
        }

        // Shaders never write this, so it marks the pixels no fragment reached (a black output still counts).
        private const float Uncovered = -12345f;
        private static readonly Matrix4x4[] BindPose = Enumerable.Repeat(Matrix4x4.Identity, 256).ToArray();

        private readonly GL _gl;
        private readonly AppSettings _settings;
        private readonly VfxCubeMapData _sky;
        private readonly uint _framebuffer;
        private readonly uint _colour;
        private readonly uint _depth;
        private readonly Dictionary<string, uint> _textures = new(StringComparer.OrdinalIgnoreCase);
        private GameShaderRuntime _runtime;
        private GameShaderBloom _bloom;
        // Each render lands a second after the last, so dynamic material fades settle on the state it draws.
        private float _clock;
        private MapCharacterAssetData _asset;
        private Dictionary<string, BitmapSource> _bitmaps;

        internal uint Size { get; }

        internal SkinSubmeshRenderer(GL gl, AppSettings settings, VfxCubeMapData sky, uint size = 128)
        {
            _gl = gl;
            _settings = settings;
            _sky = sky;
            Size = size;

            _colour = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, _colour);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, size, size, 0, PixelFormat.Rgba, PixelType.Float, ReadOnlySpan<float>.Empty);
            _depth = gl.GenRenderbuffer();
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, size, size);
            _framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _colour, 0);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _depth);
            _runtime = new GameShaderRuntime(gl, false, settings);
        }

        /// <summary>
        /// Drops the compiled programs and uploaded textures; a sweep calls it between champions so the
        /// per-material program cache does not grow with every skin.
        /// </summary>
        internal void Reset()
        {
            ReleaseTextures();
            _runtime.Dispose();
            _runtime = new GameShaderRuntime(_gl, false, _settings);
        }

        /// <param name="glowOnly">
        /// Draws the submesh, clears its colour and adds only the glow <see cref="GameShaderBloom"/> composes from
        /// the pass's second target, so the pixels are exactly what bloom adds to the frame.
        /// </param>
        internal Result Render(
            MapCharacterAssetData asset,
            MapCharacterMeshRange range,
            ModelMaterialDefinition material,
            bool keepPixels = false,
            bool glowOnly = false,
            GameMaterialState state = null)
        {
            if (!ReferenceEquals(asset, _asset))
            {
                ReleaseTextures();
                _asset = asset;
                _bitmaps = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
                foreach ((string path, BitmapSource bitmap) in asset.Textures ?? new Dictionary<string, BitmapSource>())
                    _bitmaps.TryAdd(path, bitmap);
            }

            uint[] corners = Enumerable.Range(range.StartIndex, range.IndexCount).Select(at => asset.Mesh.Indices[at]).ToArray();
            Vector3[] positions = corners.Select(v => asset.Mesh.Positions[v]).ToArray();
            Vector3 min = positions.Aggregate(Vector3.Min);
            Vector3 max = positions.Aggregate(Vector3.Max);
            Vector3 center = (min + max) * 0.5f;
            float radius = Math.Max((max - min).Length() * 0.5f, 1f);
            Vector3 eye = center + new Vector3(radius * 2.2f, radius * 0.4f, radius * 1.2f);
            var frame = new GameShaderRuntime.Frame(
                Matrix4x4.CreateLookAt(eye, center, Vector3.UnitY),
                Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, 1f, radius * 0.1f, radius * 10f),
                eye,
                _clock += 1f,
                null,
                ImageLight: _sky);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            _gl.Viewport(0, 0, Size, Size);
            _gl.ClearColor(Uncovered, Uncovered, Uncovered, Uncovered);
            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            _gl.Enable(EnableCap.DepthTest);

            uint vao = _gl.GenVertexArray();
            var buffers = new List<uint>();
            _gl.BindVertexArray(vao);
            void Buffer(uint location, float[] data, int components)
            {
                uint vbo = _gl.GenBuffer();
                buffers.Add(vbo);
                _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
                _gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(data), BufferUsageARB.StaticDraw);
                _gl.EnableVertexAttribArray(location);
                _gl.VertexAttribPointer(location, components, VertexAttribPointerType.Float, false, 0, IntPtr.Zero);
            }
            // The submesh's triangles expanded to plain vertices, drawn with DrawArrays.
            Buffer(0, positions.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray(), 3);
            Buffer(1, corners.SelectMany(v => new[] { asset.Mesh.Normals[v].X, asset.Mesh.Normals[v].Y, asset.Mesh.Normals[v].Z }).ToArray(), 3);
            Buffer(2, corners.SelectMany(v => new[] { asset.Mesh.Uv[v].X, asset.Mesh.Uv[v].Y }).ToArray(), 2);
            Buffer(6, corners.SelectMany(v => asset.Mesh.SkinWeights.Skip((int)v * 4).Take(4)).ToArray(), 4);
            if (asset.Mesh.HasColors)
                Buffer(4, corners.SelectMany(v => new[] { asset.Mesh.Colors[v].X, asset.Mesh.Colors[v].Y, asset.Mesh.Colors[v].Z, asset.Mesh.Colors[v].W }).ToArray(), 4);
            uint joints = _gl.GenBuffer();
            buffers.Add(joints);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, joints);
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                new ReadOnlySpan<byte>(corners.SelectMany(v => asset.Mesh.SkinIndices.Skip((int)v * 4).Take(4)).ToArray()),
                BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(5);
            _gl.VertexAttribIPointer(5, 4, VertexAttribIType.UnsignedByte, 0, IntPtr.Zero);

            var missing = new List<string>();
            bool bound = _runtime.TryBindSkinned(material, 0, Matrix4x4.Identity, BindPose, false, in frame, path => Texture(path, missing), state: state,
                hasColors: asset.Mesh.HasColors);
            if (bound)
            {
                _gl.BindVertexArray(vao);
                _gl.Disable(EnableCap.Blend);
                _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)corners.Length);
                if (glowOnly)
                {
                    _gl.ClearColor(0f, 0f, 0f, 0f);
                    _gl.Clear(ClearBufferMask.ColorBufferBit);
                    if (_bloom == null)
                    {
                        _bloom = new GameShaderBloom();
                        _bloom.Initialize(_gl);
                    }
                    _bloom.BeginPasses();
                    _runtime.TryBindSkinned(material, 0, Matrix4x4.Identity, BindPose, false, in frame, path => Texture(path, missing), state: state);
                    _gl.BindVertexArray(vao);
                    _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)corners.Length);
                    _bloom.EndPasses();
                    _bloom.Compose();
                }
            }

            float[] pixels = new float[Size * Size * 4];
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            _gl.ReadPixels(0, 0, Size, Size, PixelFormat.Rgba, PixelType.Float, new Span<float>(pixels));
            _gl.BindVertexArray(0);
            _gl.DeleteVertexArray(vao);
            foreach (uint buffer in buffers)
                _gl.DeleteBuffer(buffer);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            int covered = 0, nonFinite = 0;
            Vector4 sum = Vector4.Zero;
            float peak = 0f;
            for (int at = 0; at < pixels.Length; at += 4)
            {
                var value = new Vector4(pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]);
                if (value == new Vector4(Uncovered))
                    continue;
                covered++;
                if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || !float.IsFinite(value.W))
                {
                    nonFinite++;
                    continue;
                }
                sum += value;
                peak = Math.Max(peak, Math.Max(value.X, Math.Max(value.Y, value.Z)));
            }
            int finite = covered - nonFinite;
            return new Result(
                bound,
                covered,
                nonFinite,
                finite > 0 ? sum / finite : Vector4.Zero,
                peak,
                missing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                keepPixels ? pixels : null);
        }

        /// <summary>Writes float pixels (bottom-up) as an 8-bit PNG, clamped to 0..1.</summary>
        internal void SavePng(float[] pixels, string path)
        {
            int size = (int)Size;
            byte[] bgra = new byte[pixels.Length];
            static byte Channel(float value) => value == Uncovered ? (byte)0 : (byte)Math.Clamp(float.IsFinite(value) ? value * 255f + 0.5f : 255f, 0f, 255f);
            for (int row = 0; row < size; row++)
                for (int column = 0; column < size; column++)
                {
                    int from = ((size - 1 - row) * size + column) * 4;
                    int to = (row * size + column) * 4;
                    bgra[to] = Channel(pixels[from + 2]);
                    bgra[to + 1] = Channel(pixels[from + 1]);
                    bgra[to + 2] = Channel(pixels[from]);
                    bgra[to + 3] = 255;
                }
            var image = BitmapSource.Create(size, size, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, bgra, size * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using FileStream file = File.Create(path);
            encoder.Save(file);
        }

        private uint? Texture(string path, List<string> missing)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            if (_textures.TryGetValue(path, out uint cached))
                return cached;
            if (_bitmaps == null || !_bitmaps.TryGetValue(path, out BitmapSource bitmap) || bitmap == null)
            {
                missing.Add(path);
                return null;
            }
            (int width, int height, byte[] data) = GlBitmapPixels.ToBgra32(bitmap);
            uint id = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, id);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, new ReadOnlySpan<byte>(data));
            _gl.GenerateMipmap(TextureTarget.Texture2D);
            _textures[path] = id;
            return id;
        }

        private void ReleaseTextures()
        {
            foreach (uint texture in _textures.Values)
                _gl.DeleteTexture(texture);
            _textures.Clear();
            _asset = null;
            _bitmaps = null;
        }

        public void Dispose()
        {
            ReleaseTextures();
            _runtime?.Dispose();
            _bloom?.Dispose();
            _gl.DeleteFramebuffer(_framebuffer);
            _gl.DeleteTexture(_colour);
            _gl.DeleteRenderbuffer(_depth);
        }
    }
}
