using AssetsManager.Services.Viewer.Resources;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Utils;
using AssetsManager.Utils.Rendering;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Hashing;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// Dedicated renderer for structures and level props placed by a MAP scene.
    /// One GPU resource set is kept per skin while every placement only contributes a world transform.
    /// </summary>
    internal sealed class MapCharacterRenderer : IDisposable
    {
        /// <summary>Environment cube PBR game shaders light from (IBL); the preview sky the viewport shows.</summary>
        internal CubeMapData ImageLight { get; set; }

        private sealed class SkinResources
        {
            internal MapCharacterAssetData Asset;
            internal Func<string, uint?> ProgramTextureLookup;
            internal Vector3 BoundsCenter;
            internal uint Vao;
            internal uint PositionVbo;
            internal uint NormalVbo;
            internal uint UvVbo;
            internal uint TangentVbo;
            internal uint ColorVbo;
            internal uint SkinIndexVbo;
            internal uint SkinWeightVbo;
            internal uint Ebo;
            internal bool HasSkin;
            internal int[] InfluenceJointSlots = Array.Empty<int>();
            internal IReadOnlyList<BoundRange> Ranges = Array.Empty<BoundRange>();
            internal string[] TextureKeys = Array.Empty<string>();
            internal readonly Dictionary<string, uint> ProgramTexturesByPath = new(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, Matrix4x4[]> Palettes = new(StringComparer.OrdinalIgnoreCase);
        }

        internal sealed record BoundRange(
            int StartIndex,
            int IndexCount,
            string Name,
            uint NameHash,
            bool HiddenByDefault,
            ModelMaterialDefinition Material,
            BitmapSource Texture,
            bool Lit,
            bool Transparent,
            ModelMaterialWrapMode WrapU,
            ModelMaterialWrapMode WrapV);

        private bool _gameBindingsActive;

        private readonly record struct DrawCommand(
            SkinResources Resources,
            BoundRange Range,
            Matrix4x4 World,
            Matrix4x4[] Palette,
            float DistanceSquared,
            float TimeSeconds,
            float SelfIllumination = 0f,
            int PassIndex = -1,
            int Order = 0);

        private static readonly Vector3 PreviewWireColor = new(92f / 255f, 133f / 255f, 1f);
        private static readonly Vector3 DefaultUntextured = new(0.5f);
        private static readonly Vector3 DefaultErrored = new(1f, 0.08f, 0.16f);

        private readonly AppSettings _appSettings;
        private readonly Dictionary<MapCharacterAssetData, SkinResources> _skins =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<BitmapSource, uint> _textures =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<BitmapSource, uint> _rawProgramTextures =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<(ModelMaterialWrapMode U, ModelMaterialWrapMode V), uint> _samplers = new();
        private readonly List<DrawCommand> _passSource = new();
        private readonly List<DrawCommand> _opaque = new();
        private readonly List<DrawCommand> _transparent = new();

        private GL _gl;
        private GameShaderRuntime.DrawElementsDelegate _drawElements;
        private uint _program;
        private uint _boneBuffer;
        private uint _whiteTexture;
        private int _uViewProjection;
        private int _uWorld;
        private int _uUseSkinning;
        private int _uBaseTexture;
        private int _uHasTexture;
        private int _uColor;
        private int _uOpacity;
        private int _uAlphaTest;
        private int _uUvRepeat;
        private int _uUvOffset;
        private int _uLit;
        private int _uPremultipliedAlpha;
        private int _uLightDirection;
        private int _uSunColor;
        private int _uSunStrength;
        private int _uSkyColor;
        private int _uGroundColor;
        private int _uAmbientStrength;
        private int _uWireframePass;
        private int _uWireframeColor;
        private int _uSelfIllumination;
        private GlLightGridBindings _lightGridBindings;
        private GameShaderRuntime _gameShaderRuntime;
        private bool _gles;
        private bool _ready;

        internal MapCharacterRenderer(AppSettings appSettings = null)
        {
            _appSettings = appSettings;
        }

        internal void Initialize(GL gl)
        {
            ArgumentNullException.ThrowIfNull(gl);
            if (_ready) return;

            _gl = gl;
            IntPtr drawElements = gl.Context.GetProcAddress("glDrawElements");
            if (drawElements == IntPtr.Zero)
                throw new InvalidOperationException("OpenGL glDrawElements is unavailable for MAP character rendering.");
            _drawElements = Marshal.GetDelegateForFunctionPointer<GameShaderRuntime.DrawElementsDelegate>(drawElements);

            _gles = GlShaderCompiler.UsesEmbeddedProfile(gl);
            _program = GlShaderCompiler.CreateProgram(
                gl,
                _gles,
                MapCharacterShaderSource.Vertex,
                MapCharacterShaderSource.Fragment);
            _uViewProjection = gl.GetUniformLocation(_program, "uViewProjection");
            _uWorld = gl.GetUniformLocation(_program, "uWorld");
            _uUseSkinning = gl.GetUniformLocation(_program, "uUseSkinning");
            _uBaseTexture = gl.GetUniformLocation(_program, "uBaseTexture");
            _uHasTexture = gl.GetUniformLocation(_program, "uHasTexture");
            _uColor = gl.GetUniformLocation(_program, "uColor");
            _uOpacity = gl.GetUniformLocation(_program, "uOpacity");
            _uAlphaTest = gl.GetUniformLocation(_program, "uAlphaTest");
            _uUvRepeat = gl.GetUniformLocation(_program, "uUvRepeat");
            _uUvOffset = gl.GetUniformLocation(_program, "uUvOffset");
            _uLit = gl.GetUniformLocation(_program, "uLit");
            _uPremultipliedAlpha = gl.GetUniformLocation(_program, "uPremultipliedAlpha");
            _uLightDirection = gl.GetUniformLocation(_program, "uLightDirection");
            _uSunColor = gl.GetUniformLocation(_program, "uSunColor");
            _uSunStrength = gl.GetUniformLocation(_program, "uSunStrength");
            _uSkyColor = gl.GetUniformLocation(_program, "uSkyColor");
            _uGroundColor = gl.GetUniformLocation(_program, "uGroundColor");
            _uAmbientStrength = gl.GetUniformLocation(_program, "uAmbientStrength");
            _uWireframePass = gl.GetUniformLocation(_program, "uWireframePass");
            _uWireframeColor = gl.GetUniformLocation(_program, "uWireframeColor");
            _uSelfIllumination = gl.GetUniformLocation(_program, "uSelfIllumination");
            _lightGridBindings = new GlLightGridBindings(gl, _program);

            uint boneBlock = gl.GetUniformBlockIndex(_program, "BoneTransforms");
            if (boneBlock != uint.MaxValue)
                gl.UniformBlockBinding(_program, boneBlock, 0);
            _boneBuffer = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.UniformBuffer, _boneBuffer);
            gl.BufferData(
                BufferTargetARB.UniformBuffer,
                new ReadOnlySpan<float>(new float[MapCharacterShaderSource.MaximumBones * 16]),
                BufferUsageARB.DynamicDraw);
            gl.BindBufferBase(BufferTargetARB.UniformBuffer, 0, _boneBuffer);
            gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);

            gl.UseProgram(_program);
            gl.Uniform1(_uBaseTexture, 0);
            gl.UseProgram(0);
            _whiteTexture = CreateWhiteTexture();
            _gameShaderRuntime = _appSettings != null
                ? new GameShaderRuntime(_gl, _gles, _appSettings)
                : null;
            _ready = true;
        }

        /// <summary>
        /// The viewport's shared glow: passes whose shaders write bloom add to it after each draw phase; the
        /// viewport composes it once per frame. Null leaves map characters without bloom.
        /// </summary>
        internal GameShaderBloom Bloom { get; set; }

        internal void Render(
            IReadOnlyList<MapCharacterRuntimeGroup> groups,
            Matrix4x4 viewProjection,
            Matrix4x4 view,
            Matrix4x4 projection,
            Vector3 cameraPosition,
            float timeSeconds,
            MapSunData sun,
            IReadOnlySet<string> hidden = null,
            Vector3? untexturedLinear = null,
            Vector3? erroredLinear = null,
            StudioViewMode viewMode = StudioViewMode.Lit,
            bool wireOverlay = false,
            bool shadersEnabled = true,
            MapLightGridData lightGrid = null,
            bool? transparentPass = null)
        {
            if (!_ready || groups == null || groups.Count == 0)
                return;

            Vector3 untextured = untexturedLinear ?? DefaultUntextured;
            Vector3 errored = erroredLinear ?? DefaultErrored;
            if (transparentPass != true)
            {
                BuildQueues(groups, cameraPosition, timeSeconds, hidden);
                PreparePassQueues(shadersEnabled && viewMode == StudioViewMode.Lit);
            }
            if (_opaque.Count == 0 && _transparent.Count == 0)
                return;

            (bool solids, bool wireframe, float wireOpacity) =
                MapGeometryRenderer.ResolveViewPasses(viewMode, wireOverlay, supportsWireframe: !_gles);
            StudioViewMode solidMode = viewMode == StudioViewMode.Wireframe
                ? StudioViewMode.Lit
                : viewMode;

            var gameFrame = new GameShaderRuntime.Frame(
                view,
                projection,
                cameraPosition,
                timeSeconds,
                sun,
                lightGrid,
                ImageLight: ImageLight);
            UseStockProgram(viewProjection, gameFrame.Sun);
            _gl.DepthFunc(DepthFunction.Lequal);

            Matrix4x4[] activePalette = null;
            uint activeVao = 0;
            try
            {
                if (solids)
                {
                    _gl.Uniform1(_uWireframePass, 0);
                    bool glows = Bloom != null && shadersEnabled && solidMode == StudioViewMode.Lit;
                    if (transparentPass != true)
                    {
                        DrawQueue(_opaque, viewProjection, in gameFrame, untextured, errored, solidMode, shadersEnabled, wireframePass: false, ref activePalette, ref activeVao);
                        if (glows)
                            DrawBloomPasses(_opaque, in gameFrame, ref activePalette, ref activeVao);
                    }
                    if (transparentPass != false)
                    {
                        DrawQueue(_transparent, viewProjection, in gameFrame, untextured, errored, solidMode, shadersEnabled, wireframePass: false, ref activePalette, ref activeVao);
                        if (glows)
                            DrawBloomPasses(_transparent, in gameFrame, ref activePalette, ref activeVao);
                    }
                }

                if (wireframe && transparentPass != false)
                {
                    activePalette = null;
                    activeVao = 0;
                    ResetGameBindings();
                    UseStockProgram(viewProjection, gameFrame.Sun);
                    _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
                    _gl.Uniform1(_uWireframePass, 1);
                    _gl.Uniform4(
                        _uWireframeColor,
                        PreviewWireColor.X,
                        PreviewWireColor.Y,
                        PreviewWireColor.Z,
                        wireOpacity);
                    ApplyWireframeState(wireOpacity);
                    DrawQueue(_opaque, viewProjection, in gameFrame, untextured, errored, solidMode, shadersEnabled: false, wireframePass: true, ref activePalette, ref activeVao);
                    DrawQueue(_transparent, viewProjection, in gameFrame, untextured, errored, solidMode, shadersEnabled: false, wireframePass: true, ref activePalette, ref activeVao);
                }
            }
            finally
            {
                if (!_gles)
                    _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
                ResetGameBindings();
                _gl.UseProgram(_program);
                _gl.Uniform1(_uWireframePass, 0);
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

        private void BuildQueues(
            IReadOnlyList<MapCharacterRuntimeGroup> groups,
            Vector3 cameraPosition,
            float timeSeconds,
            IReadOnlySet<string> hidden = null)
        {
            _opaque.Clear();
            _transparent.Clear();

            foreach (MapCharacterRuntimeGroup group in groups)
            {
                if (group?.Asset == null || group.Animation == null || group.Placements == null)
                    continue;
                SkinResources resources = EnsureResources(group.Asset);
                if (resources?.Ranges == null || resources.Ranges.Count == 0)
                    continue;

                if (group.PreviewClip != null && group.PreviewPlacement != null)
                {
                    Matrix4x4[] previewJoints = group.Animation.EvaluateClip(
                        group.Asset,
                        group.PreviewClip,
                        group.PreviewTimeSeconds);
                    Matrix4x4[] previewPalette = GetPalette(resources, $"preview:{group.PreviewClip.OwnerPathHash:x8}");
                    FillInfluencePalette(previewPalette, previewJoints, resources.InfluenceJointSlots);

                    foreach (MapCharacterAnimationPlacementGroup animationGroup in group.AnimationGroups)
                    {
                        Matrix4x4[] authoredJoints = group.Animation.Evaluate(
                            group.Asset,
                            animationGroup.Animation,
                            timeSeconds);
                        Matrix4x4[] authoredPalette = GetPalette(resources, animationGroup.Animation);
                        FillInfluencePalette(authoredPalette, authoredJoints, resources.InfluenceJointSlots);

                        foreach (MapCharacterRuntimePlacement runtimePlacement in animationGroup.Placements)
                        {
                            bool previewPlacement = ReferenceEquals(runtimePlacement.Placement, group.PreviewPlacement);
                            QueuePlacement(
                                group,
                                resources,
                                runtimePlacement,
                                previewPlacement ? previewPalette : authoredPalette,
                                cameraPosition,
                                hidden,
                                timeSeconds,
                                previewPlacement);
                        }
                    }
                    continue;
                }

                foreach (MapCharacterAnimationPlacementGroup animationGroup in group.AnimationGroups)
                {
                    Matrix4x4[] joints = group.Animation.Evaluate(group.Asset, animationGroup.Animation, timeSeconds);
                    Matrix4x4[] palette = GetPalette(resources, animationGroup.Animation);
                    FillInfluencePalette(palette, joints, resources.InfluenceJointSlots);
                    QueuePlacements(group, resources, animationGroup.Placements, palette, cameraPosition, hidden, timeSeconds);
                }
            }
        }

        private void QueuePlacements(
            MapCharacterRuntimeGroup group,
            SkinResources resources,
            IReadOnlyList<MapCharacterRuntimePlacement> placements,
            Matrix4x4[] palette,
            Vector3 cameraPosition,
            IReadOnlySet<string> hidden,
            float timeSeconds)
        {
            foreach (MapCharacterRuntimePlacement runtimePlacement in placements)
            {
                QueuePlacement(
                    group,
                    resources,
                    runtimePlacement,
                    palette,
                    cameraPosition,
                    hidden,
                    timeSeconds,
                    previewVisibility: false);
            }
        }

        private void QueuePlacement(
            MapCharacterRuntimeGroup group,
            SkinResources resources,
            MapCharacterRuntimePlacement runtimePlacement,
            Matrix4x4[] palette,
            Vector3 cameraPosition,
            IReadOnlySet<string> hidden,
            float timeSeconds,
            bool previewVisibility)
        {
            MapCharacterData placement = runtimePlacement.Placement;
            if (MapOutlineSemantics.IsHidden(hidden, placement.ChunkHash, placement.KeyHash))
                return;

            Matrix4x4 world = runtimePlacement.World;
            float distance = Vector3.DistanceSquared(runtimePlacement.Position, cameraPosition);
            foreach (BoundRange range in resources.Ranges)
            {
                bool submeshHidden = previewVisibility
                    ? group.PreviewHiddenSubmeshes.Contains(range.NameHash)
                    : range.HiddenByDefault;
                if (submeshHidden)
                    continue;

                var command = new DrawCommand(resources, range, world, palette, distance, timeSeconds, group.Asset?.Materials?.SelfIllumination ?? 0f);
                if (range.Transparent) _transparent.Add(command);
                else _opaque.Add(command);
            }
        }

        private void PreparePassQueues(bool shadersEnabled)
        {
            _passSource.Clear();
            _passSource.AddRange(_opaque);
            _passSource.AddRange(_transparent);
            _opaque.Clear();
            _transparent.Clear();
            int order = 0;
            foreach (DrawCommand command in _passSource)
            {
                int count = shadersEnabled && command.Resources.HasSkin
                    ? _gameShaderRuntime?.GetSkinnedPassCount(command.Range.Material) ?? 0
                    : 0;
                if (count == 0)
                {
                    DrawCommand fallback = command with { Order = order++ };
                    (command.Range.Transparent ? _transparent : _opaque).Add(fallback);
                    continue;
                }

                for (int pass = 0; pass < count; pass++)
                {
                    GameMaterialPassState state = _gameShaderRuntime.GetSkinnedPassState(command.Range.Material, pass);
                    DrawCommand layered = command with { PassIndex = pass, Order = order++ };
                    (state.BlendEnabled && !command.Range.Material.BlendsInDrawOrder ? _transparent : _opaque).Add(layered);
                }
            }
            _opaque.Sort((left, right) => ComparePassOrder(
                left.PassIndex, left.DistanceSquared, left.Order,
                right.PassIndex, right.DistanceSquared, right.Order, false));
            _transparent.Sort((left, right) => ComparePassOrder(
                left.PassIndex, left.DistanceSquared, left.Order,
                right.PassIndex, right.DistanceSquared, right.Order, true));
        }

        internal static int ComparePassOrder(
            int leftPass, float leftDistance, int leftOrder,
            int rightPass, float rightDistance, int rightOrder, bool transparent)
        {
            int layer = Math.Max(leftPass, 0).CompareTo(Math.Max(rightPass, 0));
            if (layer != 0)
                return layer;
            int distance = transparent ? rightDistance.CompareTo(leftDistance) : 0;
            return distance != 0 ? distance : leftOrder.CompareTo(rightOrder);
        }

        private void DrawQueue(
            IReadOnlyList<DrawCommand> queue,
            Matrix4x4 viewProjection,
            in GameShaderRuntime.Frame gameFrame,
            Vector3 untextured,
            Vector3 errored,
            StudioViewMode viewMode,
            bool shadersEnabled,
            bool wireframePass,
            ref Matrix4x4[] activePalette,
            ref uint activeVao)
        {
            foreach (DrawCommand command in queue)
            {
                if (wireframePass && command.PassIndex > 0)
                    continue;
                if (command.Resources.Vao != activeVao)
                {
                    activeVao = command.Resources.Vao;
                    _gl.BindVertexArray(activeVao);
                }

                Matrix4x4 world = command.World;
                var drawFrame = gameFrame with
                {
                    CharacterPosition = Vector3.Transform(command.Resources.BoundsCenter, world)
                };
                _gl.FrontFace(world.GetDeterminant() < 0f
                    ? FrontFaceDirection.CW
                    : FrontFaceDirection.Ccw);

                if (!wireframePass && command.PassIndex >= 0)
                {
                    if (TryDrawGamePass(command, in drawFrame))
                    {
                        activePalette = null;
                        continue;
                    }
                    // Later passes have no separate stock counterpart.
                    if (command.PassIndex > 0)
                        continue;
                }

                ResetGameBindings();
                if (command.Resources.HasSkin)
                    ConfigureSkinIndexAttribute(command.Resources, integer: false);
                UseStockProgram(viewProjection, gameFrame.Sun);
                _lightGridBindings.Apply(drawFrame.LightGrid, drawFrame.CharacterPosition);
                if (!ReferenceEquals(activePalette, command.Palette))
                {
                    activePalette = command.Palette;
                    UploadPalette(activePalette);
                }
                _gl.UniformMatrix4(_uWorld, 1, false, in world.M11);
                _gl.Uniform1(_uUseSkinning, command.Resources.HasSkin ? 1 : 0);
                if (!wireframePass)
                    ApplyMaterial(command.Range, command.TimeSeconds, untextured, errored, viewMode, command.SelfIllumination);

                if (_gameShaderRuntime != null)
                    _gameShaderRuntime.DrawIndexedPass(
                        _drawElements, command.Range.IndexCount, new IntPtr(checked(command.Range.StartIndex * sizeof(uint))),
                        !wireframePass && command.Range.Transparent && command.Range.Material.RenderState.DoubleSided && viewMode != StudioViewMode.Untextured);
                else
                    _drawElements(
                        (uint)PrimitiveType.Triangles,
                        command.Range.IndexCount,
                        (uint)DrawElementsType.UnsignedInt,
                        new IntPtr(checked(command.Range.StartIndex * sizeof(uint))));
            }
        }

        private Func<string, uint?> CreateProgramTextureLookup(SkinResources resources) =>
            path => ResolveProgramTexture(resources, path);

        private void ResetGameBindings()
        {
            if (!_gameBindingsActive) return;
            _gameShaderRuntime.ResetBindings();
            _gameBindingsActive = false;
        }

        private bool TryDrawGamePass(DrawCommand command, in GameShaderRuntime.Frame drawFrame)
        {
            bool previousBindings = _gameBindingsActive;
            _gameBindingsActive = true;
            ConfigureSkinIndexAttribute(command.Resources, integer: true);
            if (!_gameShaderRuntime.TryBindSkinned(
                    command.Range.Material,
                    command.PassIndex,
                    command.World,
                    command.Palette,
                    command.Resources.TangentVbo != 0,
                    in drawFrame,
                    command.Resources.ProgramTextureLookup ??= CreateProgramTextureLookup(command.Resources),
                    command.SelfIllumination,
                    hasColors: command.Resources.ColorVbo != 0))
            {
                _gameBindingsActive = previousBindings;
                return false;
            }
            _gameShaderRuntime.DrawBoundPass(
                _drawElements,
                command.Range.IndexCount,
                new IntPtr(checked(command.Range.StartIndex * sizeof(uint))));
            return true;
        }

        /// <summary>Redraws the game passes whose shaders write glow, routing it into the shared bloom texture.</summary>
        private void DrawBloomPasses(
            IReadOnlyList<DrawCommand> queue,
            in GameShaderRuntime.Frame gameFrame,
            ref Matrix4x4[] activePalette,
            ref uint activeVao)
        {
            bool begun = false;
            foreach (DrawCommand command in queue)
            {
                if (command.PassIndex < 0 || !_gameShaderRuntime.WritesBloom(command.Range.Material, command.PassIndex))
                    continue;
                if (!begun)
                {
                    if (!Bloom.BeginPasses())
                        return;
                    begun = true;
                }
                if (command.Resources.Vao != activeVao)
                {
                    activeVao = command.Resources.Vao;
                    _gl.BindVertexArray(activeVao);
                }
                _gl.FrontFace(command.World.GetDeterminant() < 0f ? FrontFaceDirection.CW : FrontFaceDirection.Ccw);
                var drawFrame = gameFrame with
                {
                    CharacterPosition = Vector3.Transform(command.Resources.BoundsCenter, command.World)
                };
                TryDrawGamePass(command, in drawFrame);
                activePalette = null;
            }
            if (begun)
                Bloom.EndPasses();
        }

        private static Vector3 MeshBoundsCenter(Vector3[] positions)
        {
            if (positions == null || positions.Length == 0) return Vector3.Zero;
            Vector3 min = positions[0], max = positions[0];
            foreach (Vector3 position in positions)
            {
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
            }
            return (min + max) * 0.5f;
        }

        private void ConfigureSkinIndexAttribute(SkinResources resources, bool integer)
        {
            if (resources?.SkinIndexVbo == 0)
                return;
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, resources.SkinIndexVbo);
            _gl.EnableVertexAttribArray(5);
            if (integer)
            {
                _gl.VertexAttribIPointer(
                    5,
                    4,
                    VertexAttribIType.UnsignedByte,
                    4,
                    IntPtr.Zero);
            }
            else
            {
                _gl.VertexAttribPointer(
                    5,
                    4,
                    VertexAttribPointerType.UnsignedByte,
                    false,
                    4,
                    IntPtr.Zero);
            }
        }

        private void UseStockProgram(Matrix4x4 viewProjection, MapSunData sun)
        {
            _gl.UseProgram(_program);
            _gl.UniformMatrix4(_uViewProjection, 1, false, in viewProjection.M11);
            MapGeometryRenderer.LightState light = MapGeometryRenderer.ResolveLight(sun);
            _gl.Uniform3(_uLightDirection, light.Direction.X, light.Direction.Y, light.Direction.Z);
            _gl.Uniform3(_uSunColor, light.SunColor.X, light.SunColor.Y, light.SunColor.Z);
            _gl.Uniform1(_uSunStrength, light.SunStrength);
            _gl.Uniform3(_uSkyColor, light.SkyColor.X, light.SkyColor.Y, light.SkyColor.Z);
            _gl.Uniform3(_uGroundColor, light.GroundColor.X, light.GroundColor.Y, light.GroundColor.Z);
            _gl.Uniform1(_uAmbientStrength, light.AmbientStrength);
        }

        private uint? ResolveProgramTexture(SkinResources resources, string authoredPath)
        {
            if (resources?.Asset?.Textures == null || string.IsNullOrWhiteSpace(authoredPath))
                return null;
            if (resources.ProgramTexturesByPath.TryGetValue(authoredPath, out uint cached))
                return cached == 0 ? null : cached;

            string key = SknMaterialTextureResolver.MatchTextureKey(authoredPath, resources.TextureKeys);
            if (string.IsNullOrWhiteSpace(key) ||
                !resources.Asset.Textures.TryGetValue(key, out BitmapSource bitmap) ||
                bitmap == null)
            {
                resources.ProgramTexturesByPath[authoredPath] = 0;
                return null;
            }

            uint texture = AcquireTexture(bitmap, _rawProgramTextures, ProgramTextureInternalFormat);
            resources.ProgramTexturesByPath[authoredPath] = texture;
            return texture;
        }

        private SkinResources EnsureResources(MapCharacterAssetData asset)
        {
            if (_skins.TryGetValue(asset, out SkinResources existing))
                return existing;

            MapCharacterMeshData mesh = asset.Mesh;
            if (mesh?.Positions == null || mesh.Positions.Length == 0 || mesh.Indices == null || mesh.Indices.Length == 0)
                return null;

            Vector3[] normals = mesh.Normals != null && mesh.Normals.Length == mesh.VertexCount
                ? mesh.Normals
                : ComputeNormals(mesh.Positions, mesh.Indices);
            Vector2[] uv = mesh.Uv != null && mesh.Uv.Length == mesh.VertexCount
                ? mesh.Uv
                : new Vector2[mesh.VertexCount];

            var resources = new SkinResources
            {
                Asset = asset,
                BoundsCenter = MeshBoundsCenter(asset.Mesh?.Positions),
                Vao = _gl.GenVertexArray(),
                HasSkin = mesh.HasSkin,
                InfluenceJointSlots = BuildInfluenceJointSlots(asset.Skeleton),
                TextureKeys = asset.Textures?.Keys.ToArray() ?? Array.Empty<string>()
            };
            _gl.BindVertexArray(resources.Vao);
            resources.PositionVbo = UploadVector3Attribute(0, mesh.Positions);
            resources.NormalVbo = UploadVector3Attribute(1, normals);
            resources.UvVbo = UploadVector2Attribute(2, uv);
            if (mesh.HasTangents)
                resources.TangentVbo = UploadVector4Attribute(3, mesh.Tangents);
            if (mesh.HasColors)
                resources.ColorVbo = UploadVector4Attribute(4, mesh.Colors);
            if (mesh.HasSkin)
            {
                resources.SkinIndexVbo = UploadByte4Attribute(5, mesh.SkinIndices);
                resources.SkinWeightVbo = UploadFloat4Attribute(6, mesh.SkinWeights);
            }

            resources.Ebo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, resources.Ebo);
            _gl.BufferData(
                BufferTargetARB.ElementArrayBuffer,
                new ReadOnlySpan<uint>(mesh.Indices),
                BufferUsageARB.StaticDraw);
            resources.Ranges = BindRanges(asset);

            _gl.BindVertexArray(0);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            _skins.Add(asset, resources);
            return resources;
        }

        private IReadOnlyList<BoundRange> BindRanges(MapCharacterAssetData asset)
        {
            MapCharacterMeshData mesh = asset.Mesh;
            IEnumerable<MapCharacterMeshRange> ranges = mesh.Ranges != null && mesh.Ranges.Count > 0
                ? mesh.Ranges
                : new[] { new MapCharacterMeshRange(string.Empty, 0, mesh.Indices.Length) };
            var hidden = new HashSet<string>(
                asset.Skin.HiddenSubmeshes ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var result = new List<BoundRange>();

            foreach (MapCharacterMeshRange range in ranges)
            {
                if (range.IndexCount <= 0)
                    continue;

                string rangeName = range.Name ?? string.Empty;
                bool hiddenByDefault = hidden.Contains(rangeName);
                ModelMaterialDefinition material = asset.Materials?.ResolveMaterialDefinition(range.Name) ??
                                                   ModelMaterialDefinition.TextureOnly(null);
                BitmapSource texture = null;
                if (material.BindingKind != ModelMaterialBindingKind.Missing &&
                    !string.IsNullOrWhiteSpace(material.BaseTextureName))
                {
                    asset.Textures?.TryGetValue(material.BaseTextureName, out texture);
                }

                bool transparent = material.DrawsInTransparentQueue;
                bool forceRepeat = material.UvRepeat != Vector2.One;
                result.Add(new BoundRange(
                    range.StartIndex,
                    range.IndexCount,
                    rangeName,
                    Fnv1a.HashLower(rangeName),
                    hiddenByDefault,
                    material,
                    texture,
                    material.IsLit,
                    transparent,
                    forceRepeat ? ModelMaterialWrapMode.Repeat : material.WrapU,
                    forceRepeat ? ModelMaterialWrapMode.Repeat : material.WrapV));
            }

            if (asset.Materials == null)
                return result;

            int[] drawRanks = asset.Materials.DrawRanks(result.Select(range => range.Name).ToArray());
            return result
                .Select((range, at) => (range, rank: drawRanks[at]))
                .OrderBy(entry => entry.rank)
                .Select(entry => entry.range)
                .ToList();
        }

        private void ApplyMaterial(
            BoundRange range,
            float timeSeconds,
            Vector3 untextured,
            Vector3 errored,
            StudioViewMode viewMode,
            float selfIllumination = 0f)
        {
            ModelMaterialDefinition material = range.Material;
            bool forceUntextured = viewMode == StudioViewMode.Untextured;
            bool forceUnshaded = viewMode == StudioViewMode.Unshaded;
            bool hasTexture = !forceUntextured && range.Texture != null;
            uint textureId = hasTexture
                ? AcquireTexture(range.Texture, _textures, BaseTextureInternalFormat)
                : _whiteTexture;
            Vector3 color;
            if (forceUntextured)
                color = untextured;
            else if (material.BindingKind == ModelMaterialBindingKind.Missing)
                color = errored;
            else if (!hasTexture && material.BindingKind == ModelMaterialBindingKind.Authored && !material.HasAuthoredTint)
                color = untextured;
            else if (!hasTexture && material.BindingKind == ModelMaterialBindingKind.TextureOnly)
                color = untextured;
            else
                color = VectorMathUtils.SrgbToLinear(new Vector3(material.Color.X, material.Color.Y, material.Color.Z));

            Vector2 scroll = forceUntextured
                ? Vector2.Zero
                : new Vector2(
                    ScrollAt(material.UvScroll.X, timeSeconds),
                    ScrollAt(material.UvScroll.Y, timeSeconds));
            ModelMaterialRenderState renderState = forceUntextured
                ? ModelMaterialRenderState.Default with { DoubleSided = material.RenderState.DoubleSided }
                : material.RenderState;
            bool transparent = !forceUntextured &&
                renderState.Blending != ModelMaterialBlendMode.Opaque &&
                !renderState.Cutout;

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, textureId);
            _gl.BindSampler(0, ResolveSampler(range.WrapU, range.WrapV));
            _gl.Uniform1(_uHasTexture, hasTexture ? 1 : 0);
            _gl.Uniform3(_uColor, color.X, color.Y, color.Z);
            _gl.Uniform1(_uOpacity, forceUntextured || material.BindingKind == ModelMaterialBindingKind.Missing ? 1f : material.Color.W);
            _gl.Uniform1(_uAlphaTest, forceUntextured || material.BindingKind == ModelMaterialBindingKind.Missing ? 0f : material.AlphaCutoff);
            _gl.Uniform2(_uUvRepeat, forceUntextured ? 1f : material.UvRepeat.X, forceUntextured ? 1f : material.UvRepeat.Y);
            _gl.Uniform2(_uUvOffset, scroll.X, scroll.Y);
            _gl.Uniform1(_uLit, forceUntextured || (!forceUnshaded && range.Lit) ? 1 : 0);
            _gl.Uniform1(_uPremultipliedAlpha, renderState.PremultipliedAlpha ? 1 : 0);
            _gl.Uniform1(_uSelfIllumination, forceUntextured || forceUnshaded ? 0f : selfIllumination);
            ApplyRenderState(renderState, transparent);
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

        private void ApplyRenderState(ModelMaterialRenderState state, bool transparent)
        {
            if (transparent)
            {
                _gl.Enable(EnableCap.Blend);
                _gl.BlendEquation(GLEnum.FuncAdd);
                switch (state.Blending)
                {
                    case ModelMaterialBlendMode.Additive:
                        _gl.BlendFunc(
                            state.PremultipliedAlpha ? BlendingFactor.One : BlendingFactor.SrcAlpha,
                            BlendingFactor.One);
                        break;
                    case ModelMaterialBlendMode.Modulate:
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

            if (state.DepthTest) _gl.Enable(EnableCap.DepthTest);
            else _gl.Disable(EnableCap.DepthTest);
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

        internal static Matrix4x4 CreateWorldMatrix(Matrix4x4 authoredTransform, float skinScale) =>
            MapCharacterSemantics.WorldTransform(authoredTransform, skinScale);

        internal static InternalFormat BaseTextureInternalFormat => InternalFormat.Srgb8Alpha8;
        internal static InternalFormat ProgramTextureInternalFormat => InternalFormat.Rgba8;

        internal static float ScrollAt(float rate, float timeSeconds)
        {
            float turns = rate * timeSeconds;
            if (!float.IsFinite(turns)) return 0f;
            return turns - MathF.Floor(turns);
        }

        internal static int[] BuildInfluenceJointSlots(RigResource skeleton)
        {
            if (skeleton == null || skeleton.Influences == null)
                return Array.Empty<int>();
            var byId = new Dictionary<short, int>();
            for (int index = 0; index < skeleton.Joints.Count; index++)
                byId.TryAdd(skeleton.Joints[index].Id, index);

            int count = Math.Min(skeleton.Influences.Count, MapCharacterShaderSource.MaximumBones);
            var slots = new int[count];
            for (int index = 0; index < count; index++)
                slots[index] = byId.TryGetValue(skeleton.Influences[index], out int slot) ? slot : -1;
            return slots;
        }

        internal static void FillInfluencePalette(
            Matrix4x4[] palette,
            IReadOnlyList<Matrix4x4> jointTransforms,
            IReadOnlyList<int> influenceSlots)
        {
            if (palette == null) return;
            Array.Fill(palette, Matrix4x4.Identity);
            if (jointTransforms == null || influenceSlots == null) return;

            int count = Math.Min(palette.Length, influenceSlots.Count);
            for (int index = 0; index < count; index++)
            {
                int slot = influenceSlots[index];
                if ((uint)slot < (uint)jointTransforms.Count)
                    palette[index] = jointTransforms[slot];
            }
        }

        internal static Vector3[] ComputeNormals(Vector3[] positions, uint[] indices)
        {
            var normals = new Vector3[positions?.Length ?? 0];
            if (positions == null || indices == null) return normals;

            for (int index = 0; index + 2 < indices.Length; index += 3)
            {
                int a = checked((int)indices[index]);
                int b = checked((int)indices[index + 1]);
                int c = checked((int)indices[index + 2]);
                if ((uint)a >= (uint)positions.Length ||
                    (uint)b >= (uint)positions.Length ||
                    (uint)c >= (uint)positions.Length)
                    continue;
                Vector3 normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                if (normal.LengthSquared() <= 1e-12f) continue;
                normals[a] += normal;
                normals[b] += normal;
                normals[c] += normal;
            }

            for (int index = 0; index < normals.Length; index++)
                normals[index] = normals[index].LengthSquared() > 1e-12f
                    ? Vector3.Normalize(normals[index])
                    : Vector3.UnitY;
            return normals;
        }

        private Matrix4x4[] GetPalette(SkinResources resources, string animation)
        {
            string key = animation ?? string.Empty;
            if (resources.Palettes.TryGetValue(key, out Matrix4x4[] palette))
                return palette;
            palette = new Matrix4x4[MapCharacterShaderSource.MaximumBones];
            Array.Fill(palette, Matrix4x4.Identity);
            resources.Palettes[key] = palette;
            return palette;
        }

        private void UploadPalette(Matrix4x4[] palette)
        {
            if (_boneBuffer == 0 || palette == null) return;
            _gl.BindBuffer(BufferTargetARB.UniformBuffer, _boneBuffer);
            _gl.BufferSubData(
                BufferTargetARB.UniformBuffer,
                0,
                new ReadOnlySpan<Matrix4x4>(palette, 0, Math.Min(palette.Length, MapCharacterShaderSource.MaximumBones)));
            _gl.BindBufferBase(BufferTargetARB.UniformBuffer, 0, _boneBuffer);
            _gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);
        }

        private uint UploadVector3Attribute(uint location, Vector3[] values)
        {
            uint buffer = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<Vector3>(values), BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(location);
            _gl.VertexAttribPointer(location, 3, VertexAttribPointerType.Float, false, (uint)Marshal.SizeOf<Vector3>(), IntPtr.Zero);
            return buffer;
        }

        private uint UploadVector2Attribute(uint location, Vector2[] values)
        {
            uint buffer = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<Vector2>(values), BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(location);
            _gl.VertexAttribPointer(location, 2, VertexAttribPointerType.Float, false, (uint)Marshal.SizeOf<Vector2>(), IntPtr.Zero);
            return buffer;
        }

        private uint UploadVector4Attribute(uint location, Vector4[] values)
        {
            uint buffer = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<Vector4>(values), BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(location);
            _gl.VertexAttribPointer(location, 4, VertexAttribPointerType.Float, false, (uint)Marshal.SizeOf<Vector4>(), IntPtr.Zero);
            return buffer;
        }

        private uint UploadByte4Attribute(uint location, byte[] values)
        {
            uint buffer = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<byte>(values), BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(location);
            _gl.VertexAttribPointer(location, 4, VertexAttribPointerType.UnsignedByte, false, 4, IntPtr.Zero);
            return buffer;
        }

        private uint UploadFloat4Attribute(uint location, float[] values)
        {
            uint buffer = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(values), BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(location);
            _gl.VertexAttribPointer(location, 4, VertexAttribPointerType.Float, false, 16, IntPtr.Zero);
            return buffer;
        }

        private uint AcquireTexture(
            BitmapSource source,
            Dictionary<BitmapSource, uint> cache,
            InternalFormat internalFormat)
        {
            if (cache.TryGetValue(source, out uint existing)) return existing;

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
            byte[] pixels = new byte[checked(stride * height)];
            bitmap.CopyPixels(pixels, stride, 0);

            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                internalFormat,
                (uint)width,
                (uint)height,
                0,
                Silk.NET.OpenGL.PixelFormat.Bgra,
                PixelType.UnsignedByte,
                new ReadOnlySpan<byte>(pixels));
            _gl.GenerateMipmap(TextureTarget.Texture2D);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            cache[source] = texture;
            return texture;
        }

        private uint ResolveSampler(ModelMaterialWrapMode u, ModelMaterialWrapMode v)
        {
            var key = (u, v);
            if (_samplers.TryGetValue(key, out uint sampler)) return sampler;
            sampler = _gl.GenSampler();
            _gl.SamplerParameter(sampler, SamplerParameterI.MinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.SamplerParameter(sampler, SamplerParameterI.MagFilter, (int)TextureMagFilter.Linear);
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapS, (int)GlTextureWrap.Of(u));
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapT, (int)GlTextureWrap.Of(v));
            _samplers[key] = sampler;
            return sampler;
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
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            return texture;
        }

        internal void Clear()
        {
            if (!_ready) return;

            foreach (SkinResources resources in _skins.Values)
                ReleaseResources(resources);
            _skins.Clear();
            foreach (uint texture in _textures.Values)
                if (texture != 0) _gl.DeleteTexture(texture);
            _textures.Clear();
            foreach (uint texture in _rawProgramTextures.Values)
                if (texture != 0) _gl.DeleteTexture(texture);
            _rawProgramTextures.Clear();
            _opaque.Clear();
            _transparent.Clear();
            _passSource.Clear();
        }

        /// <summary>
        /// Releases the GPU resources of skins no longer drawn after a map-state switch while keeping
        /// buffers and textures of the skins that remain, so kept structures are not uploaded again.
        /// </summary>
        internal void Retain(IEnumerable<MapCharacterAssetData> liveAssets)
        {
            if (!_ready) return;

            var live = new HashSet<MapCharacterAssetData>(ReferenceEqualityComparer.Instance);
            var liveBitmaps = new HashSet<BitmapSource>(ReferenceEqualityComparer.Instance);
            foreach (MapCharacterAssetData asset in liveAssets ?? Enumerable.Empty<MapCharacterAssetData>())
            {
                if (asset == null || !live.Add(asset) || asset.Textures == null)
                    continue;
                foreach (BitmapSource bitmap in asset.Textures.Values)
                    if (bitmap != null)
                        liveBitmaps.Add(bitmap);
            }

            foreach (MapCharacterAssetData asset in _skins.Keys.Where(asset => !live.Contains(asset)).ToArray())
            {
                ReleaseResources(_skins[asset]);
                _skins.Remove(asset);
            }
            ReleaseTexturesExcept(_textures, liveBitmaps);
            ReleaseTexturesExcept(_rawProgramTextures, liveBitmaps);
            _opaque.Clear();
            _transparent.Clear();
            _passSource.Clear();
        }

        private void ReleaseTexturesExcept(Dictionary<BitmapSource, uint> cache, HashSet<BitmapSource> keep)
        {
            foreach (BitmapSource bitmap in cache.Keys.Where(bitmap => !keep.Contains(bitmap)).ToArray())
            {
                uint texture = cache[bitmap];
                if (texture != 0) _gl.DeleteTexture(texture);
                cache.Remove(bitmap);
            }
        }

        private void ReleaseResources(SkinResources resources)
        {
            DeleteBuffer(resources.PositionVbo);
            DeleteBuffer(resources.NormalVbo);
            DeleteBuffer(resources.UvVbo);
            DeleteBuffer(resources.TangentVbo);
            DeleteBuffer(resources.ColorVbo);
            DeleteBuffer(resources.SkinIndexVbo);
            DeleteBuffer(resources.SkinWeightVbo);
            DeleteBuffer(resources.Ebo);
            if (resources.Vao != 0) _gl.DeleteVertexArray(resources.Vao);
        }

        private void DeleteBuffer(uint buffer)
        {
            if (buffer != 0) _gl.DeleteBuffer(buffer);
        }

        public void Dispose()
        {
            if (!_ready) return;
            try
            {
                foreach (SkinResources resources in _skins.Values)
                    ReleaseResources(resources);
                _skins.Clear();
                foreach (uint texture in _textures.Values)
                    if (texture != 0) _gl.DeleteTexture(texture);
                _textures.Clear();
                foreach (uint texture in _rawProgramTextures.Values)
                    if (texture != 0) _gl.DeleteTexture(texture);
                _rawProgramTextures.Clear();
                _gameShaderRuntime?.Dispose();
                _gameShaderRuntime = null;
                foreach (uint sampler in _samplers.Values)
                    if (sampler != 0) _gl.DeleteSampler(sampler);
                _samplers.Clear();
                if (_boneBuffer != 0) _gl.DeleteBuffer(_boneBuffer);
                if (_whiteTexture != 0) _gl.DeleteTexture(_whiteTexture);
                if (_program != 0) _gl.DeleteProgram(_program);
            }
            catch (Silk.NET.Core.Loader.SymbolLoadingException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _skins.Clear();
                _textures.Clear();
                _rawProgramTextures.Clear();
                _samplers.Clear();
                _gameShaderRuntime = null;
                _boneBuffer = 0;
                _whiteTexture = 0;
                _program = 0;
                _drawElements = null;
                _gl = null;
                _ready = false;
            }
        }
    }
}
