using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.Map
{
    /// <summary>
    /// Dedicated MAPGEO backdrop renderer aligned with LTK Manager 1.20.0.
    /// One set of vertex/index buffers owns the whole map; submeshes are draw groups rather
    /// than SceneModel/ModelPart instances.
    /// </summary>
    internal sealed class MapGeometryRenderer : IDisposable
    {
        private const int DefaultLayer = MapGeometryData.DefaultLayer;
        private const string IndicatorPattern = "indicator";

        private static readonly Vector3 StoneSrgb = new(154f / 255f, 149f / 255f, 140f / 255f);
        private static readonly Vector3 StoneLinear = SrgbToLinear(StoneSrgb);
        private static readonly Vector3 SunDirection = Vector3.Normalize(new Vector3(0.25f, 0.75f, -0.05f));
        private static readonly Regex IndicatorShader = new(IndicatorPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly MapMaterialRenderState UnboundState = new(
            MapMaterialBlendMode.Opaque,
            MapBlendFactor.One,
            MapBlendFactor.Zero,
            false,
            false,
            true,
            false,
            true,
            true);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void DrawElementsDelegate(uint mode, int count, uint type, IntPtr indices);

        internal sealed record BoundMaterial(
            int MaterialIndex,
            string MaterialPath,
            MapMaterialDefinition Material,
            MapMaterialRenderState RenderState,
            bool Lit,
            bool Transparent,
            Vector2 UvRepeat,
            MapTextureWrap WrapU,
            MapTextureWrap WrapV);

        internal readonly record struct DrawGroup(
            int StartIndex,
            int IndexCount,
            int BoundMaterialIndex,
            int Order);

        internal sealed record DrawPlan(
            IReadOnlyList<BoundMaterial> Materials,
            IReadOnlyList<DrawGroup> OpaqueGroups,
            IReadOnlyList<DrawGroup> TransparentGroups);

        private sealed class SharedTexture
        {
            internal uint Id;
            internal int References;
        }

        private sealed record MaterialTexture(BitmapSource Bitmap, uint TextureId);

        private readonly Dictionary<BitmapSource, SharedTexture> _sharedTextures =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, MaterialTexture> _materialTextures =
            new(StringComparer.Ordinal);
        private readonly Dictionary<(MapTextureWrap U, MapTextureWrap V), uint> _samplers = new();

        private GL _gl;
        private DrawElementsDelegate _drawElements;
        private MapSceneData _scene;
        private DrawPlan _plan;
        private uint _program;
        private uint _vao;
        private uint _positionVbo;
        private uint _normalVbo;
        private uint _uv0Vbo;
        private uint _uv1Vbo;
        private uint _ebo;
        private uint _whiteTexture;
        private int _uViewProjection;
        private int _uBaseTexture;
        private int _uColor;
        private int _uOpacity;
        private int _uAlphaTest;
        private int _uUvRepeat;
        private int _uLit;
        private int _uPremultipliedAlpha;
        private int _uLightDirection;
        private bool _ready;

        internal bool HasScene => _scene != null && _plan != null && _vao != 0;

        internal void Initialize(GL gl)
        {
            ArgumentNullException.ThrowIfNull(gl);
            if (_ready)
                return;

            _gl = gl;
            IntPtr drawElements = gl.Context.GetProcAddress("glDrawElements");
            if (drawElements == IntPtr.Zero)
                throw new InvalidOperationException("OpenGL glDrawElements is unavailable for MAPGEO rendering.");
            _drawElements = Marshal.GetDelegateForFunctionPointer<DrawElementsDelegate>(drawElements);

            bool embedded = GlShaderCompiler.UsesEmbeddedProfile(gl);
            _program = GlShaderCompiler.CreateProgram(
                gl,
                embedded,
                MapGeometryShaderSource.Vertex,
                MapGeometryShaderSource.Fragment);
            _uViewProjection = gl.GetUniformLocation(_program, "uViewProjection");
            _uBaseTexture = gl.GetUniformLocation(_program, "uBaseTexture");
            _uColor = gl.GetUniformLocation(_program, "uColor");
            _uOpacity = gl.GetUniformLocation(_program, "uOpacity");
            _uAlphaTest = gl.GetUniformLocation(_program, "uAlphaTest");
            _uUvRepeat = gl.GetUniformLocation(_program, "uUvRepeat");
            _uLit = gl.GetUniformLocation(_program, "uLit");
            _uPremultipliedAlpha = gl.GetUniformLocation(_program, "uPremultipliedAlpha");
            _uLightDirection = gl.GetUniformLocation(_program, "uLightDirection");

            gl.UseProgram(_program);
            gl.Uniform1(_uBaseTexture, 0);
            gl.UseProgram(0);
            _whiteTexture = CreateWhiteTexture();
            _ready = true;
        }

        internal void LoadScene(MapSceneData scene)
        {
            if (!_ready)
                throw new InvalidOperationException("MapGeometryRenderer must be initialized before loading a scene.");
            ArgumentNullException.ThrowIfNull(scene);

            ReleaseSceneResources();
            _scene = scene;
            _plan = BuildDrawPlan(scene);
            UploadGeometry(scene.Geometry);
            UpdateTextures(scene.Textures);
        }

        /// <summary>
        /// Rebinds only texture images. Geometry, material classification and draw groups stay intact,
        /// allowing the 64px preview wave to sharpen to the 1024px wave without rebuilding the map.
        /// </summary>
        internal void UpdateTextures(IReadOnlyDictionary<string, BitmapSource> textures)
        {
            if (!_ready || _scene == null || textures == null)
                return;

            foreach ((string materialPath, BitmapSource bitmap) in textures)
            {
                if (string.IsNullOrWhiteSpace(materialPath) || bitmap == null)
                    continue;
                if (!_scene.Geometry.Materials.Contains(materialPath, StringComparer.Ordinal))
                    continue;

                if (_materialTextures.TryGetValue(materialPath, out MaterialTexture current) &&
                    ReferenceEquals(current.Bitmap, bitmap))
                {
                    continue;
                }

                uint textureId = AcquireTexture(bitmap);
                if (_materialTextures.TryGetValue(materialPath, out current))
                    ReleaseTexture(current.Bitmap);
                _materialTextures[materialPath] = new MaterialTexture(bitmap, textureId);
            }
        }

        internal void Render(Matrix4x4 viewProjection)
        {
            if (!_ready || !HasScene)
                return;

            _gl.UseProgram(_program);
            _gl.UniformMatrix4(_uViewProjection, 1, false, in viewProjection.M11);
            _gl.Uniform3(_uLightDirection, SunDirection.X, SunDirection.Y, SunDirection.Z);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindVertexArray(_vao);
            _gl.DepthFunc(DepthFunction.Lequal);

            // LTK mirrors the entire backdrop across X and lets Three.js flip the front face
            // from the negative world determinant. The shader mirrors X here, so GL's front
            // winding is flipped for the same result without rewriting two million vertices.
            _gl.FrontFace(FrontFaceDirection.CW);
            try
            {
                DrawGroups(_plan.OpaqueGroups);
                DrawGroups(_plan.TransparentGroups);
            }
            finally
            {
                _gl.FrontFace(FrontFaceDirection.Ccw);
                _gl.Disable(EnableCap.Blend);
                _gl.Disable(EnableCap.CullFace);
                _gl.Enable(EnableCap.DepthTest);
                _gl.DepthMask(true);
                _gl.BindSampler(0, 0);
                _gl.BindTexture(TextureTarget.Texture2D, 0);
                _gl.BindVertexArray(0);
                _gl.UseProgram(0);
            }
        }

        private void DrawGroups(IReadOnlyList<DrawGroup> groups)
        {
            int activeMaterial = -1;
            foreach (DrawGroup group in groups)
            {
                if (group.IndexCount <= 0 || group.BoundMaterialIndex < 0 ||
                    group.BoundMaterialIndex >= _plan.Materials.Count)
                {
                    continue;
                }

                if (activeMaterial != group.BoundMaterialIndex)
                {
                    activeMaterial = group.BoundMaterialIndex;
                    ApplyMaterial(_plan.Materials[activeMaterial]);
                }

                _drawElements(
                    (uint)PrimitiveType.Triangles,
                    group.IndexCount,
                    (uint)DrawElementsType.UnsignedInt,
                    new IntPtr(checked(group.StartIndex * sizeof(uint))));
            }
        }

        private void ApplyMaterial(BoundMaterial bound)
        {
            MapMaterialDefinition material = bound.Material;
            MaterialTexture texture = null;
            bool hasTexture = !string.IsNullOrWhiteSpace(bound.MaterialPath) &&
                              _materialTextures.TryGetValue(bound.MaterialPath, out texture);
            uint textureId = hasTexture ? texture.TextureId : _whiteTexture;
            Vector3 color = ResolveColor(material, hasTexture);

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, textureId);
            _gl.BindSampler(0, ResolveSampler(bound.WrapU, bound.WrapV));
            _gl.Uniform3(_uColor, color.X, color.Y, color.Z);
            _gl.Uniform1(_uOpacity, material?.Missing == false ? material.Opacity ?? 1f : 1f);
            _gl.Uniform1(_uAlphaTest, material?.Missing == false ? material.AlphaTest ?? 0f : 0f);
            _gl.Uniform2(_uUvRepeat, bound.UvRepeat.X, bound.UvRepeat.Y);
            _gl.Uniform1(_uLit, bound.Lit ? 1 : 0);
            _gl.Uniform1(_uPremultipliedAlpha, bound.RenderState.PremultipliedAlpha ? 1 : 0);
            ApplyRenderState(bound.RenderState, bound.Transparent);
        }

        private void ApplyRenderState(MapMaterialRenderState state, bool transparent)
        {
            if (transparent)
            {
                _gl.Enable(EnableCap.Blend);
                _gl.BlendEquation(GLEnum.FuncAdd);
                switch (state.Blending)
                {
                    case MapMaterialBlendMode.Additive:
                        // Three.js AdditiveBlending uses the same factors for RGB and alpha.
                        _gl.BlendFunc(
                            state.PremultipliedAlpha ? BlendingFactor.One : BlendingFactor.SrcAlpha,
                            BlendingFactor.One);
                        break;
                    case MapMaterialBlendMode.Modulate:
                        _gl.BlendFunc(BlendingFactor.OneMinusSrcColor, BlendingFactor.Zero);
                        break;
                    default:
                        _gl.BlendFuncSeparate(
                            state.PremultipliedAlpha ? BlendingFactor.One : BlendingFactor.SrcAlpha,
                            BlendingFactor.OneMinusSrcAlpha,
                            BlendingFactor.One,
                            BlendingFactor.OneMinusSrcAlpha);
                        break;
                }
            }
            else
            {
                _gl.Disable(EnableCap.Blend);
            }

            if (state.DepthTest)
                _gl.Enable(EnableCap.DepthTest);
            else
                _gl.Disable(EnableCap.DepthTest);
            _gl.DepthMask(state.DepthWrite);

            if (state.DoubleSided)
            {
                _gl.Disable(EnableCap.CullFace);
            }
            else
            {
                _gl.Enable(EnableCap.CullFace);
                _gl.CullFace(state.Inverted ? TriangleFace.Front : TriangleFace.Back);
            }
        }

        private void UploadGeometry(MapGeometryData geometry)
        {
            _vao = _gl.GenVertexArray();
            _gl.BindVertexArray(_vao);

            _positionVbo = UploadAttribute(0, geometry.Positions, 3);
            _normalVbo = UploadAttribute(1, geometry.Normals, 3);
            _uv0Vbo = UploadAttribute(2, geometry.Uv0, 2);
            if (geometry.Uv1 != null)
                _uv1Vbo = UploadAttribute(3, geometry.Uv1, 2);

            _ebo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
            _gl.BufferData(
                BufferTargetARB.ElementArrayBuffer,
                new ReadOnlySpan<uint>(geometry.Indices),
                BufferUsageARB.StaticDraw);

            _gl.BindVertexArray(0);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        }

        private uint UploadAttribute(uint location, Vector3[] values, int components)
        {
            uint buffer = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer);
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                new ReadOnlySpan<Vector3>(values),
                BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(location);
            _gl.VertexAttribPointer(
                location,
                components,
                VertexAttribPointerType.Float,
                false,
                (uint)Marshal.SizeOf<Vector3>(),
                IntPtr.Zero);
            return buffer;
        }

        private uint UploadAttribute(uint location, Vector2[] values, int components)
        {
            uint buffer = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer);
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                new ReadOnlySpan<Vector2>(values),
                BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(location);
            _gl.VertexAttribPointer(
                location,
                components,
                VertexAttribPointerType.Float,
                false,
                (uint)Marshal.SizeOf<Vector2>(),
                IntPtr.Zero);
            return buffer;
        }

        private uint AcquireTexture(BitmapSource bitmap)
        {
            if (_sharedTextures.TryGetValue(bitmap, out SharedTexture shared))
            {
                shared.References++;
                return shared.Id;
            }

            uint id = UploadTexture(bitmap);
            _sharedTextures[bitmap] = new SharedTexture { Id = id, References = 1 };
            return id;
        }

        private void ReleaseTexture(BitmapSource bitmap)
        {
            if (bitmap == null || !_sharedTextures.TryGetValue(bitmap, out SharedTexture shared))
                return;

            shared.References--;
            if (shared.References > 0)
                return;

            if (shared.Id != 0)
                _gl.DeleteTexture(shared.Id);
            _sharedTextures.Remove(bitmap);
        }

        private uint UploadTexture(BitmapSource source)
        {
            BitmapSource bitmap = source;
            if (bitmap.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap();
                converted.BeginInit();
                converted.Source = bitmap;
                converted.DestinationFormat = PixelFormats.Bgra32;
                converted.EndInit();
                converted.Freeze();
                bitmap = converted;
            }

            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            int stride = checked(width * 4);
            byte[] pixels = new byte[checked(height * stride)];
            bitmap.CopyPixels(pixels, stride, 0);

            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Srgb8Alpha8,
                (uint)width,
                (uint)height,
                0,
                Silk.NET.OpenGL.PixelFormat.Bgra,
                PixelType.UnsignedByte,
                new ReadOnlySpan<byte>(pixels));
            _gl.GenerateMipmap(TextureTarget.Texture2D);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            return texture;
        }

        private uint CreateWhiteTexture()
        {
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            byte[] white = { 255, 255, 255, 255 };
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Srgb8Alpha8,
                1,
                1,
                0,
                Silk.NET.OpenGL.PixelFormat.Rgba,
                PixelType.UnsignedByte,
                new ReadOnlySpan<byte>(white));
            _gl.GenerateMipmap(TextureTarget.Texture2D);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            return texture;
        }

        private uint ResolveSampler(MapTextureWrap wrapU, MapTextureWrap wrapV)
        {
            var key = (wrapU, wrapV);
            if (_samplers.TryGetValue(key, out uint sampler))
                return sampler;

            sampler = _gl.GenSampler();
            _gl.SamplerParameter(sampler, SamplerParameterI.MinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.SamplerParameter(sampler, SamplerParameterI.MagFilter, (int)TextureMagFilter.Linear);
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapS, (int)ToTextureWrapMode(wrapU));
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapT, (int)ToTextureWrapMode(wrapV));
            _samplers[key] = sampler;
            return sampler;
        }

        internal static DrawPlan BuildDrawPlan(MapSceneData scene)
        {
            ArgumentNullException.ThrowIfNull(scene);
            MapGeometryData geometry = scene.Geometry;
            IReadOnlyList<MapMaterialDefinition> materials = scene.Materials;
            var bound = new List<BoundMaterial>();
            var byKey = new Dictionary<(int Material, bool MeshDoubleSided), int>();
            var opaque = new List<DrawGroup>();
            var transparent = new List<DrawGroup>();
            int order = 0;

            for (int meshIndex = 0; meshIndex < geometry.Meshes.Count; meshIndex++)
            {
                MapGeometryMeshData mesh = geometry.Meshes[meshIndex];
                if (!mesh.IsVisibleOnLayer(DefaultLayer) || mesh.SubmeshCount <= 0)
                    continue;

                bool meshDoubleSided = (mesh.Flags & MapGeometryMeshFlags.CullDisabled) != 0;
                int end = Math.Min(mesh.FirstSubmesh + mesh.SubmeshCount, geometry.Submeshes.Count);
                for (int at = Math.Max(mesh.FirstSubmesh, 0); at < end; at++)
                {
                    MapGeometrySubmeshData submesh = geometry.Submeshes[at];
                    int materialIndex = submesh.MaterialIndex;
                    MapMaterialDefinition material = materialIndex >= 0 && materialIndex < materials.Count
                        ? materials[materialIndex]
                        : null;
                    if (!Covers(material))
                        continue;

                    var key = (materialIndex, meshDoubleSided);
                    if (!byKey.TryGetValue(key, out int boundIndex))
                    {
                        MapMaterialRenderState authored = material?.Missing == false
                            ? material.RenderState
                            : UnboundState;
                        MapMaterialRenderState effective = authored with
                        {
                            DoubleSided = authored.DoubleSided || meshDoubleSided
                        };
                        bool transparentMaterial = effective.Blending != MapMaterialBlendMode.Opaque &&
                                                   !effective.Cutout;
                        bool lit = material == null ||
                                   (!material.Missing && effective.Blending != MapMaterialBlendMode.Additive);
                        Vector2 repeat = material?.Missing == false && material.UvRepeat.HasValue
                            ? material.UvRepeat.Value
                            : Vector2.One;
                        bool forceRepeat = material?.Missing == false && material.UvRepeat.HasValue;
                        MapTextureWrap wrapU = forceRepeat
                            ? MapTextureWrap.Repeat
                            : material?.BaseTexture?.WrapU ?? MapTextureWrap.Repeat;
                        MapTextureWrap wrapV = forceRepeat
                            ? MapTextureWrap.Repeat
                            : material?.BaseTexture?.WrapV ?? MapTextureWrap.Repeat;
                        string materialPath = materialIndex >= 0 && materialIndex < geometry.Materials.Count
                            ? geometry.Materials[materialIndex]
                            : null;

                        boundIndex = bound.Count;
                        byKey[key] = boundIndex;
                        bound.Add(new BoundMaterial(
                            materialIndex,
                            materialPath,
                            material,
                            effective,
                            lit,
                            transparentMaterial,
                            repeat,
                            wrapU,
                            wrapV));
                    }

                    var group = new DrawGroup(
                        submesh.StartIndex,
                        submesh.IndexCount,
                        boundIndex,
                        order++);
                    if (bound[boundIndex].Transparent)
                        transparent.Add(group);
                    else
                        opaque.Add(group);
                }
            }

            // Three.js sorts opaque render items by material identity. Bound materials are created
            // in first-use order, so sorting by their index reproduces that batching while keeping
            // the original order inside each material. Transparent groups keep authored order.
            DrawGroup[] opaqueOrdered = opaque
                .OrderBy(group => group.BoundMaterialIndex)
                .ThenBy(group => group.Order)
                .ToArray();
            return new DrawPlan(bound, opaqueOrdered, transparent);
        }

        internal static bool Covers(MapMaterialDefinition material) =>
            material == null ||
            material.BaseTexture != null ||
            string.IsNullOrWhiteSpace(material.ShaderPath) ||
            !IndicatorShader.IsMatch(material.ShaderPath);

        internal static Vector3 SrgbToLinear(Vector3 value) => new(
            SrgbChannelToLinear(value.X),
            SrgbChannelToLinear(value.Y),
            SrgbChannelToLinear(value.Z));

        private static Vector3 ResolveColor(MapMaterialDefinition material, bool hasTexture)
        {
            if (material?.Missing == false && material.Tint.HasValue)
                return SrgbToLinear(material.Tint.Value);
            return hasTexture ? Vector3.One : StoneLinear;
        }

        private static float SrgbChannelToLinear(float value) =>
            value <= 0.04045f
                ? value / 12.92f
                : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

        private static TextureWrapMode ToTextureWrapMode(MapTextureWrap wrap) =>
            wrap switch
            {
                MapTextureWrap.Clamp => TextureWrapMode.ClampToEdge,
                MapTextureWrap.Mirror => TextureWrapMode.MirroredRepeat,
                MapTextureWrap.Border => TextureWrapMode.ClampToEdge,
                _ => TextureWrapMode.Repeat
            };

        private void ReleaseSceneResources()
        {
            foreach (MaterialTexture texture in _materialTextures.Values)
                ReleaseTexture(texture.Bitmap);
            _materialTextures.Clear();

            DeleteBuffer(ref _positionVbo);
            DeleteBuffer(ref _normalVbo);
            DeleteBuffer(ref _uv0Vbo);
            DeleteBuffer(ref _uv1Vbo);
            DeleteBuffer(ref _ebo);
            if (_vao != 0)
            {
                _gl.DeleteVertexArray(_vao);
                _vao = 0;
            }

            _scene = null;
            _plan = null;
        }

        private void DeleteBuffer(ref uint buffer)
        {
            if (buffer == 0)
                return;
            _gl.DeleteBuffer(buffer);
            buffer = 0;
        }

        public void Dispose()
        {
            if (!_ready)
                return;

            try
            {
                ReleaseSceneResources();
                foreach (uint sampler in _samplers.Values)
                    if (sampler != 0)
                        _gl.DeleteSampler(sampler);
                _samplers.Clear();
                if (_whiteTexture != 0)
                    _gl.DeleteTexture(_whiteTexture);
                if (_program != 0)
                    _gl.DeleteProgram(_program);
            }
            catch (Silk.NET.Core.Loader.SymbolLoadingException)
            {
                // The OpenGL context owns remaining handles and reclaims them on teardown.
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _sharedTextures.Clear();
                _materialTextures.Clear();
                _samplers.Clear();
                _whiteTexture = 0;
                _program = 0;
                _ready = false;
                _gl = null;
                _drawElements = null;
            }
        }
    }
}
