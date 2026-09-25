using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Utils;
using AssetsManager.Utils.Rendering;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// Draws scene mesh parts using the shared OpenGL resource cache.
    /// </summary>
    public sealed class GlMeshRenderer : IDisposable
    {
        private static readonly Vector3 ReferenceCharacterLightDirection =
            Vector3.Normalize(new Vector3(0.25f, 0.75f, -0.05f));
        private static readonly Vector3 ReferenceCharacterLightColor = new(0.4f, 0.4f, 0.4f);
        private static readonly Vector3 ReferenceCharacterAmbientColor = new(0.6f, 0.6f, 0.6f);
        private static readonly Vector3 PreviewWireColor = new(92f / 255f, 133f / 255f, 1f);
        private static readonly Vector3 DefaultUntexturedColor = new(0.5f);

        private readonly AppSettings _appSettings;
        private GL _gl = null!;
        private GlMeshResourceCache _resources = null!;
        private GameShaderRuntime _gameShaderRuntime;
        private uint _program;
        private uint _boneBuffer;
        private readonly List<ModelPart> _alphaRenderQueue = new();
        private readonly Dictionary<SceneModel, long> _materialTimeOrigins = new();
        private readonly Dictionary<SceneModel, Matrix4x4[]> _bindSkinningPalettes = new();
        private int _uViewProj;
        private int _uWorld;
        private int _uUseSkinning;
        private int _uTex;
        private int _uEffectTime;
        private int _uCameraPosition;
        private int _uLightDir;
        private int _uLightColor;
        private int _uLightDir2;
        private int _uLightColor2;
        private int _uAmbient;
        private int _uColorTint;
        private int _uAlphaCutoff;
        private int _uMaterialUvRepeat;
        private int _uMaterialUvScroll;
        private int _uMaterialUnlit;
        private int _uMaterialPremultipliedAlpha;
        private int _uMaterialSrgb;
        private int _uMaterialUsesTextureAlpha;
        private int _uWireframePass;
        private int _uWireframeColor;
        private int _uSelfIllumination;
        private bool _gles;
        private bool _ready;

        public GlMeshRenderer(AppSettings appSettings = null)
        {
            _appSettings = appSettings;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void DrawElementsDelegate(uint mode, int count, uint type, IntPtr indices);

        private DrawElementsDelegate _drawElements = null!;

        public void Initialize(GL gl)
        {
            _gl = gl;
            IntPtr proc = gl.Context.GetProcAddress("glDrawElements");
            if (proc != IntPtr.Zero)
            {
                _drawElements = Marshal.GetDelegateForFunctionPointer<DrawElementsDelegate>(proc);
            }

            _gles = GlShaderCompiler.UsesEmbeddedProfile(gl);
            _program = GlShaderCompiler.CreateProgram(
                gl,
                _gles,
                GlMeshShaderSource.Vertex,
                GlMeshShaderSource.Fragment);
            CacheUniformLocations(gl);
            uint boneBlock = gl.GetUniformBlockIndex(_program, "BoneTransforms");
            if (boneBlock != uint.MaxValue)
                gl.UniformBlockBinding(_program, boneBlock, 0);

            // Initialize static sampler uniform slot assignments once.
            gl.UseProgram(_program);
            gl.Uniform1(_uTex, 0);
            gl.UseProgram(0);

            _resources = new GlMeshResourceCache(gl);
            _gameShaderRuntime = _appSettings != null
                ? new GameShaderRuntime(_gl, _gles, _appSettings)
                : null;
            _boneBuffer = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.UniformBuffer, _boneBuffer);
            gl.BufferData(
                BufferTargetARB.UniformBuffer,
                new ReadOnlySpan<float>(new float[GpuSkinningData.MaxBones * 16]),
                BufferUsageARB.DynamicDraw);
            gl.BindBufferBase(BufferTargetARB.UniformBuffer, 0, _boneBuffer);
            gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);
            _ready = true;
        }

        public void Render(
            SceneModel model,
            Matrix4x4 viewProj,
            Matrix4x4 view,
            Matrix4x4 projection,
            Vector3 cameraPosition,
            Vector3 lightDir,
            Vector3 lightColor,
            Vector3 lightDir2,
            Vector3 lightColor2,
            Vector3 ambientColor,
            VfxPreviewViewMode viewMode = VfxPreviewViewMode.Lit,
            bool wireOverlay = false,
            bool shadersEnabled = false,
            bool mirrorCharacterX = false)
        {
            if (!_ready || model == null || !model.IsVisible) return;

            Matrix4x4 world = CreateWorldMatrix(model, mirrorCharacterX);
            UploadBoneTransforms(model.SkinningMatrices);
            IReadOnlyList<Matrix4x4> gameSkinningMatrices = model.SkinningMatrices;
            if ((gameSkinningMatrices == null || gameSkinningMatrices.Count == 0) &&
                UsesGameShaders(viewMode, shadersEnabled) &&
                model.GpuSkinningData != null)
            {
                gameSkinningMatrices = GetBindSkinningPalette(model);
            }
            long now = Stopwatch.GetTimestamp();
            if (!_materialTimeOrigins.TryGetValue(model, out long materialTimeOrigin))
            {
                materialTimeOrigin = now;
                _materialTimeOrigins[model] = materialTimeOrigin;
            }
            float materialTimeSeconds =
                (float)((now - materialTimeOrigin) / (double)Stopwatch.Frequency);
            var gameFrame = new GameShaderRuntime.Frame(
                view,
                projection,
                cameraPosition,
                materialTimeSeconds,
                null);
            (bool solids, bool wireframe, float wireOpacity) =
                MapGeometryRenderer.ResolveViewPasses(viewMode, wireOverlay, supportsWireframe: !_gles);
            VfxPreviewViewMode solidMode = viewMode == VfxPreviewViewMode.Wireframe
                ? VfxPreviewViewMode.Lit
                : viewMode;

            UseStockProgram(
                viewProj,
                world,
                cameraPosition,
                lightDir,
                lightColor,
                lightDir2,
                lightColor2,
                ambientColor,
                materialTimeSeconds,
                model.SelfIllumination);

            // Per-part state below owns blending, depth and culling. Start and end from
            // conservative defaults so unbound Viewer/Diff parts keep their shared behavior unchanged.
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
            _gl.Disable(EnableCap.CullFace);
            _gl.FrontFace(world.GetDeterminant() < 0f
                ? FrontFaceDirection.CW
                : FrontFaceDirection.Ccw);
            try
            {
                if (solids)
                {
                    _gl.Uniform1(_uWireframePass, 0);
                    RenderParts(model, false, cameraPosition, world, viewProj, in gameFrame,
                        lightDir, lightColor, lightDir2, lightColor2, ambientColor, materialTimeSeconds,
                        solidMode, shadersEnabled, wireframePass: false, gameSkinningMatrices);
                    RenderParts(model, true, cameraPosition, world, viewProj, in gameFrame,
                        lightDir, lightColor, lightDir2, lightColor2, ambientColor, materialTimeSeconds,
                        solidMode, shadersEnabled, wireframePass: false, gameSkinningMatrices);
                }

                if (wireframe)
                {
                    _gameShaderRuntime?.ResetBindings();
                    UseStockProgram(
                        viewProj,
                        world,
                        cameraPosition,
                        lightDir,
                        lightColor,
                        lightDir2,
                        lightColor2,
                        ambientColor,
                        materialTimeSeconds,
                        model.SelfIllumination);
                    _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
                    _gl.Uniform1(_uWireframePass, 1);
                    _gl.Uniform4(
                        _uWireframeColor,
                        PreviewWireColor.X,
                        PreviewWireColor.Y,
                        PreviewWireColor.Z,
                        wireOpacity);
                    ApplyWireframeState(wireOpacity);
                    RenderParts(model, false, cameraPosition, world, viewProj, in gameFrame,
                        lightDir, lightColor, lightDir2, lightColor2, ambientColor, materialTimeSeconds,
                        solidMode, shadersEnabled: false, wireframePass: true, gameSkinningMatrices);
                    RenderParts(model, true, cameraPosition, world, viewProj, in gameFrame,
                        lightDir, lightColor, lightDir2, lightColor2, ambientColor, materialTimeSeconds,
                        solidMode, shadersEnabled: false, wireframePass: true, gameSkinningMatrices);
                }
            }
            finally
            {
                if (!_gles)
                    _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
                _gameShaderRuntime?.ResetBindings();
                _gl.UseProgram(_program);
                _gl.Uniform1(_uWireframePass, 0);
                _gl.FrontFace(FrontFaceDirection.Ccw);
                _gl.Disable(EnableCap.Blend);
                _gl.Disable(EnableCap.CullFace);
                _gl.Enable(EnableCap.DepthTest);
                _gl.DepthMask(true);
                _gl.BindVertexArray(0);
                UnbindSceneTextures();
            }
        }

        private void CacheUniformLocations(GL gl)
        {
            _uViewProj = gl.GetUniformLocation(_program, "uViewProj");
            _uWorld = gl.GetUniformLocation(_program, "uWorld");
            _uUseSkinning = gl.GetUniformLocation(_program, "uUseSkinning");
            _uTex = gl.GetUniformLocation(_program, "uTex");
            _uEffectTime = gl.GetUniformLocation(_program, "uEffectTime");
            _uCameraPosition = gl.GetUniformLocation(_program, "uCameraPosition");
            _uLightDir = gl.GetUniformLocation(_program, "uLightDir");
            _uLightColor = gl.GetUniformLocation(_program, "uLightColor");
            _uLightDir2 = gl.GetUniformLocation(_program, "uLightDir2");
            _uLightColor2 = gl.GetUniformLocation(_program, "uLightColor2");
            _uAmbient = gl.GetUniformLocation(_program, "uAmbient");
            _uColorTint = gl.GetUniformLocation(_program, "uColorTint");
            _uAlphaCutoff = gl.GetUniformLocation(_program, "uAlphaCutoff");
            _uMaterialUvRepeat = gl.GetUniformLocation(_program, "uMaterialUvRepeat");
            _uMaterialUvScroll = gl.GetUniformLocation(_program, "uMaterialUvScroll");
            _uMaterialUnlit = gl.GetUniformLocation(_program, "uMaterialUnlit");
            _uMaterialPremultipliedAlpha = gl.GetUniformLocation(_program, "uMaterialPremultipliedAlpha");
            _uMaterialSrgb = gl.GetUniformLocation(_program, "uMaterialSrgb");
            _uMaterialUsesTextureAlpha = gl.GetUniformLocation(_program, "uMaterialUsesTextureAlpha");
            _uWireframePass = gl.GetUniformLocation(_program, "uWireframePass");
            _uWireframeColor = gl.GetUniformLocation(_program, "uWireframeColor");
            _uSelfIllumination = gl.GetUniformLocation(_program, "uSelfIllumination");
        }

        private void ConfigureSkinIndexAttribute(
            GlMeshResourceCache.PartResources resources,
            bool integer)
        {
            if (resources?.BoneIndexVbo == 0)
                return;
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, resources.BoneIndexVbo);
            _gl.EnableVertexAttribArray(5);
            if (integer)
            {
                _gl.VertexAttribIPointer(
                    5,
                    4,
                    VertexAttribIType.UnsignedShort,
                    4 * sizeof(ushort),
                    IntPtr.Zero);
            }
            else
            {
                _gl.VertexAttribPointer(
                    5,
                    4,
                    VertexAttribPointerType.UnsignedShort,
                    false,
                    4 * sizeof(ushort),
                    IntPtr.Zero);
            }
        }

        private void UseStockProgram(
            Matrix4x4 viewProj,
            Matrix4x4 world,
            Vector3 cameraPosition,
            Vector3 lightDir,
            Vector3 lightColor,
            Vector3 lightDir2,
            Vector3 lightColor2,
            Vector3 ambientColor,
            float materialTimeSeconds,
            float selfIllumination = 0f)
        {
            _gl.UseProgram(_program);
            _gl.BindBufferBase(BufferTargetARB.UniformBuffer, 0, _boneBuffer);
            _gl.UniformMatrix4(_uViewProj, 1, false, in viewProj.M11);
            _gl.UniformMatrix4(_uWorld, 1, false, in world.M11);
            _gl.Uniform3(_uLightDir, NormalizeOrDefault(lightDir));
            _gl.Uniform3(_uLightColor, lightColor);
            _gl.Uniform3(_uLightDir2, NormalizeOrDefault(lightDir2));
            _gl.Uniform3(_uLightColor2, lightColor2);
            _gl.Uniform3(_uAmbient, ambientColor);
            _gl.Uniform3(_uCameraPosition, cameraPosition);
            _gl.Uniform1(_uEffectTime, materialTimeSeconds);
            _gl.Uniform1(_uSelfIllumination, selfIllumination);
        }

        private void RenderParts(
            SceneModel model,
            bool alphaBlended,
            Vector3 cameraPosition,
            Matrix4x4 world,
            Matrix4x4 viewProj,
            in GameShaderRuntime.Frame gameFrame,
            Vector3 lightDir,
            Vector3 lightColor,
            Vector3 lightDir2,
            Vector3 lightColor2,
            Vector3 ambientColor,
            float materialTimeSeconds,
            VfxPreviewViewMode viewMode,
            bool shadersEnabled,
            bool wireframePass,
            IReadOnlyList<Matrix4x4> gameSkinningMatrices)
        {
            IEnumerable<ModelPart> parts = model.Parts;
            if (alphaBlended)
            {
                _alphaRenderQueue.Clear();
                foreach (ModelPart part in model.Parts)
                {
                    if (part.IsVisible && IsPreviewAlphaBlended(part, viewMode, wireframePass))
                        _alphaRenderQueue.Add(part);
                }

                _alphaRenderQueue.Sort((left, right) =>
                    GetRenderDistanceSquared(right, cameraPosition, world)
                        .CompareTo(GetRenderDistanceSquared(left, cameraPosition, world)));
                parts = _alphaRenderQueue;
            }

            uint lastBoundTex0 = uint.MaxValue;

            foreach (ModelPart part in parts)
            {
                if (!part.IsVisible || IsPreviewAlphaBlended(part, viewMode, wireframePass) != alphaBlended)
                    continue;

                GlMeshResourceCache.PartResources resources = _resources.Ensure(model, part);
                if (resources.Vao == 0) continue;

                _gl.BindVertexArray(resources.Vao);
                ModelMaterialDefinition material = part.MaterialDefinition;
                bool wantsGameProgram = UsesGameShaders(viewMode, shadersEnabled, wireframePass) &&
                                        resources.IsGpuSkinned &&
                                        gameSkinningMatrices is { Count: > 0 } &&
                                        material?.Program != null;
                if (wantsGameProgram)
                    ConfigureSkinIndexAttribute(resources, integer: true);

                bool gameBound = wantsGameProgram &&
                                 _gameShaderRuntime?.TryBindSkinned(
                                     material,
                                     world,
                                     gameSkinningMatrices,
                                     hasTangents: resources.TangentVbo != 0,
                                     in gameFrame,
                                     path => _resources.ResolveProgramTexture(part, resources, path), model.SelfIllumination) == true;
                if (gameBound)
                {
                    _gl.FrontFace(world.GetDeterminant() < 0f
                        ? FrontFaceDirection.CW
                        : FrontFaceDirection.Ccw);
                    _drawElements?.Invoke(
                        (uint)PrimitiveType.Triangles,
                        resources.IndexCount,
                        (uint)DrawElementsType.UnsignedInt,
                        IntPtr.Zero);
                    _gameShaderRuntime.ResetBindings();
                    lastBoundTex0 = uint.MaxValue;
                    continue;
                }

                if (resources.IsGpuSkinned)
                    ConfigureSkinIndexAttribute(resources, integer: false);
                UseStockProgram(
                    viewProj,
                    world,
                    cameraPosition,
                    lightDir,
                    lightColor,
                    lightDir2,
                    lightColor2,
                    ambientColor,
                    materialTimeSeconds,
                    model.SelfIllumination);
                _gl.Uniform1(
                    _uUseSkinning,
                    resources.IsGpuSkinned && model.SkinningMatrices != null ? 1 : 0);

                if (!wireframePass)
                {
                    _gl.Uniform1(_uWireframePass, 0);
                    if (viewMode == VfxPreviewViewMode.Untextured)
                    {
                        ApplyUntexturedPartState();
                        _gl.ActiveTexture(TextureUnit.Texture0);
                        _gl.BindTexture(TextureTarget.Texture2D, _resources.WhiteTexture);
                        lastBoundTex0 = _resources.WhiteTexture;
                        _gl.Uniform4(
                            _uColorTint,
                            DefaultUntexturedColor.X,
                            DefaultUntexturedColor.Y,
                            DefaultUntexturedColor.Z,
                            1f);
                        _gl.Uniform1(_uAlphaCutoff, 0f);
                        _gl.Uniform2(_uMaterialUvRepeat, 1f, 1f);
                        _gl.Uniform2(_uMaterialUvScroll, 0f, 0f);
                        _gl.Uniform1(_uMaterialUnlit, 0);
                        _gl.Uniform1(_uMaterialPremultipliedAlpha, 0);
                        _gl.Uniform1(_uMaterialSrgb, 0);
                        _gl.Uniform1(_uMaterialUsesTextureAlpha, 0);
                    }
                    else
                    {
                        ApplyPartRenderState(part, material);
                        uint targetTex0 = resources.Texture != 0 ? resources.Texture : _resources.WhiteTexture;
                        if (targetTex0 != lastBoundTex0)
                        {
                            _gl.ActiveTexture(TextureUnit.Texture0);
                            _gl.BindTexture(TextureTarget.Texture2D, targetTex0);
                            lastBoundTex0 = targetTex0;
                        }
                        if (resources.Texture != 0)
                        {
                            if (material != null)
                                ApplyBaseTextureWrap(material);
                            else
                                ApplyUnboundTextureWrap(part);
                        }

                        // Authored SKN color and runtime Viewer/Diff tint are separate concerns and combine multiplicatively.
                        Vector4 colorTint = material != null
                            ? material.Color * part.ColorTint
                            : part.ColorTint;
                        float alphaCutoff = material?.AlphaCutoff ??
                            (part.IsAlphaBlended ? 0f : part.AlphaCutoff);
                        Vector2 uvRepeat = material?.UvRepeat ?? Vector2.One;
                        Vector2 uvScroll = material?.UvScroll ?? Vector2.Zero;

                        _gl.Uniform4(_uColorTint, colorTint.X, colorTint.Y, colorTint.Z, colorTint.W);
                        _gl.Uniform1(_uAlphaCutoff, alphaCutoff);
                        _gl.Uniform2(_uMaterialUvRepeat, uvRepeat.X, uvRepeat.Y);
                        _gl.Uniform2(_uMaterialUvScroll, uvScroll.X, uvScroll.Y);
                        _gl.Uniform1(
                            _uMaterialUnlit,
                            viewMode == VfxPreviewViewMode.Unshaded || part.UsesUnlitShading ? 1 : 0);
                        _gl.Uniform1(
                            _uMaterialPremultipliedAlpha,
                            material?.RenderState.PremultipliedAlpha == true ? 1 : 0);
                        _gl.Uniform1(_uMaterialSrgb, part.UsesSrgbBaseTexture ? 1 : 0);
                        bool usesTextureAlpha = material?.UsesTextureAlpha ??
                            (part.UseBaseTextureAlpha || part.AlphaCutoff > 0f);
                        _gl.Uniform1(_uMaterialUsesTextureAlpha, usesTextureAlpha ? 1 : 0);
                    }
                }

                _drawElements?.Invoke(
                    (uint)PrimitiveType.Triangles,
                    resources.IndexCount,
                    (uint)DrawElementsType.UnsignedInt,
                    IntPtr.Zero);
            }

            _gl.ActiveTexture(TextureUnit.Texture0);
        }

        internal static bool UsesGameShaders(
            VfxPreviewViewMode viewMode,
            bool shadersEnabled,
            bool wireframePass = false) =>
            !wireframePass && shadersEnabled && viewMode == VfxPreviewViewMode.Lit;

        private static bool IsPreviewAlphaBlended(
            ModelPart part,
            VfxPreviewViewMode viewMode,
            bool wireframePass) =>
            !wireframePass && viewMode != VfxPreviewViewMode.Untextured && part.IsAlphaBlended;

        private void ApplyUntexturedPartState()
        {
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Lequal);
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
            // The reference untextured binding is the unbound stock material: opaque and double-sided.
            _gl.Disable(EnableCap.CullFace);
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
                _gl.BlendEquation(GLEnum.FuncAdd);
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            }
            else
            {
                _gl.Disable(EnableCap.Blend);
            }
        }

        private void ApplyPartRenderState(ModelPart part, ModelMaterialDefinition material)
        {
            if (material == null)
            {
                ApplyUnboundPartRenderState(part);
                return;
            }

            ModelMaterialRenderState state = material.RenderState;
            bool runtimeForcesBlend =
                state.Blending == ModelMaterialBlendMode.Opaque &&
                part.ColorTint.W < 0.999f;
            ModelMaterialBlendMode blending = runtimeForcesBlend
                ? ModelMaterialBlendMode.Normal
                : state.Cutout
                    ? ModelMaterialBlendMode.Opaque
                    : state.Blending;

            if (blending == ModelMaterialBlendMode.Opaque)
            {
                _gl.Disable(EnableCap.Blend);
            }
            else
            {
                _gl.Enable(EnableCap.Blend);
                _gl.BlendEquation(GLEnum.FuncAdd);
                var factors = MaterialBlendFactors(blending, state.PremultipliedAlpha);
                _gl.BlendFuncSeparate(
                    factors.SourceRgb,
                    factors.DestinationRgb,
                    factors.SourceAlpha,
                    factors.DestinationAlpha);
            }

            // Three.js stock character materials use LessEqualDepth unless a material overrides it.
            _gl.DepthFunc(DepthFunction.Lequal);
            if (state.DepthTest)
                _gl.Enable(EnableCap.DepthTest);
            else
                _gl.Disable(EnableCap.DepthTest);

            // Runtime opacity may promote an opaque material
            // to the transparent pass; in that case depth writes must stay disabled for sorting.
            _gl.DepthMask(runtimeForcesBlend ? false : state.DepthWrite);

            if (state.DoubleSided)
            {
                _gl.Disable(EnableCap.CullFace);
            }
            else
            {
                _gl.Enable(EnableCap.CullFace);
                // Front-facing authored geometry stays visible by default. An inverted pass
                // deliberately flips that contract and keeps the back-facing side instead.
                _gl.CullFace(MaterialCullFace(state));
            }
        }

        internal static TriangleFace MaterialCullFace(ModelMaterialRenderState state)
            => state.Inverted ? TriangleFace.Front : TriangleFace.Back;

        internal static (
            Vector3 LightDirection,
            Vector3 LightColor,
            Vector3 FillDirection,
            Vector3 FillColor,
            Vector3 AmbientColor) ReferenceCharacterLighting() => (
                ReferenceCharacterLightDirection,
                ReferenceCharacterLightColor,
                Vector3.UnitY,
                Vector3.Zero,
                ReferenceCharacterAmbientColor);

        internal static (
            Vector3 LightDirection,
            Vector3 LightColor,
            Vector3 FillDirection,
            Vector3 FillColor,
            Vector3 AmbientColor) StudioCharacterLighting(
                double ambientPercent,
                double rotationDegrees,
                double heightDegrees)
        {
            float ambient = (float)Math.Clamp(ambientPercent / 100.0, 0.0, 1.0);
            float phi = (float)(rotationDegrees * Math.PI / 180.0);
            float theta = (float)(heightDegrees * Math.PI / 180.0);
            var lightDirection = Vector3.Normalize(new Vector3(
                MathF.Cos(theta) * MathF.Sin(phi),
                MathF.Sin(theta),
                MathF.Cos(theta) * MathF.Cos(phi)));
            float key = 1f - ambient;

            return (
                lightDirection,
                new Vector3(key),
                Vector3.UnitY,
                Vector3.Zero,
                new Vector3(ambient));
        }

        internal static (
            BlendingFactor SourceRgb,
            BlendingFactor DestinationRgb,
            BlendingFactor SourceAlpha,
            BlendingFactor DestinationAlpha) MaterialBlendFactors(
                ModelMaterialBlendMode blending,
                bool premultipliedAlpha)
        {
            return blending switch
            {
                ModelMaterialBlendMode.Normal => (
                    premultipliedAlpha ? BlendingFactor.One : BlendingFactor.SrcAlpha,
                    BlendingFactor.OneMinusSrcAlpha,
                    BlendingFactor.One,
                    BlendingFactor.OneMinusSrcAlpha),
                ModelMaterialBlendMode.Additive => (
                    premultipliedAlpha ? BlendingFactor.One : BlendingFactor.SrcAlpha,
                    BlendingFactor.One,
                    BlendingFactor.One,
                    BlendingFactor.One),
                ModelMaterialBlendMode.Modulate => (
                    BlendingFactor.OneMinusSrcColor,
                    BlendingFactor.Zero,
                    BlendingFactor.OneMinusSrcColor,
                    BlendingFactor.Zero),
                _ => (
                    BlendingFactor.One,
                    BlendingFactor.Zero,
                    BlendingFactor.One,
                    BlendingFactor.Zero)
            };
        }

        private void ApplyUnboundPartRenderState(ModelPart part)
        {
            _gl.Enable(EnableCap.DepthTest);
            if (part.IsDoubleSided)
            {
                _gl.Disable(EnableCap.CullFace);
            }
            else
            {
                _gl.Enable(EnableCap.CullFace);
                _gl.CullFace(TriangleFace.Back);
            }

            if (part.IsAlphaBlended)
            {
                _gl.Enable(EnableCap.Blend);
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                _gl.DepthMask(false);
            }
            else
            {
                _gl.Disable(EnableCap.Blend);
                _gl.DepthMask(true);
            }
        }

        private void ApplyUnboundTextureWrap(ModelPart part)
        {
            TextureWrapMode wrap = part.IsTextureTiled
                ? TextureWrapMode.Repeat
                : TextureWrapMode.ClampToEdge;
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrap);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrap);
        }

        private void ApplyBaseTextureWrap(ModelMaterialDefinition material)
        {
            if (material == null)
                return;

            // LTK forces repeat while an authored UV scale is active; otherwise the sampler's
            // own addressU/addressV modes are preserved. OpenGL has no equivalent border mode here.
            bool forceRepeat = material.UvRepeat != Vector2.One;
            ModelMaterialWrapMode wrapU = forceRepeat ? ModelMaterialWrapMode.Repeat : material.WrapU;
            ModelMaterialWrapMode wrapV = forceRepeat ? ModelMaterialWrapMode.Repeat : material.WrapV;
            _gl.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapS,
                (int)ToTextureWrapMode(wrapU));
            _gl.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapT,
                (int)ToTextureWrapMode(wrapV));
        }

        private static TextureWrapMode ToTextureWrapMode(ModelMaterialWrapMode wrap) =>
            wrap switch
            {
                ModelMaterialWrapMode.Clamp => TextureWrapMode.ClampToEdge,
                ModelMaterialWrapMode.Mirror => TextureWrapMode.MirroredRepeat,
                ModelMaterialWrapMode.Border => TextureWrapMode.ClampToEdge,
                _ => TextureWrapMode.Repeat
            };

        private void UnbindSceneTextures()
        {
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
        }

        private void UploadBoneTransforms(Matrix4x4[] boneTransforms)
        {
            if (_boneBuffer == 0 || boneTransforms == null || boneTransforms.Length == 0)
                return;

            int boneCount = Math.Min(boneTransforms.Length, GpuSkinningData.MaxBones);
            _gl.BindBuffer(BufferTargetARB.UniformBuffer, _boneBuffer);
            _gl.BufferSubData(
                BufferTargetARB.UniformBuffer,
                0,
                new ReadOnlySpan<Matrix4x4>(boneTransforms, 0, boneCount));
            _gl.BindBufferBase(BufferTargetARB.UniformBuffer, 0, _boneBuffer);
            _gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);
        }

        private Matrix4x4[] GetBindSkinningPalette(SceneModel model)
        {
            int jointCount = model?.Skeleton?.Joints?.Count ?? 0;
            if (jointCount == 0)
                return Array.Empty<Matrix4x4>();

            if (_bindSkinningPalettes.TryGetValue(model, out Matrix4x4[] cached) &&
                cached?.Length == jointCount)
            {
                return cached;
            }

            Matrix4x4[] palette = AnimationService.CreateBindSkinningMatrices(model.Skeleton);
            _bindSkinningPalettes[model] = palette;
            return palette;
        }

        internal static Matrix4x4 CreateWorldMatrix(SceneModel model, bool mirrorCharacterX = false)
        {
            float pitch = (float)(model.RotationX * (Math.PI / 180.0));
            float yaw = (float)(model.RotationY * (Math.PI / 180.0));
            float roll = (float)(model.RotationZ * (Math.PI / 180.0));
            float scale = (float)model.Scale;
            float scaleX = mirrorCharacterX ? -scale : scale;
            return Matrix4x4.CreateScale(scaleX, scale, scale) *
                   Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll) *
                   Matrix4x4.CreateTranslation(
                       (float)model.PositionX,
                       (float)model.PositionY,
                       (float)model.PositionZ);
        }

        private static float GetRenderDistanceSquared(
            ModelPart part,
            Vector3 cameraPosition,
            Matrix4x4 world)
        {
            if (part.Geometry == null) return float.PositiveInfinity;

            var bounds = part.Geometry.Bounds;
            if (bounds.IsEmpty) return float.PositiveInfinity;

            Vector3 center = new(
                (float)(bounds.X + bounds.SizeX * 0.5),
                (float)(bounds.Y + bounds.SizeY * 0.5),
                (float)(bounds.Z + bounds.SizeZ * 0.5));
            return Vector3.DistanceSquared(Vector3.Transform(center, world), cameraPosition);
        }

        private static Vector3 NormalizeOrDefault(Vector3 value) =>
            value.LengthSquared() > 1e-6f ? Vector3.Normalize(value) : Vector3.UnitY;

        public void QueueRelease(SceneModel model)
        {
            if (model != null)
            {
                _materialTimeOrigins.Remove(model);
                _bindSkinningPalettes.Remove(model);
            }
            _resources?.QueueRelease(model);
        }

        public void ProcessPendingReleases()
        {
            if (_ready)
                _resources.ProcessPendingReleases();
        }

        public void Dispose()
        {
            if (!_ready) return;

            try
            {
                _gameShaderRuntime?.Dispose();
                _gameShaderRuntime = null;
                _resources?.Dispose();
                _materialTimeOrigins.Clear();
                _bindSkinningPalettes.Clear();
                if (_boneBuffer != 0)
                    _gl?.DeleteBuffer(_boneBuffer);
                _boneBuffer = 0;
                if (_program != 0)
                    _gl?.DeleteProgram(_program);
            }
            catch (Silk.NET.Core.Loader.SymbolLoadingException)
            {
                // The OpenGL context owns these handles and reclaims them on teardown.
            }
            catch (Exception)
            {
            }
            finally
            {
                _gameShaderRuntime = null;
                _boneBuffer = 0;
                _program = 0;
                _ready = false;
            }
        }
    }
}
