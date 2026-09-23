using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Utils;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// Dedicated MAPGEO backdrop renderer aligned with the current LTK Manager MAIN viewport.
    /// One set of vertex/index buffers owns the whole map; submeshes are draw groups rather
    /// than SceneModel/ModelPart instances.
    /// </summary>
    internal sealed class MapGeometryRenderer : IDisposable
    {
        private const string IndicatorPattern = "indicator";

        private static readonly Vector3 StoneSrgb = new(154f / 255f, 149f / 255f, 140f / 255f);
        private static readonly Vector3 StoneLinear = SrgbToLinear(StoneSrgb);
        private static readonly Vector3 PreviewWireColor = new(92f / 255f, 133f / 255f, 1f);
        private static readonly Vector3 DefaultSunDirection = Vector3.Normalize(new Vector3(0.25f, 0.75f, -0.05f));
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
            bool MeshDoubleSided,
            Vector2 UvRepeat,
            MapTextureWrap WrapU,
            MapTextureWrap WrapV);

        internal readonly record struct DrawGroup(
            int StartIndex,
            int IndexCount,
            int BoundMaterialIndex,
            int MeshIndex,
            int Order);

        internal sealed record DrawPlan(
            IReadOnlyList<BoundMaterial> Materials,
            IReadOnlyList<DrawGroup> OpaqueGroups,
            IReadOnlyList<DrawGroup> TransparentGroups);

        internal readonly record struct LightState(
            Vector3 Direction,
            Vector3 SunColor,
            float SunStrength,
            Vector3 SkyColor,
            Vector3 GroundColor,
            Vector3 HorizonColor,
            float AmbientStrength,
            float LightMapColorScale);

        private sealed class SharedTexture
        {
            internal uint Id;
            internal int References;
        }

        private sealed record MaterialTexture(MapTextureImage Image, uint TextureId);

        internal enum TextureSamplingSpace
        {
            SrgbColor,
            LinearRaw
        }

        // League textures already arrive in DirectX row order: the first decoded row is v=0.
        // Preserve those rows verbatim, matching LTK's texture.flipY = false.
        internal const bool PreservesDirectXRowOrder = true;

        private readonly AppSettings _appSettings;
        private readonly Dictionary<MapTextureImage, SharedTexture> _sharedTextures =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<MapTextureImage, SharedTexture> _sharedRawTextures =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, MaterialTexture> _materialTextures =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, MaterialTexture> _programTextures =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, MaterialTexture> _lightmapTextures =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(MapTextureWrap U, MapTextureWrap V), uint> _samplers = new();
        private uint _lightmapSampler;

        private GL _gl;
        private DrawElementsDelegate _drawElements;
        private MapSceneData _scene;
        private MapSunData _previewSun;
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
        private int _uBakedLight;
        private int _uStationaryLight;
        private int _uColor;
        private int _uOpacity;
        private int _uAlphaTest;
        private int _uUvRepeat;
        private int _uLit;
        private int _uPremultipliedAlpha;
        private int _uLightDirection;
        private int _uSunColor;
        private int _uSunStrength;
        private int _uSkyColor;
        private int _uGroundColor;
        private int _uHorizonColor;
        private int _uAmbientStrength;
        private int _uLightMapColorScale;
        private int _uHasBakedLight;
        private int _uHasStationaryLight;
        private int _uBakedLightScale;
        private int _uBakedLightBias;
        private int _uStationaryLightScale;
        private int _uStationaryLightBias;
        private int _uWireframePass;
        private int _uWireframeColor;
        private LightState _light = ResolveLight(null);
        private GameShaderRuntime _gameShaderRuntime;
        private bool _gles;
        private bool _ready;

        internal bool HasScene => _scene != null && _plan != null && _vao != 0;

        internal MapGeometryRenderer(AppSettings appSettings = null)
        {
            _appSettings = appSettings;
        }

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
            _gles = embedded;
            _program = GlShaderCompiler.CreateProgram(
                gl,
                embedded,
                MapGeometryShaderSource.Vertex,
                MapGeometryShaderSource.Fragment);
            _uViewProjection = gl.GetUniformLocation(_program, "uViewProjection");
            _uBaseTexture = gl.GetUniformLocation(_program, "uBaseTexture");
            _uBakedLight = gl.GetUniformLocation(_program, "uBakedLight");
            _uStationaryLight = gl.GetUniformLocation(_program, "uStationaryLight");
            _uColor = gl.GetUniformLocation(_program, "uColor");
            _uOpacity = gl.GetUniformLocation(_program, "uOpacity");
            _uAlphaTest = gl.GetUniformLocation(_program, "uAlphaTest");
            _uUvRepeat = gl.GetUniformLocation(_program, "uUvRepeat");
            _uLit = gl.GetUniformLocation(_program, "uLit");
            _uPremultipliedAlpha = gl.GetUniformLocation(_program, "uPremultipliedAlpha");
            _uLightDirection = gl.GetUniformLocation(_program, "uLightDirection");
            _uSunColor = gl.GetUniformLocation(_program, "uSunColor");
            _uSunStrength = gl.GetUniformLocation(_program, "uSunStrength");
            _uSkyColor = gl.GetUniformLocation(_program, "uSkyColor");
            _uGroundColor = gl.GetUniformLocation(_program, "uGroundColor");
            _uHorizonColor = gl.GetUniformLocation(_program, "uHorizonColor");
            _uAmbientStrength = gl.GetUniformLocation(_program, "uAmbientStrength");
            _uLightMapColorScale = gl.GetUniformLocation(_program, "uLightMapColorScale");
            _uHasBakedLight = gl.GetUniformLocation(_program, "uHasBakedLight");
            _uHasStationaryLight = gl.GetUniformLocation(_program, "uHasStationaryLight");
            _uBakedLightScale = gl.GetUniformLocation(_program, "uBakedLightScale");
            _uBakedLightBias = gl.GetUniformLocation(_program, "uBakedLightBias");
            _uStationaryLightScale = gl.GetUniformLocation(_program, "uStationaryLightScale");
            _uStationaryLightBias = gl.GetUniformLocation(_program, "uStationaryLightBias");
            _uWireframePass = gl.GetUniformLocation(_program, "uWireframePass");
            _uWireframeColor = gl.GetUniformLocation(_program, "uWireframeColor");

            gl.UseProgram(_program);
            gl.Uniform1(_uBaseTexture, 0);
            gl.Uniform1(_uBakedLight, 1);
            gl.Uniform1(_uStationaryLight, 2);
            gl.UseProgram(0);
            _whiteTexture = CreateWhiteTexture();
            _lightmapSampler = CreateLightmapSampler();
            // Keep the large ShaderCache WAD and translated/linked game programs alive for the
            // renderer lifetime. Map variants commonly reuse the same 14-ish permutations.
            _gameShaderRuntime = _appSettings != null
                ? new GameShaderRuntime(_gl, _gles, _appSettings)
                : null;
            _ready = true;
        }

        internal void LoadScene(MapSceneData scene)
        {
            if (!_ready)
                throw new InvalidOperationException("MapGeometryRenderer must be initialized before loading a scene.");
            ArgumentNullException.ThrowIfNull(scene);

            ReleaseSceneResources();
            _scene = scene;
            _previewSun = scene.Sun;
            _plan = BuildDrawPlan(scene, scene.OpeningVisibilityFlags);
            _light = ResolveLight(scene.Sun);
            UploadGeometry(scene.Geometry);
            UpdateTextures(scene.Textures);
            UpdateProgramTextures(scene.ProgramTextures);
            UpdateLightmaps(scene.Lightmaps);
        }

        internal void SetPreviewSun(MapSunData effectiveSun, MapSunPreviewOverride? previewOverride)
        {
            if (!_ready || _scene == null)
                return;

            _previewSun = effectiveSun;
            _light = previewOverride.HasValue
                ? ResolveLight(effectiveSun, previewOverride.Value)
                : ResolveLight(effectiveSun);
        }

        /// <summary>
        /// Rebinds only texture images. Geometry, material classification and draw groups stay intact,
        /// allowing the 64px preview wave to sharpen to the 1024px wave without rebuilding the map.
        /// </summary>
        internal void UpdateTextures(IReadOnlyDictionary<string, MapTextureImage> textures)
        {
            if (!_ready || _scene == null || textures == null)
                return;

            foreach ((string materialPath, MapTextureImage image) in textures)
            {
                if (string.IsNullOrWhiteSpace(materialPath) || image == null)
                    continue;
                if (!_scene.Geometry.Materials.Contains(materialPath, StringComparer.Ordinal))
                    continue;

                if (_materialTextures.TryGetValue(materialPath, out MaterialTexture current) &&
                    ReferenceEquals(current.Image, image))
                {
                    continue;
                }

                uint textureId = AcquireTexture(image);
                if (_materialTextures.TryGetValue(materialPath, out current))
                    ReleaseTexture(current.Image);
                _materialTextures[materialPath] = new MaterialTexture(image, textureId);
            }
        }

        internal void UpdateProgramTextures(IReadOnlyDictionary<string, MapTextureImage> textures)
        {
            if (!_ready || _scene == null || textures == null)
                return;

            foreach ((string key, MapTextureImage image) in textures)
            {
                if (string.IsNullOrWhiteSpace(key) || image == null)
                    continue;
                if (_programTextures.TryGetValue(key, out MaterialTexture current) &&
                    ReferenceEquals(current.Image, image))
                {
                    continue;
                }

                // The translated game shader performs its own colour decode; LTK loads these
                // with NoColorSpace, so keep the texels raw rather than using an sRGB texture.
                uint textureId = AcquireRawTexture(image);
                if (_programTextures.TryGetValue(key, out current))
                    ReleaseRawTexture(current.Image);
                _programTextures[key] = new MaterialTexture(image, textureId);
            }
        }

        internal void UpdateLightmaps(IReadOnlyDictionary<string, MapTextureImage> lightmaps)
        {
            if (!_ready || _scene == null || lightmaps == null)
                return;

            foreach ((string path, MapTextureImage image) in lightmaps)
            {
                if (string.IsNullOrWhiteSpace(path) || image == null)
                    continue;
                if (!_scene.Geometry.Lightmaps.Contains(path, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (_lightmapTextures.TryGetValue(path, out MaterialTexture current) &&
                    ReferenceEquals(current.Image, image))
                {
                    continue;
                }

                // Light maps encode lighting data, not display colour. LTK loads them with
                // NoColorSpace, therefore they must remain linear/raw in the GPU texture.
                uint textureId = AcquireRawTexture(image);
                if (_lightmapTextures.TryGetValue(path, out current))
                    ReleaseRawTexture(current.Image);
                _lightmapTextures[path] = new MaterialTexture(image, textureId);
            }
        }

        internal void Render(
            Matrix4x4 viewProjection,
            Matrix4x4 view,
            Matrix4x4 projection,
            Vector3 eye,
            float timeSeconds,
            VfxPreviewViewMode viewMode = VfxPreviewViewMode.Lit,
            bool wireOverlay = false,
            bool shadersEnabled = true)
        {
            if (!_ready || !HasScene)
                return;

            (bool solids, bool wireframe, float wireOpacity) =
                ResolveViewPasses(viewMode, wireOverlay, supportsWireframe: !_gles);
            VfxPreviewViewMode solidMode = viewMode == VfxPreviewViewMode.Wireframe
                ? VfxPreviewViewMode.Lit
                : viewMode;
            var gameFrame = new GameShaderRuntime.Frame(
                view,
                projection,
                eye,
                timeSeconds,
                _previewSun);

            PrepareStockFrame(viewProjection);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindVertexArray(_vao);
            _gl.DepthFunc(DepthFunction.Lequal);

            // LTK mirrors the entire backdrop across X and lets Three.js flip the front face
            // from the negative world determinant. The stock shader mirrors X directly while
            // the translated game path carries the same mirror in PerFrameVertexCB.
            _gl.FrontFace(FrontFaceDirection.CW);
            try
            {
                if (solids)
                {
                    _gl.UseProgram(_program);
                    _gl.Uniform1(_uWireframePass, 0);
                    DrawGroups(_plan.OpaqueGroups, solidMode, shadersEnabled, in gameFrame);
                    DrawGroups(_plan.TransparentGroups, solidMode, shadersEnabled, in gameFrame);
                }

                if (wireframe)
                {
                    _gl.UseProgram(_program);
                    _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
                    _gl.Uniform1(_uWireframePass, 1);
                    _gl.Uniform4(
                        _uWireframeColor,
                        PreviewWireColor.X,
                        PreviewWireColor.Y,
                        PreviewWireColor.Z,
                        wireOpacity);
                    ApplyWireframeState(wireOpacity);
                    DrawWireGroups(_plan.OpaqueGroups);
                    DrawWireGroups(_plan.TransparentGroups);
                }
            }
            finally
            {
                if (!_gles)
                    _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
                _gameShaderRuntime?.ResetBindings();
                _gl.UseProgram(_program);
                _gl.Uniform1(_uWireframePass, 0);
                _gl.ColorMask(true, true, true, true);
                _gl.FrontFace(FrontFaceDirection.Ccw);
                _gl.Disable(EnableCap.Blend);
                _gl.Disable(EnableCap.CullFace);
                _gl.Enable(EnableCap.DepthTest);
                _gl.DepthMask(true);
                _gl.BindSampler(0, 0);
                _gl.ActiveTexture(TextureUnit.Texture1);
                _gl.BindSampler(1, 0);
                _gl.BindTexture(TextureTarget.Texture2D, 0);
                _gl.ActiveTexture(TextureUnit.Texture2);
                _gl.BindSampler(2, 0);
                _gl.BindTexture(TextureTarget.Texture2D, 0);
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.Texture2D, 0);
                _gl.BindVertexArray(0);
                _gl.UseProgram(0);
            }
        }

        private void PrepareStockFrame(Matrix4x4 viewProjection)
        {
            _gl.UseProgram(_program);
            _gl.UniformMatrix4(_uViewProjection, 1, false, in viewProjection.M11);
            _gl.Uniform3(_uLightDirection, _light.Direction.X, _light.Direction.Y, _light.Direction.Z);
            _gl.Uniform3(_uSunColor, _light.SunColor.X, _light.SunColor.Y, _light.SunColor.Z);
            _gl.Uniform1(_uSunStrength, _light.SunStrength);
            _gl.Uniform3(_uSkyColor, _light.SkyColor.X, _light.SkyColor.Y, _light.SkyColor.Z);
            _gl.Uniform3(_uGroundColor, _light.GroundColor.X, _light.GroundColor.Y, _light.GroundColor.Z);
            _gl.Uniform3(_uHorizonColor, _light.HorizonColor.X, _light.HorizonColor.Y, _light.HorizonColor.Z);
            _gl.Uniform1(_uAmbientStrength, _light.AmbientStrength);
            _gl.Uniform1(_uLightMapColorScale, _light.LightMapColorScale);
        }

        internal static (bool Solids, bool Wireframe, float WireOpacity) ResolveViewPasses(
            VfxPreviewViewMode viewMode,
            bool wireOverlay,
            bool supportsWireframe)
        {
            bool wireframeOnly = viewMode == VfxPreviewViewMode.Wireframe;
            bool overlayAllowed = viewMode == VfxPreviewViewMode.Lit || viewMode == VfxPreviewViewMode.Untextured;
            bool solids = !wireframeOnly || !supportsWireframe;
            bool wireframe = supportsWireframe && (wireframeOnly || (wireOverlay && overlayAllowed));
            return (solids, wireframe, wireframeOnly ? 1f : 0.35f);
        }

        private void DrawGroups(
            IReadOnlyList<DrawGroup> groups,
            VfxPreviewViewMode viewMode,
            bool shadersEnabled,
            in GameShaderRuntime.Frame gameFrame)
        {
            _gl.UseProgram(_program);
            int activeStockMaterial = -1;
            int activeStockMesh = -1;
            bool stockActive = true;
            foreach (DrawGroup group in groups)
            {
                if (group.IndexCount <= 0 || group.BoundMaterialIndex < 0 ||
                    group.BoundMaterialIndex >= _plan.Materials.Count)
                {
                    continue;
                }

                BoundMaterial bound = _plan.Materials[group.BoundMaterialIndex];
                MapGeometryMeshData mesh = group.MeshIndex >= 0 && group.MeshIndex < _scene.Geometry.Meshes.Count
                    ? _scene.Geometry.Meshes[group.MeshIndex]
                    : null;
                bool gameBound = shadersEnabled &&
                                 viewMode == VfxPreviewViewMode.Lit &&
                                 _gameShaderRuntime?.TryBind(
                                     bound.Material,
                                     mesh,
                                     bound.MeshDoubleSided,
                                     in gameFrame,
                                     ResolveProgramTexture,
                                     ResolveLightmapTexture) == true;

                if (gameBound)
                {
                    // Game programs carry material, lighting and lightmap state themselves.
                    // Reset stock batching so a later fallback always rebinds the preview shader.
                    stockActive = false;
                    activeStockMaterial = -1;
                    activeStockMesh = -1;
                }
                else
                {
                    if (!stockActive)
                    {
                        _gl.UseProgram(_program);
                        stockActive = true;
                    }
                    if (activeStockMaterial != group.BoundMaterialIndex)
                    {
                        activeStockMaterial = group.BoundMaterialIndex;
                        ApplyMaterial(bound, viewMode);
                    }
                    if (activeStockMesh != group.MeshIndex)
                    {
                        activeStockMesh = group.MeshIndex;
                        ApplyMeshLighting(activeStockMesh);
                    }
                }

                _drawElements(
                    (uint)PrimitiveType.Triangles,
                    group.IndexCount,
                    (uint)DrawElementsType.UnsignedInt,
                    new IntPtr(checked(group.StartIndex * sizeof(uint))));
            }
        }

        private uint? ResolveProgramTexture(string key) =>
            !string.IsNullOrWhiteSpace(key) && _programTextures.TryGetValue(key, out MaterialTexture texture)
                ? texture.TextureId
                : null;

        private uint? ResolveLightmapTexture(string path) =>
            !string.IsNullOrWhiteSpace(path) && _lightmapTextures.TryGetValue(path, out MaterialTexture texture)
                ? texture.TextureId
                : null;

        private void DrawWireGroups(IReadOnlyList<DrawGroup> groups)
        {
            foreach (DrawGroup group in groups)
            {
                if (group.IndexCount <= 0)
                    continue;

                _drawElements(
                    (uint)PrimitiveType.Triangles,
                    group.IndexCount,
                    (uint)DrawElementsType.UnsignedInt,
                    new IntPtr(checked(group.StartIndex * sizeof(uint))));
            }
        }

        private void ApplyWireframeState(float opacity)
        {
            _gl.Disable(EnableCap.CullFace);
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Lequal);
            bool overlay = opacity < 1f;
            _gl.DepthMask(!overlay);
            if (overlay)
            {
                _gl.Enable(EnableCap.Blend);
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            }
            else
            {
                _gl.Disable(EnableCap.Blend);
            }
        }

        private void ApplyMaterial(BoundMaterial bound, VfxPreviewViewMode viewMode)
        {
            MapMaterialDefinition material = bound.Material;
            bool untextured = viewMode == VfxPreviewViewMode.Untextured;
            bool unshaded = viewMode == VfxPreviewViewMode.Unshaded;
            MaterialTexture texture = null;
            bool hasTexture = !untextured &&
                              !string.IsNullOrWhiteSpace(bound.MaterialPath) &&
                              _materialTextures.TryGetValue(bound.MaterialPath, out texture);
            uint textureId = hasTexture ? texture.TextureId : _whiteTexture;
            Vector3 color = untextured ? StoneLinear : ResolveColor(material, hasTexture);
            float opacity = untextured ? 1f : material?.Missing == false ? material.Opacity ?? 1f : 1f;
            float alphaTest = untextured ? 0f : material?.Missing == false ? material.AlphaTest ?? 0f : 0f;
            bool lit = untextured || (!unshaded && bound.Lit);
            MapMaterialRenderState renderState = untextured
                ? MapMaterialRenderState.Default with { DoubleSided = bound.RenderState.DoubleSided }
                : bound.RenderState;
            bool transparent = !untextured && bound.Transparent;

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, textureId);
            _gl.BindSampler(0, ResolveSampler(bound.WrapU, bound.WrapV));
            _gl.Uniform3(_uColor, color.X, color.Y, color.Z);
            _gl.Uniform1(_uOpacity, opacity);
            _gl.Uniform1(_uAlphaTest, alphaTest);
            _gl.Uniform2(_uUvRepeat, bound.UvRepeat.X, bound.UvRepeat.Y);
            _gl.Uniform1(_uLit, lit ? 1 : 0);
            _gl.Uniform1(_uPremultipliedAlpha, renderState.PremultipliedAlpha ? 1 : 0);
            ApplyRenderState(renderState, transparent);
        }

        private void ApplyMeshLighting(int meshIndex)
        {
            if (meshIndex < 0 || meshIndex >= _scene.Geometry.Meshes.Count || !_scene.Geometry.HasUv1)
            {
                _gl.Uniform1(_uHasBakedLight, 0);
                _gl.Uniform1(_uHasStationaryLight, 0);
                return;
            }

            MapGeometryMeshData mesh = _scene.Geometry.Meshes[meshIndex];
            BindLightChannel(mesh.BakedLight, TextureUnit.Texture1, 1, _uHasBakedLight, _uBakedLightScale, _uBakedLightBias);
            BindLightChannel(mesh.StationaryLight, TextureUnit.Texture2, 2, _uHasStationaryLight, _uStationaryLightScale, _uStationaryLightBias);
            _gl.ActiveTexture(TextureUnit.Texture0);
        }

        private void BindLightChannel(
            MapGeometryLightChannelData channel,
            TextureUnit textureUnit,
            uint samplerUnit,
            int hasLocation,
            int scaleLocation,
            int biasLocation)
        {
            MaterialTexture texture = null;
            bool available = channel?.IsEmpty == false &&
                             _lightmapTextures.TryGetValue(channel.Texture, out texture);
            _gl.Uniform1(hasLocation, available ? 1 : 0);
            _gl.Uniform2(scaleLocation, channel?.Scale.X ?? 1f, channel?.Scale.Y ?? 1f);
            _gl.Uniform2(biasLocation, channel?.Bias.X ?? 0f, channel?.Bias.Y ?? 0f);
            _gl.ActiveTexture(textureUnit);
            _gl.BindTexture(TextureTarget.Texture2D, available ? texture.TextureId : _whiteTexture);
            _gl.BindSampler(samplerUnit, _lightmapSampler);
        }

        private void ApplyRenderState(MapMaterialRenderState state, bool transparent)
        {
            // A previous game pass may have changed these states; the stock preview path
            // always writes colour and uses the normal LEQUAL depth comparison.
            _gl.ColorMask(true, true, true, true);
            _gl.DepthFunc(DepthFunction.Lequal);
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

        private uint AcquireTexture(MapTextureImage image)
        {
            if (_sharedTextures.TryGetValue(image, out SharedTexture shared))
            {
                shared.References++;
                return shared.Id;
            }

            // Stock/base material textures use the stage's sRGB colour space. OpenGL's sRGB
            // internal format gives the shader the same linear sample Three.js produces.
            uint id = UploadTexture(image, TextureSamplingSpace.SrgbColor);
            _sharedTextures[image] = new SharedTexture { Id = id, References = 1 };
            return id;
        }

        private void ReleaseTexture(MapTextureImage image) =>
            ReleaseSharedTexture(_sharedTextures, image);

        private uint AcquireRawTexture(MapTextureImage image) =>
            AcquireSharedTexture(_sharedRawTextures, image, TextureSamplingSpace.LinearRaw);

        private void ReleaseRawTexture(MapTextureImage image) =>
            ReleaseSharedTexture(_sharedRawTextures, image);

        private uint AcquireSharedTexture(
            Dictionary<MapTextureImage, SharedTexture> cache,
            MapTextureImage image,
            TextureSamplingSpace samplingSpace)
        {
            if (cache.TryGetValue(image, out SharedTexture shared))
            {
                shared.References++;
                return shared.Id;
            }

            uint id = UploadTexture(image, samplingSpace);
            cache[image] = new SharedTexture { Id = id, References = 1 };
            return id;
        }

        private void ReleaseSharedTexture(
            Dictionary<MapTextureImage, SharedTexture> cache,
            MapTextureImage image)
        {
            if (image == null || !cache.TryGetValue(image, out SharedTexture shared))
                return;

            shared.References--;
            if (shared.References > 0)
                return;

            if (shared.Id != 0)
                _gl.DeleteTexture(shared.Id);
            cache.Remove(image);
        }

        private uint UploadTexture(MapTextureImage image, TextureSamplingSpace samplingSpace)
        {
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);

            for (int level = 0; level < image.MipLevels.Count; level++)
                UploadTextureLevel(image.MipLevels[level], level, samplingSpace);

            // Current LTK MAIN preserves every authored level. Only a one-level source lets the
            // GPU synthesize the missing chain, which also covers PNG/TGA fallback textures.
            if (!image.HasAuthoredMipChain)
                _gl.GenerateMipmap(TextureTarget.Texture2D);

            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            return texture;
        }

        private void UploadTextureLevel(BitmapSource source, int level, TextureSamplingSpace samplingSpace)
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
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                level,
                TextureInternalFormat(samplingSpace),
                (uint)width,
                (uint)height,
                0,
                Silk.NET.OpenGL.PixelFormat.Bgra,
                PixelType.UnsignedByte,
                new ReadOnlySpan<byte>(pixels));
        }

        internal static InternalFormat TextureInternalFormat(TextureSamplingSpace samplingSpace) =>
            samplingSpace == TextureSamplingSpace.SrgbColor
                ? InternalFormat.Srgb8Alpha8
                : InternalFormat.Rgba8;

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

        private uint CreateLightmapSampler()
        {
            uint sampler = _gl.GenSampler();
            _gl.SamplerParameter(sampler, SamplerParameterI.MinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.SamplerParameter(sampler, SamplerParameterI.MagFilter, (int)TextureMagFilter.Linear);
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapT, (int)TextureWrapMode.ClampToEdge);
            return sampler;
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

        internal void SetVisibilityFlags(int flags)
        {
            if (!_ready || _scene == null)
                return;
            _plan = BuildDrawPlan(_scene, flags);
        }

        internal static DrawPlan BuildDrawPlan(MapSceneData scene)
            => BuildDrawPlan(scene, scene?.OpeningVisibilityFlags ?? 0);

        internal static DrawPlan BuildDrawPlan(MapSceneData scene, int visibilityFlags)
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
                if (!mesh.IsVisibleForFlags(visibilityFlags) || mesh.SubmeshCount <= 0)
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
                            meshDoubleSided,
                            repeat,
                            wrapU,
                            wrapV));
                    }

                    var group = new DrawGroup(
                        submesh.StartIndex,
                        submesh.IndexCount,
                        boundIndex,
                        meshIndex,
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

        internal static LightState ResolveLight(MapSunData sun)
        {
            if (sun == null)
            {
                return new LightState(
                    DefaultSunDirection,
                    Vector3.One,
                    0.4f,
                    Vector3.One,
                    Vector3.One,
                    Vector3.One,
                    0.6f,
                    1f);
            }

            Vector3 direction = sun.Direction;
            if (!IsFinite(direction) || direction.LengthSquared() <= 1e-12f)
                direction = new Vector3(-0.25f, 0.75f, -0.05f);
            direction = Vector3.Normalize(direction);
            direction.X = -direction.X;

            float sunStrength = MathF.Max(sun.Intensity, 0f);
            float ambientStrength = MathF.Max(sun.SkyScale, 0f);
            float total = sunStrength + ambientStrength;
            if (total > 0f)
            {
                sunStrength /= total;
                ambientStrength /= total;
            }
            else
            {
                sunStrength = 0.5f;
                ambientStrength = 0.5f;
            }

            return new LightState(
                direction,
                SrgbToLinear(new Vector3(sun.Color.X, sun.Color.Y, sun.Color.Z)),
                sunStrength,
                SrgbToLinear(new Vector3(sun.SkyColor.X, sun.SkyColor.Y, sun.SkyColor.Z)),
                SrgbToLinear(new Vector3(sun.GroundColor.X, sun.GroundColor.Y, sun.GroundColor.Z)),
                SrgbToLinear(new Vector3(sun.HorizonColor.X, sun.HorizonColor.Y, sun.HorizonColor.Z)),
                ambientStrength,
                MathF.Max(sun.LightMapColorScale, 0f));
        }

        internal static LightState ResolveLight(MapSunData sun, MapSunPreviewOverride previewOverride)
        {
            LightState own = ResolveLight(sun);
            Vector3 direction = previewOverride.Direction;
            if (!IsFinite(direction) || direction.LengthSquared() <= 1e-12f)
                direction = MapPreviewSemantics.DefaultSun.Direction;
            direction = Vector3.Normalize(direction);
            direction.X = -direction.X;

            return own with
            {
                Direction = direction,
                SunColor = SrgbToLinear(new Vector3(previewOverride.Color.X, previewOverride.Color.Y, previewOverride.Color.Z)),
                SunStrength = MathF.Max(previewOverride.Strength, 0f),
                SkyColor = SrgbToLinear(new Vector3(previewOverride.SkyColor.X, previewOverride.SkyColor.Y, previewOverride.SkyColor.Z)),
                GroundColor = SrgbToLinear(new Vector3(previewOverride.GroundColor.X, previewOverride.GroundColor.Y, previewOverride.GroundColor.Z)),
                AmbientStrength = MathF.Max(previewOverride.Ambient, 0f)
            };
        }

        internal static Vector3 SrgbToLinear(Vector3 value) => new(
            SrgbChannelToLinear(value.X),
            SrgbChannelToLinear(value.Y),
            SrgbChannelToLinear(value.Z));

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

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

        internal void ClearScene()
        {
            if (!_ready || _scene == null)
                return;
            ReleaseSceneResources();
        }

        private void ReleaseSceneResources()
        {
            foreach (MaterialTexture texture in _materialTextures.Values)
                ReleaseTexture(texture.Image);
            _materialTextures.Clear();
            foreach (MaterialTexture texture in _programTextures.Values)
                ReleaseRawTexture(texture.Image);
            _programTextures.Clear();
            foreach (MaterialTexture texture in _lightmapTextures.Values)
                ReleaseRawTexture(texture.Image);
            _lightmapTextures.Clear();

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
            _previewSun = null;
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
                _gameShaderRuntime?.Dispose();
                _gameShaderRuntime = null;
                foreach (uint sampler in _samplers.Values)
                    if (sampler != 0)
                        _gl.DeleteSampler(sampler);
                _samplers.Clear();
                if (_lightmapSampler != 0)
                    _gl.DeleteSampler(_lightmapSampler);
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
                _sharedRawTextures.Clear();
                _materialTextures.Clear();
                _programTextures.Clear();
                _lightmapTextures.Clear();
                _samplers.Clear();
                _lightmapSampler = 0;
                _whiteTexture = 0;
                _program = 0;
                _ready = false;
                _gl = null;
                _drawElements = null;
            }
        }
    }
}
