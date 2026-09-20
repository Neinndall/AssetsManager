using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// Draws scene mesh parts using the shared OpenGL resource cache.
    /// </summary>
    public sealed class GlMeshRenderer : IDisposable
    {
        private const float DefaultLightmapEmissionScale = 0.1f;
        private static readonly Vector3 ReferenceCharacterLightDirection =
            Vector3.Normalize(new Vector3(0.25f, 0.75f, -0.05f));
        private static readonly Vector3 ReferenceCharacterLightColor = new(0.4f, 0.4f, 0.4f);
        private static readonly Vector3 ReferenceCharacterAmbientColor = new(0.6f, 0.6f, 0.6f);

        private GL _gl = null!;
        private GlMeshResourceCache _resources = null!;
        private uint _program;
        private uint _boneBuffer;
        private readonly List<ModelPart> _alphaRenderQueue = new();
        private readonly Dictionary<(ModelMaterialWrapMode U, ModelMaterialWrapMode V), uint> _auxiliarySamplers = new();
        private readonly Dictionary<SceneModel, long> _materialTimeOrigins = new();
        private int _uViewProj;
        private int _uWorld;
        private int _uUseSkinning;
        private int _maxAuxiliaryTextures = GlMeshShaderSource.PortableAuxiliaryTextureCount;
        private int _uTex;
        private int[] _uAuxTex = Array.Empty<int>();
        private int _uEffectKind;
        private int _uEffectTime;
        private int _uCameraPosition;
        private int _uAdditiveTexIndex;
        private int _uAdditiveMaskIndex;
        private int _uAdditiveScrollSpeed;
        private int _uAdditiveTiling;
        private int _uAdditiveColor;
        private int _uAdditiveStrength;
        private int _uAdditiveTextureChannel;
        private int _uAdditiveMaskChannel;
        private int _uFlowTexIndex;
        private int _uFlowMaskIndex;
        private int _uFlowScrollSpeed;
        private int _uFlowTiling;
        private int _uFlowStrength;
        private int _uFlowIntensity;
        private int _uFlowMaskChannel;
        private int _uGradientTexIndex;
        private int _uGradientMaskIndex;
        private int _uGradientScrollSpeed;
        private int _uGradientTiling;
        private int _uGradientColor;
        private int _uGradientStrength;
        private int _uPulseRate;
        private int _uPulseMax;
        private int _uPulseOffset;
        private int _uGradientSharpness;
        private int _uGradientBloomIntensity;
        private int _uGradientMaskThreshold;
        private int _uGradientMaskSoftness;
        private int _uGradientTextureChannel;
        private int _uGradientMaskChannel;
        private int _uDissolvePatternIndex;
        private int _uDissolveStateIndex;
        private int _uDissolveMaskIndex;
        private int _uDissolveScrollSpeed;
        private int _uDissolveTiling;
        private int _uDissolveThreshold;
        private int _uDissolveSoftness;
        private int _uDissolvePatternChannel;
        private int _uDissolveMaskChannel;
        private int _uFresnelMaskIndex;
        private int _uFresnelNoiseIndex;
        private int _uFresnelColor;
        private int _uFresnelPower;
        private int _uFresnelStrength;
        private int _uFresnelNoiseTiling;
        private int _uFresnelNoiseSpeed;
        private int _uFresnelMaskChannel;
        private int _uFresnelNoiseChannel;
        private int _uBloomMaskIndex;
        private int _uBloomColor;
        private int _uBloomIntensity;
        private int _uBloomMaskChannel;
        private int _uEmissionTexIndex;
        private int _uEmissionMaskIndex;
        private int _uEmissionScrollSpeed;
        private int _uEmissionTiling;
        private int _uEmissionColor;
        private int _uEmissionStrength;
        private int _uEmissionChannel;
        private int _uEmissionMaskChannel;
        private int _uDistortionTexIndex;
        private int _uDistortionMaskIndex;
        private int _uDistortionScrollSpeed;
        private int _uDistortionTiling;
        private int _uDistortionStrength;
        private int _uDistortionChannelX;
        private int _uDistortionChannelY;
        private int _uDistortionMaskChannel;
        private int _uIridescenceTexIndex;
        private int _uIridescenceMaskIndex;
        private int _uIridescenceControl;
        private int _uIridescencePulseSpeedMin;
        private int _uIridescenceAlphaMinMax;
        private int _uIridescenceDiffuseFadeMask;
        private int _uIridescenceMaskChannel;
        private int _uWaveDirection;
        private int _uWaveSpeed;
        private int _uWaveFrequency;
        private int _uWaveIntensity;
        private int _uDeformNoiseIndex;
        private int _uDeformMaskIndex;
        private int _uDeformDirection;
        private int _uDeformScrollSpeed;
        private int _uDeformTiling;
        private int _uDeformSpeed;
        private int _uDeformFrequency;
        private int _uDeformIntensity;
        private int _uDeformProtection;
        private int _uDeformNoiseChannel;
        private int _uDeformMaskChannel;
        private int _uLightDir;
        private int _uLightColor;
        private int _uLightDir2;
        private int _uLightColor2;
        private int _uAmbient;
        private int _uLightmap;
        private int _uHasLightmap;
        private int _uLightMapColorScale;
        private int _uColorTint;
        private int _uAlphaCutoff;
        private int _uMaterialUvRepeat;
        private int _uMaterialUvScroll;
        private int _uMaterialUnlit;
        private int _uMaterialPremultipliedAlpha;
        private int _uMaterialSrgb;
        private int _uMaterialUsesTextureAlpha;
        private int _uUsesBakedDiffuse;
        private int _uHasVertexColor;
        private bool _ready;

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

            _maxAuxiliaryTextures = ResolveAuxiliaryTextureCapacity(gl);
            _uAuxTex = new int[_maxAuxiliaryTextures];

            bool gles = GlShaderCompiler.UsesEmbeddedProfile(gl);
            _program = GlShaderCompiler.CreateProgram(
                gl,
                gles,
                GlMeshShaderSource.CreateVertex(_maxAuxiliaryTextures),
                GlMeshShaderSource.CreateFragment(_maxAuxiliaryTextures));
            CacheUniformLocations(gl);
            uint boneBlock = gl.GetUniformBlockIndex(_program, "BoneTransforms");
            if (boneBlock != uint.MaxValue)
                gl.UniformBlockBinding(_program, boneBlock, 0);

            // Initialize static sampler uniform slot assignments once
            gl.UseProgram(_program);
            gl.Uniform1(_uTex, 0);
            gl.Uniform1(_uLightmap, 1);
            for (int i = 0; i < _maxAuxiliaryTextures; i++)
                gl.Uniform1(_uAuxTex[i], i + 2);
            gl.UseProgram(0);

            _resources = new GlMeshResourceCache(gl);
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

        private static int ResolveAuxiliaryTextureCapacity(GL gl)
        {
            gl.GetInteger(GLEnum.MaxTextureImageUnits, out int fragmentTextureUnits);
            gl.GetInteger(GLEnum.MaxVertexTextureImageUnits, out int vertexTextureUnits);
            gl.GetInteger(GLEnum.MaxCombinedTextureImageUnits, out int combinedTextureUnits);
            return CalculateAuxiliaryTextureCapacity(
                fragmentTextureUnits,
                vertexTextureUnits,
                combinedTextureUnits);
        }

        internal static int CalculateAuxiliaryTextureCapacity(
            int fragmentTextureUnits,
            int vertexTextureUnits,
            int combinedTextureUnits)
        {
            int fragmentCapacity = Math.Max(0, fragmentTextureUnits - 2);
            int vertexCapacity = Math.Max(0, vertexTextureUnits);
            int combinedCapacity = Math.Max(0, (combinedTextureUnits - 2) / 2);
            int available = Math.Min(
                fragmentCapacity,
                Math.Min(vertexCapacity, combinedCapacity));
            return Math.Clamp(
                available,
                1,
                GlMeshShaderSource.MaximumAuxiliaryTextureCount);
        }

        public void Render(
            SceneModel model,
            Matrix4x4 viewProj,
            Vector3 cameraPosition,
            Vector3 lightDir,
            Vector3 lightColor,
            Vector3 lightDir2,
            Vector3 lightColor2,
            Vector3 ambientColor)
        {
            if (!_ready || model == null || !model.IsVisible) return;

            float lightmapScale = DefaultLightmapEmissionScale;
            if (model.MapLightingProfile is MapLightingProfile mapLighting)
            {
                lightDir = mapLighting.SunDirection;
                lightColor = mapLighting.SunColor;
                lightDir2 = Vector3.UnitY;
                lightColor2 = Vector3.Zero;
                ambientColor = mapLighting.AmbientColor;
                lightmapScale *= mapLighting.LightMapColorScale;
            }

            _gl.UseProgram(_program);
            _gl.UniformMatrix4(_uViewProj, 1, false, in viewProj.M11);
            Matrix4x4 world = CreateWorldMatrix(model);
            _gl.UniformMatrix4(_uWorld, 1, false, in world.M11);
            UploadBoneTransforms(model.SkinningMatrices);
            _gl.Uniform3(_uLightDir, NormalizeOrDefault(lightDir));
            _gl.Uniform3(_uLightColor, lightColor);
            _gl.Uniform3(_uLightDir2, NormalizeOrDefault(lightDir2));
            _gl.Uniform3(_uLightColor2, lightColor2);
            _gl.Uniform3(_uAmbient, ambientColor);
            _gl.Uniform3(_uCameraPosition, cameraPosition);
            long now = Stopwatch.GetTimestamp();
            if (!_materialTimeOrigins.TryGetValue(model, out long materialTimeOrigin))
            {
                materialTimeOrigin = now;
                _materialTimeOrigins[model] = materialTimeOrigin;
            }
            _gl.Uniform1(
                _uEffectTime,
                (float)((now - materialTimeOrigin) / (double)Stopwatch.Frequency));
            _gl.Uniform1(_uLightMapColorScale, lightmapScale);

            // Per-part state below owns blending, depth and culling. Start and end from
            // conservative defaults so unbound Map/Diff parts keep their shared behavior unchanged.
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
            _gl.Disable(EnableCap.CullFace);
            RenderParts(model, false, false, cameraPosition, world);
            RenderParts(model, true, false, cameraPosition, world);
            RenderParts(model, false, true, cameraPosition, world);
            RenderParts(model, true, true, cameraPosition, world);

            _gl.Disable(EnableCap.PolygonOffsetFill);
            _gl.Disable(EnableCap.Blend);
            _gl.Disable(EnableCap.CullFace);
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthMask(true);
            _gl.BindVertexArray(0);
            UnbindSceneTextures();
        }

        private void CacheUniformLocations(GL gl)
        {
            _uViewProj = gl.GetUniformLocation(_program, "uViewProj");
            _uWorld = gl.GetUniformLocation(_program, "uWorld");
            _uUseSkinning = gl.GetUniformLocation(_program, "uUseSkinning");
            _uTex = gl.GetUniformLocation(_program, "uTex");
            for (int i = 0; i < _maxAuxiliaryTextures; i++)
                _uAuxTex[i] = gl.GetUniformLocation(_program, $"uAuxTex{i}");
            _uEffectKind = gl.GetUniformLocation(_program, "uEffectKind");
            _uEffectTime = gl.GetUniformLocation(_program, "uEffectTime");
            _uCameraPosition = gl.GetUniformLocation(_program, "uCameraPosition");
            _uAdditiveTexIndex = gl.GetUniformLocation(_program, "uAdditiveTexIndex");
            _uAdditiveMaskIndex = gl.GetUniformLocation(_program, "uAdditiveMaskIndex");
            _uAdditiveScrollSpeed = gl.GetUniformLocation(_program, "uAdditiveScrollSpeed");
            _uAdditiveTiling = gl.GetUniformLocation(_program, "uAdditiveTiling");
            _uAdditiveColor = gl.GetUniformLocation(_program, "uAdditiveColor");
            _uAdditiveStrength = gl.GetUniformLocation(_program, "uAdditiveStrength");
            _uAdditiveTextureChannel = gl.GetUniformLocation(_program, "uAdditiveTextureChannel");
            _uAdditiveMaskChannel = gl.GetUniformLocation(_program, "uAdditiveMaskChannel");
            _uFlowTexIndex = gl.GetUniformLocation(_program, "uFlowTexIndex");
            _uFlowMaskIndex = gl.GetUniformLocation(_program, "uFlowMaskIndex");
            _uFlowScrollSpeed = gl.GetUniformLocation(_program, "uFlowScrollSpeed");
            _uFlowTiling = gl.GetUniformLocation(_program, "uFlowTiling");
            _uFlowStrength = gl.GetUniformLocation(_program, "uFlowStrength");
            _uFlowIntensity = gl.GetUniformLocation(_program, "uFlowIntensity");
            _uFlowMaskChannel = gl.GetUniformLocation(_program, "uFlowMaskChannel");
            _uGradientTexIndex = gl.GetUniformLocation(_program, "uGradientTexIndex");
            _uGradientMaskIndex = gl.GetUniformLocation(_program, "uGradientMaskIndex");
            _uGradientScrollSpeed = gl.GetUniformLocation(_program, "uGradientScrollSpeed");
            _uGradientTiling = gl.GetUniformLocation(_program, "uGradientTiling");
            _uGradientColor = gl.GetUniformLocation(_program, "uGradientColor");
            _uGradientStrength = gl.GetUniformLocation(_program, "uGradientStrength");
            _uPulseRate = gl.GetUniformLocation(_program, "uPulseRate");
            _uPulseMax = gl.GetUniformLocation(_program, "uPulseMax");
            _uPulseOffset = gl.GetUniformLocation(_program, "uPulseOffset");
            _uGradientSharpness = gl.GetUniformLocation(_program, "uGradientSharpness");
            _uGradientBloomIntensity = gl.GetUniformLocation(_program, "uGradientBloomIntensity");
            _uGradientMaskThreshold = gl.GetUniformLocation(_program, "uGradientMaskThreshold");
            _uGradientMaskSoftness = gl.GetUniformLocation(_program, "uGradientMaskSoftness");
            _uGradientTextureChannel = gl.GetUniformLocation(_program, "uGradientTextureChannel");
            _uGradientMaskChannel = gl.GetUniformLocation(_program, "uGradientMaskChannel");
            _uDissolvePatternIndex = gl.GetUniformLocation(_program, "uDissolvePatternIndex");
            _uDissolveStateIndex = gl.GetUniformLocation(_program, "uDissolveStateIndex");
            _uDissolveMaskIndex = gl.GetUniformLocation(_program, "uDissolveMaskIndex");
            _uDissolveScrollSpeed = gl.GetUniformLocation(_program, "uDissolveScrollSpeed");
            _uDissolveTiling = gl.GetUniformLocation(_program, "uDissolveTiling");
            _uDissolveThreshold = gl.GetUniformLocation(_program, "uDissolveThreshold");
            _uDissolveSoftness = gl.GetUniformLocation(_program, "uDissolveSoftness");
            _uDissolvePatternChannel = gl.GetUniformLocation(_program, "uDissolvePatternChannel");
            _uDissolveMaskChannel = gl.GetUniformLocation(_program, "uDissolveMaskChannel");
            _uFresnelMaskIndex = gl.GetUniformLocation(_program, "uFresnelMaskIndex");
            _uFresnelNoiseIndex = gl.GetUniformLocation(_program, "uFresnelNoiseIndex");
            _uFresnelColor = gl.GetUniformLocation(_program, "uFresnelColor");
            _uFresnelPower = gl.GetUniformLocation(_program, "uFresnelPower");
            _uFresnelStrength = gl.GetUniformLocation(_program, "uFresnelStrength");
            _uFresnelNoiseTiling = gl.GetUniformLocation(_program, "uFresnelNoiseTiling");
            _uFresnelNoiseSpeed = gl.GetUniformLocation(_program, "uFresnelNoiseSpeed");
            _uFresnelMaskChannel = gl.GetUniformLocation(_program, "uFresnelMaskChannel");
            _uFresnelNoiseChannel = gl.GetUniformLocation(_program, "uFresnelNoiseChannel");
            _uBloomMaskIndex = gl.GetUniformLocation(_program, "uBloomMaskIndex");
            _uBloomColor = gl.GetUniformLocation(_program, "uBloomColor");
            _uBloomIntensity = gl.GetUniformLocation(_program, "uBloomIntensity");
            _uBloomMaskChannel = gl.GetUniformLocation(_program, "uBloomMaskChannel");
            _uEmissionTexIndex = gl.GetUniformLocation(_program, "uEmissionTexIndex");
            _uEmissionMaskIndex = gl.GetUniformLocation(_program, "uEmissionMaskIndex");
            _uEmissionScrollSpeed = gl.GetUniformLocation(_program, "uEmissionScrollSpeed");
            _uEmissionTiling = gl.GetUniformLocation(_program, "uEmissionTiling");
            _uEmissionColor = gl.GetUniformLocation(_program, "uEmissionColor");
            _uEmissionStrength = gl.GetUniformLocation(_program, "uEmissionStrength");
            _uEmissionChannel = gl.GetUniformLocation(_program, "uEmissionChannel");
            _uEmissionMaskChannel = gl.GetUniformLocation(_program, "uEmissionMaskChannel");
            _uDistortionTexIndex = gl.GetUniformLocation(_program, "uDistortionTexIndex");
            _uDistortionMaskIndex = gl.GetUniformLocation(_program, "uDistortionMaskIndex");
            _uDistortionScrollSpeed = gl.GetUniformLocation(_program, "uDistortionScrollSpeed");
            _uDistortionTiling = gl.GetUniformLocation(_program, "uDistortionTiling");
            _uDistortionStrength = gl.GetUniformLocation(_program, "uDistortionStrength");
            _uDistortionChannelX = gl.GetUniformLocation(_program, "uDistortionChannelX");
            _uDistortionChannelY = gl.GetUniformLocation(_program, "uDistortionChannelY");
            _uDistortionMaskChannel = gl.GetUniformLocation(_program, "uDistortionMaskChannel");
            _uIridescenceTexIndex = gl.GetUniformLocation(_program, "uIridescenceTexIndex");
            _uIridescenceMaskIndex = gl.GetUniformLocation(_program, "uIridescenceMaskIndex");
            _uIridescenceControl = gl.GetUniformLocation(_program, "uIridescenceControl");
            _uIridescencePulseSpeedMin = gl.GetUniformLocation(_program, "uIridescencePulseSpeedMin");
            _uIridescenceAlphaMinMax = gl.GetUniformLocation(_program, "uIridescenceAlphaMinMax");
            _uIridescenceDiffuseFadeMask = gl.GetUniformLocation(_program, "uIridescenceDiffuseFadeMask");
            _uIridescenceMaskChannel = gl.GetUniformLocation(_program, "uIridescenceMaskChannel");
            _uWaveDirection = gl.GetUniformLocation(_program, "uWaveDirection");
            _uWaveSpeed = gl.GetUniformLocation(_program, "uWaveSpeed");
            _uWaveFrequency = gl.GetUniformLocation(_program, "uWaveFrequency");
            _uWaveIntensity = gl.GetUniformLocation(_program, "uWaveIntensity");
            _uDeformNoiseIndex = gl.GetUniformLocation(_program, "uDeformNoiseIndex");
            _uDeformMaskIndex = gl.GetUniformLocation(_program, "uDeformMaskIndex");
            _uDeformDirection = gl.GetUniformLocation(_program, "uDeformDirection");
            _uDeformScrollSpeed = gl.GetUniformLocation(_program, "uDeformScrollSpeed");
            _uDeformTiling = gl.GetUniformLocation(_program, "uDeformTiling");
            _uDeformSpeed = gl.GetUniformLocation(_program, "uDeformSpeed");
            _uDeformFrequency = gl.GetUniformLocation(_program, "uDeformFrequency");
            _uDeformIntensity = gl.GetUniformLocation(_program, "uDeformIntensity");
            _uDeformProtection = gl.GetUniformLocation(_program, "uDeformProtection");
            _uDeformNoiseChannel = gl.GetUniformLocation(_program, "uDeformNoiseChannel");
            _uDeformMaskChannel = gl.GetUniformLocation(_program, "uDeformMaskChannel");
            _uLightDir = gl.GetUniformLocation(_program, "uLightDir");
            _uLightColor = gl.GetUniformLocation(_program, "uLightColor");
            _uLightDir2 = gl.GetUniformLocation(_program, "uLightDir2");
            _uLightColor2 = gl.GetUniformLocation(_program, "uLightColor2");
            _uAmbient = gl.GetUniformLocation(_program, "uAmbient");
            _uLightmap = gl.GetUniformLocation(_program, "uLightmap");
            _uHasLightmap = gl.GetUniformLocation(_program, "uHasLightmap");
            _uLightMapColorScale = gl.GetUniformLocation(_program, "uLightMapColorScale");
            _uColorTint = gl.GetUniformLocation(_program, "uColorTint");
            _uAlphaCutoff = gl.GetUniformLocation(_program, "uAlphaCutoff");
            _uMaterialUvRepeat = gl.GetUniformLocation(_program, "uMaterialUvRepeat");
            _uMaterialUvScroll = gl.GetUniformLocation(_program, "uMaterialUvScroll");
            _uMaterialUnlit = gl.GetUniformLocation(_program, "uMaterialUnlit");
            _uMaterialPremultipliedAlpha = gl.GetUniformLocation(_program, "uMaterialPremultipliedAlpha");
            _uMaterialSrgb = gl.GetUniformLocation(_program, "uMaterialSrgb");
            _uMaterialUsesTextureAlpha = gl.GetUniformLocation(_program, "uMaterialUsesTextureAlpha");
            _uUsesBakedDiffuse = gl.GetUniformLocation(_program, "uUsesBakedDiffuse");
            _uHasVertexColor = gl.GetUniformLocation(_program, "uHasVertexColor");
        }

        private void RenderParts(SceneModel model, bool renderDecals, bool alphaBlended, Vector3 cameraPosition, Matrix4x4 world)
        {
            if (renderDecals)
            {
                _gl.Enable(EnableCap.PolygonOffsetFill);
                _gl.PolygonOffset(-1f, -1f);
            }
            else
            {
                _gl.Disable(EnableCap.PolygonOffsetFill);
            }

            IEnumerable<ModelPart> parts = model.Parts;
            if (alphaBlended)
            {
                _alphaRenderQueue.Clear();
                foreach (ModelPart part in model.Parts)
                    if (part.IsVisible && part.IsDecal == renderDecals && part.IsAlphaBlended)
                        _alphaRenderQueue.Add(part);

                _alphaRenderQueue.Sort((left, right) =>
                    GetRenderDistanceSquared(right, cameraPosition, world)
                        .CompareTo(GetRenderDistanceSquared(left, cameraPosition, world)));
                parts = _alphaRenderQueue;
            }

            uint lastBoundTex0 = uint.MaxValue;

            foreach (ModelPart part in parts)
            {
                if (!part.IsVisible ||
                    part.IsDecal != renderDecals ||
                    part.IsAlphaBlended != alphaBlended)
                {
                    continue;
                }

                GlMeshResourceCache.PartResources resources = _resources.Ensure(model, part);
                if (resources.Vao == 0) continue;

                ModelMaterialDefinition material = part.MaterialDefinition;
                ApplyPartRenderState(part, material);

                _gl.BindVertexArray(resources.Vao);
                _gl.Uniform1(
                    _uUseSkinning,
                    resources.IsGpuSkinned && model.SkinningMatrices != null ? 1 : 0);

                // Texture 0: Diffuse / Albedo (with redundant state cache)
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

                ModelMaterialEffectDefinition effect = material?.Effect ?? ModelMaterialEffectDefinition.None;
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
                _gl.Uniform1(_uMaterialUnlit, material != null && !material.IsLit ? 1 : 0);
                _gl.Uniform1(
                    _uMaterialPremultipliedAlpha,
                    material?.RenderState.PremultipliedAlpha == true ? 1 : 0);
                // Character textures and authored tint follow LTK's sRGB working/output semantics.
                _gl.Uniform1(_uMaterialSrgb, material != null ? 1 : 0);
                bool usesTextureAlpha = material?.UsesTextureAlpha ?? part.AlphaCutoff > 0f;
                _gl.Uniform1(_uMaterialUsesTextureAlpha, usesTextureAlpha ? 1 : 0);
                _gl.Uniform1(_uUsesBakedDiffuse, part.UsesBakedDiffuse ? 1 : 0);
                _gl.Uniform1(_uHasVertexColor, resources.ColorVbo != 0 ? 1 : 0);
                UploadMaterialEffects(effect, resources);

                // --- Lightmap parameters ---
                bool hasLightmap = resources.LightmapTexture != 0 && resources.LightmapVbo != 0;
                _gl.Uniform1(_uHasLightmap, hasLightmap ? 1 : 0);
                if (hasLightmap)
                {
                    _gl.ActiveTexture(TextureUnit.Texture1);
                    _gl.BindTexture(TextureTarget.Texture2D, resources.LightmapTexture);
                }

                _drawElements?.Invoke(
                    (uint)PrimitiveType.Triangles,
                    resources.IndexCount,
                    (uint)DrawElementsType.UnsignedInt,
                    IntPtr.Zero);
            }

            _gl.ActiveTexture(TextureUnit.Texture0);
        }

        private void UploadMaterialEffects(
            ModelMaterialEffectDefinition effect,
            GlMeshResourceCache.PartResources resources)
        {
            Dictionary<string, int> textureBindings = BindAuxiliaryTextures(effect, resources);
            _gl.Uniform1(_uEffectKind, (int)effect.Kind);

            ModelTextureLayerDefinition additive = effect.AdditiveScroll;
            _gl.Uniform1(_uAdditiveTexIndex, TextureIndex(textureBindings, additive?.TextureName));
            _gl.Uniform1(_uAdditiveMaskIndex, TextureIndex(textureBindings, additive?.MaskTextureName));
            SetVector2(_uAdditiveScrollSpeed, additive?.ScrollSpeed ?? Vector2.Zero);
            SetVector2(_uAdditiveTiling, additive?.Tiling ?? Vector2.One);
            SetVector4(_uAdditiveColor, additive?.Color ?? Vector4.One);
            _gl.Uniform1(_uAdditiveStrength, additive?.Strength ?? 0f);
            _gl.Uniform1(_uAdditiveTextureChannel, additive?.TextureChannel ?? -1);
            _gl.Uniform1(_uAdditiveMaskChannel, additive?.MaskChannel ?? 0);

            ModelFlowMapDefinition flow = effect.FlowMap;
            _gl.Uniform1(_uFlowTexIndex, TextureIndex(textureBindings, flow?.TextureName));
            _gl.Uniform1(_uFlowMaskIndex, TextureIndex(textureBindings, flow?.MaskTextureName));
            SetVector2(_uFlowScrollSpeed, flow?.ScrollSpeed ?? Vector2.Zero);
            SetVector2(_uFlowTiling, flow?.Tiling ?? Vector2.One);
            _gl.Uniform1(_uFlowStrength, flow?.Strength ?? 0f);
            _gl.Uniform1(_uFlowIntensity, flow?.Intensity ?? 0f);
            _gl.Uniform1(_uFlowMaskChannel, flow?.MaskChannel ?? 0);

            ModelGradientPulseDefinition gradient = effect.GradientPulse;
            _gl.Uniform1(_uGradientTexIndex, TextureIndex(textureBindings, gradient?.TextureName));
            _gl.Uniform1(_uGradientMaskIndex, TextureIndex(textureBindings, gradient?.MaskTextureName));
            SetVector2(_uGradientScrollSpeed, gradient?.ScrollSpeed ?? Vector2.Zero);
            SetVector2(_uGradientTiling, gradient?.Tiling ?? Vector2.One);
            SetVector4(_uGradientColor, gradient?.Color ?? Vector4.One);
            _gl.Uniform1(_uGradientStrength, gradient?.Strength ?? 0f);
            _gl.Uniform1(_uPulseRate, gradient?.PulseRate ?? 0f);
            _gl.Uniform1(_uPulseMax, gradient?.PulseMax ?? 0f);
            _gl.Uniform1(_uPulseOffset, gradient?.PulseOffset ?? 0f);
            _gl.Uniform1(_uGradientSharpness, gradient?.Sharpness ?? 1f);
            _gl.Uniform1(_uGradientBloomIntensity, gradient?.BloomIntensity ?? 0f);
            _gl.Uniform1(_uGradientMaskThreshold, gradient?.MaskThreshold ?? 0f);
            _gl.Uniform1(_uGradientMaskSoftness, gradient?.MaskSoftness ?? 0.05f);
            _gl.Uniform1(_uGradientTextureChannel, gradient?.TextureChannel ?? 0);
            _gl.Uniform1(_uGradientMaskChannel, gradient?.MaskChannel ?? 0);

            ModelDissolveDefinition dissolve = effect.Dissolve;
            _gl.Uniform1(_uDissolvePatternIndex, TextureIndex(textureBindings, dissolve?.PatternTextureName));
            _gl.Uniform1(_uDissolveStateIndex, TextureIndex(textureBindings, dissolve?.StateTextureName));
            _gl.Uniform1(_uDissolveMaskIndex, TextureIndex(textureBindings, dissolve?.MaskTextureName));
            SetVector2(_uDissolveScrollSpeed, dissolve?.ScrollSpeed ?? Vector2.Zero);
            SetVector2(_uDissolveTiling, dissolve?.Tiling ?? Vector2.One);
            _gl.Uniform1(_uDissolveThreshold, dissolve?.Threshold ?? 0.5f);
            _gl.Uniform1(_uDissolveSoftness, dissolve?.Softness ?? 0.05f);
            _gl.Uniform1(_uDissolvePatternChannel, dissolve?.PatternChannel ?? 0);
            _gl.Uniform1(_uDissolveMaskChannel, dissolve?.MaskChannel ?? 0);

            ModelFresnelDefinition fresnel = effect.Fresnel;
            _gl.Uniform1(_uFresnelMaskIndex, TextureIndex(textureBindings, fresnel?.MaskTextureName));
            _gl.Uniform1(_uFresnelNoiseIndex, TextureIndex(textureBindings, fresnel?.NoiseTextureName));
            SetVector4(_uFresnelColor, fresnel?.Color ?? Vector4.One);
            _gl.Uniform1(_uFresnelPower, fresnel?.Power ?? 2f);
            _gl.Uniform1(_uFresnelStrength, fresnel?.Strength ?? 0f);
            SetVector2(_uFresnelNoiseTiling, fresnel?.NoiseTiling ?? Vector2.One);
            SetVector2(_uFresnelNoiseSpeed, fresnel?.NoiseSpeed ?? Vector2.Zero);
            _gl.Uniform1(_uFresnelMaskChannel, fresnel?.MaskChannel ?? 0);
            _gl.Uniform1(_uFresnelNoiseChannel, fresnel?.NoiseChannel ?? 0);

            ModelBloomDefinition bloom = effect.Bloom;
            _gl.Uniform1(_uBloomMaskIndex, TextureIndex(textureBindings, bloom?.MaskTextureName));
            SetVector4(_uBloomColor, bloom?.Color ?? Vector4.One);
            _gl.Uniform1(_uBloomIntensity, bloom?.Intensity ?? 0f);
            _gl.Uniform1(_uBloomMaskChannel, bloom?.MaskChannel ?? 0);

            ModelEmissionDefinition emission = effect.Emission;
            _gl.Uniform1(_uEmissionTexIndex, TextureIndex(textureBindings, emission?.TextureName));
            _gl.Uniform1(_uEmissionMaskIndex, TextureIndex(textureBindings, emission?.MaskTextureName));
            SetVector2(_uEmissionScrollSpeed, emission?.ScrollSpeed ?? Vector2.Zero);
            SetVector2(_uEmissionTiling, emission?.Tiling ?? Vector2.One);
            SetVector4(_uEmissionColor, emission?.Color ?? Vector4.One);
            _gl.Uniform1(_uEmissionStrength, emission?.Strength ?? 0f);
            _gl.Uniform1(_uEmissionChannel, emission?.TextureChannel ?? -1);
            _gl.Uniform1(_uEmissionMaskChannel, emission?.MaskChannel ?? 0);

            ModelDistortionDefinition distortion = effect.Distortion;
            _gl.Uniform1(_uDistortionTexIndex, TextureIndex(textureBindings, distortion?.TextureName));
            _gl.Uniform1(_uDistortionMaskIndex, TextureIndex(textureBindings, distortion?.MaskTextureName));
            SetVector2(_uDistortionScrollSpeed, distortion?.ScrollSpeed ?? Vector2.Zero);
            SetVector2(_uDistortionTiling, distortion?.Tiling ?? Vector2.One);
            _gl.Uniform1(_uDistortionStrength, distortion?.Strength ?? 0f);
            _gl.Uniform1(_uDistortionChannelX, distortion?.ChannelX ?? 0);
            _gl.Uniform1(_uDistortionChannelY, distortion?.ChannelY ?? -1);
            _gl.Uniform1(_uDistortionMaskChannel, distortion?.MaskChannel ?? 0);

            ModelIridescenceDefinition iridescence = effect.Iridescence;
            _gl.Uniform1(_uIridescenceTexIndex, TextureIndex(textureBindings, iridescence?.LutTextureName));
            _gl.Uniform1(
                _uIridescenceMaskIndex,
                TextureIndex(
                    textureBindings,
                    iridescence?.MaskTextureName,
                    namedMissingValue: -2));
            SetVector4(_uIridescenceControl, iridescence?.Control ?? new Vector4(1f, 1f, 1f, 0f));
            SetVector2(
                _uIridescencePulseSpeedMin,
                iridescence?.UsesPulse == true ? iridescence.PulseSpeedMin : Vector2.Zero);
            SetVector2(
                _uIridescenceAlphaMinMax,
                iridescence?.RequiresAlphaBlend == true ? iridescence.FresnelAlphaMinMax : Vector2.One);
            _gl.Uniform1(
                _uIridescenceDiffuseFadeMask,
                iridescence?.RequiresAlphaBlend == true ? iridescence.DiffuseFadeMaskValue : 0f);
            _gl.Uniform1(_uIridescenceMaskChannel, iridescence?.MaskChannel ?? 0);

            ModelWaveDefinition wave = effect.Wave;
            _gl.Uniform3(_uWaveDirection, wave?.Direction ?? Vector3.UnitY);
            _gl.Uniform1(_uWaveSpeed, wave?.Speed ?? 0f);
            _gl.Uniform1(_uWaveFrequency, wave?.Frequency ?? 1f);
            _gl.Uniform1(_uWaveIntensity, wave?.Intensity ?? 0f);

            ModelVertexDeformationDefinition deformation = effect.VertexDeformation;
            _gl.Uniform1(_uDeformNoiseIndex, TextureIndex(textureBindings, deformation?.NoiseTextureName));
            _gl.Uniform1(_uDeformMaskIndex, TextureIndex(textureBindings, deformation?.MaskTextureName));
            _gl.Uniform3(_uDeformDirection, deformation?.Direction ?? Vector3.UnitY);
            SetVector2(_uDeformScrollSpeed, deformation?.ScrollSpeed ?? Vector2.Zero);
            SetVector2(_uDeformTiling, deformation?.Tiling ?? Vector2.One);
            _gl.Uniform1(_uDeformSpeed, deformation?.Speed ?? 0f);
            _gl.Uniform1(_uDeformFrequency, deformation?.Frequency ?? 1f);
            _gl.Uniform1(_uDeformIntensity, deformation?.Intensity ?? 0f);
            _gl.Uniform1(_uDeformProtection, deformation?.Protection ?? 0f);
            _gl.Uniform1(_uDeformNoiseChannel, deformation?.NoiseChannel ?? 0);
            _gl.Uniform1(_uDeformMaskChannel, deformation?.MaskChannel ?? 0);
        }

        private Dictionary<string, int> BindAuxiliaryTextures(
            ModelMaterialEffectDefinition effect,
            GlMeshResourceCache.PartResources resources)
        {
            var bindings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int slot = 0;
            foreach (string textureKey in effect.EnumerateTextureNames())
            {
                if (string.IsNullOrWhiteSpace(textureKey) || bindings.ContainsKey(textureKey))
                    continue;
                if (!resources.AuxiliaryTextures.TryGetValue(textureKey, out uint texture))
                    continue;
                if (slot >= _maxAuxiliaryTextures)
                {
                    Debug.WriteLine(
                        $"SKN material auxiliary texture capacity exceeded ({_maxAuxiliaryTextures}); " +
                        $"remaining authored layer textures cannot be bound on this OpenGL context.");
                    break;
                }

                bindings[textureKey] = slot;
                _gl.ActiveTexture(ToTextureUnit(slot + 2));
                _gl.BindTexture(TextureTarget.Texture2D, texture);
                _gl.BindSampler((uint)(slot + 2), ResolveAuxiliarySampler(effect, textureKey));
                slot++;
            }

            for (int i = slot; i < _maxAuxiliaryTextures; i++)
            {
                _gl.ActiveTexture(ToTextureUnit(i + 2));
                _gl.BindTexture(TextureTarget.Texture2D, 0);
                _gl.BindSampler((uint)(i + 2), 0);
            }
            return bindings;
        }

        private static int TextureIndex(
            IReadOnlyDictionary<string, int> bindings,
            string textureKey,
            int namedMissingValue = -1)
        {
            if (string.IsNullOrWhiteSpace(textureKey))
                return -1;
            return bindings.TryGetValue(textureKey, out int index) ? index : namedMissingValue;
        }

        private uint ResolveAuxiliarySampler(ModelMaterialEffectDefinition effect, string textureKey)
        {
            ModelMaterialEffectDefinition materialEffect = effect ?? ModelMaterialEffectDefinition.None;
            if (!materialEffect.TextureSampling.TryGetValue(textureKey, out ModelEffectTextureSamplingDefinition sampling))
            {
                sampling = new ModelEffectTextureSamplingDefinition(
                    ModelMaterialWrapMode.Repeat,
                    ModelMaterialWrapMode.Repeat);
            }

            var key = (sampling.WrapU, sampling.WrapV);
            if (_auxiliarySamplers.TryGetValue(key, out uint sampler))
                return sampler;

            sampler = _gl.GenSampler();
            _gl.SamplerParameter(
                sampler,
                SamplerParameterI.MinFilter,
                (int)TextureMinFilter.LinearMipmapLinear);
            _gl.SamplerParameter(
                sampler,
                SamplerParameterI.MagFilter,
                (int)TextureMagFilter.Linear);
            _gl.SamplerParameter(
                sampler,
                SamplerParameterI.WrapS,
                (int)ToTextureWrapMode(sampling.WrapU));
            _gl.SamplerParameter(
                sampler,
                SamplerParameterI.WrapT,
                (int)ToTextureWrapMode(sampling.WrapV));
            _auxiliarySamplers[key] = sampler;
            return sampler;
        }

        private void SetVector2(int location, Vector2 value) =>
            _gl.Uniform2(location, value.X, value.Y);

        private void SetVector4(int location, Vector4 value) =>
            _gl.Uniform4(location, value.X, value.Y, value.Z, value.W);

        private static TextureUnit ToTextureUnit(int index) =>
            (TextureUnit)((int)TextureUnit.Texture0 + index);

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
                (part.ColorTint.W < 0.999f || material.Effect?.RequiresAlphaBlend == true);
            ModelMaterialBlendMode blending = runtimeForcesBlend
                ? ModelMaterialBlendMode.Normal
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

            // Runtime opacity or an authored shader alpha layer may promote an opaque material
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
            for (int i = _maxAuxiliaryTextures + 1; i >= 0; i--)
            {
                _gl.ActiveTexture(ToTextureUnit(i));
                _gl.BindTexture(TextureTarget.Texture2D, 0);
                _gl.BindSampler((uint)i, 0);
            }
            _gl.ActiveTexture(TextureUnit.Texture0);
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

        private static Matrix4x4 CreateWorldMatrix(SceneModel model)
        {
            float pitch = (float)(model.RotationX * (Math.PI / 180.0));
            float yaw = (float)(model.RotationY * (Math.PI / 180.0));
            float roll = (float)(model.RotationZ * (Math.PI / 180.0));
            return Matrix4x4.CreateScale((float)model.Scale) *
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
                _materialTimeOrigins.Remove(model);
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
                _resources?.Dispose();
                foreach (uint sampler in _auxiliarySamplers.Values)
                {
                    if (sampler != 0)
                        _gl?.DeleteSampler(sampler);
                }
                _auxiliarySamplers.Clear();
                _materialTimeOrigins.Clear();
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
                _boneBuffer = 0;
                _program = 0;
                _ready = false;
            }
        }
    }
}
