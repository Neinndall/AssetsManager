using System;
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

        private GL _gl = null!;
        private GlMeshResourceCache _resources = null!;
        private uint _program;
        private uint _boneBuffer;
        private int _uViewProj;
        private int _uWorld;
        private int _uUseSkinning;
        private int _uTex;
        private int _uEffectTex;
        private int _uEffectMask;
        private int _uEmissionTex;
        private int _uEmissionMask;
        private int _uEffectKind;
        private int _uHasEffectTex;
        private int _uHasEmissionTex;
        private int _uHasEmissionMask;
        private int _uEffectTime;
        private int _uEffectScrollSpeed;
        private int _uEffectTiling;
        private int _uEffectColor;
        private int _uEffectStrength;
        private int _uFlowIntensity;
        private int _uCameraPosition;
        private int _uFresnelColor;
        private int _uFresnelPower;
        private int _uFresnelStrength;
        private int _uDissolveThreshold;
        private int _uDissolveSoftness;
        private int _uBloomColor;
        private int _uBloomIntensity;
        private int _uPulseRate;
        private int _uPulseMax;
        private int _uPulseOffset;
        private int _uGradientSharpness;
        private int _uEmissionScrollSpeed;
        private int _uEmissionTiling;
        private int _uEmissionColor;
        private int _uEmissionStrength;
        private int _uEmissionChannel;
        private int _uIridescenceTex;
        private int _uIridescenceMask;
        private int _uHasIridescenceTex;
        private int _uIridescenceControl;
        private int _uIridescencePulseSpeedMin;
        private int _uIridescenceAlphaMinMax;
        private int _uIridescenceDiffuseFadeMask;
        private int _uWaveDirection;
        private int _uWaveSpeed;
        private int _uWaveFrequency;
        private int _uWaveIntensity;
        private int _uFresnelNoiseTiling;
        private int _uFresnelNoiseSpeed;
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
        private int _uUsesBakedDiffuse;
        private int _uHasVertexColor;
        private bool _ready;
        private long _startTimestamp;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void DrawElementsDelegate(uint mode, int count, uint type, IntPtr indices);

        private DrawElementsDelegate _drawElements = null!;

        public void Initialize(GL gl)
        {
            _gl = gl;
            _startTimestamp = Stopwatch.GetTimestamp();
            IntPtr proc = gl.Context.GetProcAddress("glDrawElements");
            if (proc != IntPtr.Zero)
            {
                _drawElements = Marshal.GetDelegateForFunctionPointer<DrawElementsDelegate>(proc);
            }

            bool gles = GlShaderCompiler.UsesEmbeddedProfile(gl);
            _program = GlShaderCompiler.CreateProgram(
                gl,
                gles,
                GlMeshShaderSource.Vertex,
                GlMeshShaderSource.Fragment);
            CacheUniformLocations(gl);
            uint boneBlock = gl.GetUniformBlockIndex(_program, "BoneTransforms");
            if (boneBlock != uint.MaxValue)
                gl.UniformBlockBinding(_program, boneBlock, 0);
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
            _gl.Uniform1(_uTex, 0);
            _gl.Uniform1(_uLightmap, 1);
            _gl.Uniform1(_uEffectTex, 2);
            _gl.Uniform1(_uEffectMask, 3);
            _gl.Uniform1(_uEmissionTex, 4);
            _gl.Uniform1(_uEmissionMask, 5);
            _gl.Uniform1(_uIridescenceTex, 6);
            _gl.Uniform1(_uIridescenceMask, 7);
            _gl.Uniform1(
                _uEffectTime,
                (float)((Stopwatch.GetTimestamp() - _startTimestamp) / (double)Stopwatch.Frequency));
            _gl.Uniform1(_uLightMapColorScale, lightmapScale);

            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
            RenderParts(model, renderDecals: false, alphaBlended: false);
            RenderParts(model, renderDecals: true, alphaBlended: false);

            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.DepthMask(false);
            RenderParts(model, renderDecals: false, alphaBlended: true);
            RenderParts(model, renderDecals: true, alphaBlended: true);

            _gl.Disable(EnableCap.PolygonOffsetFill);
            _gl.Disable(EnableCap.Blend);
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
            _uEffectTex = gl.GetUniformLocation(_program, "uEffectTex");
            _uEffectMask = gl.GetUniformLocation(_program, "uEffectMask");
            _uEmissionTex = gl.GetUniformLocation(_program, "uEmissionTex");
            _uEmissionMask = gl.GetUniformLocation(_program, "uEmissionMask");
            _uEffectKind = gl.GetUniformLocation(_program, "uEffectKind");
            _uHasEffectTex = gl.GetUniformLocation(_program, "uHasEffectTex");
            _uHasEmissionTex = gl.GetUniformLocation(_program, "uHasEmissionTex");
            _uHasEmissionMask = gl.GetUniformLocation(_program, "uHasEmissionMask");
            _uEffectTime = gl.GetUniformLocation(_program, "uEffectTime");
            _uEffectScrollSpeed = gl.GetUniformLocation(_program, "uEffectScrollSpeed");
            _uEffectTiling = gl.GetUniformLocation(_program, "uEffectTiling");
            _uEffectColor = gl.GetUniformLocation(_program, "uEffectColor");
            _uEffectStrength = gl.GetUniformLocation(_program, "uEffectStrength");
            _uFlowIntensity = gl.GetUniformLocation(_program, "uFlowIntensity");
            _uCameraPosition = gl.GetUniformLocation(_program, "uCameraPosition");
            _uFresnelColor = gl.GetUniformLocation(_program, "uFresnelColor");
            _uFresnelPower = gl.GetUniformLocation(_program, "uFresnelPower");
            _uFresnelStrength = gl.GetUniformLocation(_program, "uFresnelStrength");
            _uDissolveThreshold = gl.GetUniformLocation(_program, "uDissolveThreshold");
            _uDissolveSoftness = gl.GetUniformLocation(_program, "uDissolveSoftness");
            _uBloomColor = gl.GetUniformLocation(_program, "uBloomColor");
            _uBloomIntensity = gl.GetUniformLocation(_program, "uBloomIntensity");
            _uPulseRate = gl.GetUniformLocation(_program, "uPulseRate");
            _uPulseMax = gl.GetUniformLocation(_program, "uPulseMax");
            _uPulseOffset = gl.GetUniformLocation(_program, "uPulseOffset");
            _uGradientSharpness = gl.GetUniformLocation(_program, "uGradientSharpness");
            _uEmissionScrollSpeed = gl.GetUniformLocation(_program, "uEmissionScrollSpeed");
            _uEmissionTiling = gl.GetUniformLocation(_program, "uEmissionTiling");
            _uEmissionColor = gl.GetUniformLocation(_program, "uEmissionColor");
            _uEmissionStrength = gl.GetUniformLocation(_program, "uEmissionStrength");
            _uEmissionChannel = gl.GetUniformLocation(_program, "uEmissionChannel");
            _uIridescenceTex = gl.GetUniformLocation(_program, "uIridescenceTex");
            _uIridescenceMask = gl.GetUniformLocation(_program, "uIridescenceMask");
            _uHasIridescenceTex = gl.GetUniformLocation(_program, "uHasIridescenceTex");
            _uIridescenceControl = gl.GetUniformLocation(_program, "uIridescenceControl");
            _uIridescencePulseSpeedMin = gl.GetUniformLocation(_program, "uIridescencePulseSpeedMin");
            _uIridescenceAlphaMinMax = gl.GetUniformLocation(_program, "uIridescenceAlphaMinMax");
            _uIridescenceDiffuseFadeMask = gl.GetUniformLocation(_program, "uIridescenceDiffuseFadeMask");
            _uWaveDirection = gl.GetUniformLocation(_program, "uWaveDirection");
            _uWaveSpeed = gl.GetUniformLocation(_program, "uWaveSpeed");
            _uWaveFrequency = gl.GetUniformLocation(_program, "uWaveFrequency");
            _uWaveIntensity = gl.GetUniformLocation(_program, "uWaveIntensity");
            _uFresnelNoiseTiling = gl.GetUniformLocation(_program, "uFresnelNoiseTiling");
            _uFresnelNoiseSpeed = gl.GetUniformLocation(_program, "uFresnelNoiseSpeed");
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
            _uUsesBakedDiffuse = gl.GetUniformLocation(_program, "uUsesBakedDiffuse");
            _uHasVertexColor = gl.GetUniformLocation(_program, "uHasVertexColor");
        }

        private void RenderParts(SceneModel model, bool renderDecals, bool alphaBlended)
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

            foreach (ModelPart part in model.Parts)
            {
                if (!part.IsVisible ||
                    part.IsDecal != renderDecals ||
                    part.IsAlphaBlended != alphaBlended)
                {
                    continue;
                }

                GlMeshResourceCache.PartResources resources = _resources.Ensure(model, part);
                if (resources.Vao == 0) continue;

                _gl.BindVertexArray(resources.Vao);
                _gl.Uniform1(
                    _uUseSkinning,
                    resources.IsGpuSkinned && model.SkinningMatrices != null ? 1 : 0);
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(
                    TextureTarget.Texture2D,
                    resources.Texture != 0 ? resources.Texture : _resources.WhiteTexture);
                ModelMaterialEffectDefinition effect =
                    part.MaterialEffect ?? ModelMaterialEffectDefinition.None;
                ModelIridescenceDefinition iridescence = effect.Iridescence;
                _gl.Uniform4(_uColorTint, part.ColorTint.X, part.ColorTint.Y, part.ColorTint.Z, part.ColorTint.W);
                _gl.Uniform1(_uAlphaCutoff, part.IsAlphaBlended ? 0f : part.AlphaCutoff);
                _gl.Uniform1(_uUsesBakedDiffuse, part.UsesBakedDiffuse ? 1 : 0);
                _gl.Uniform1(_uHasVertexColor, resources.ColorVbo != 0 ? 1 : 0);

                _gl.Uniform1(_uEffectKind, (int)effect.Kind);
                _gl.Uniform1(_uHasIridescenceTex, resources.IridescenceTexture != 0 ? 1 : 0);
                Vector4 iridescenceControl = iridescence?.Control ?? new Vector4(1f, 1f, 1f, 0f);
                Vector2 iridescencePulseSpeedMin = iridescence?.UsesPulse == true
                    ? iridescence.PulseSpeedMin
                    : Vector2.Zero;
                Vector2 iridescenceAlphaMinMax = iridescence?.UsesLocalizedAlpha == true
                    ? iridescence.FresnelAlphaMinMax
                    : Vector2.One;
                _gl.Uniform4(
                    _uIridescenceControl,
                    iridescenceControl.X,
                    iridescenceControl.Y,
                    iridescenceControl.Z,
                    iridescenceControl.W);
                _gl.Uniform2(
                    _uIridescencePulseSpeedMin,
                    iridescencePulseSpeedMin.X,
                    iridescencePulseSpeedMin.Y);
                _gl.Uniform2(
                    _uIridescenceAlphaMinMax,
                    iridescenceAlphaMinMax.X,
                    iridescenceAlphaMinMax.Y);
                _gl.Uniform1(
                    _uIridescenceDiffuseFadeMask,
                    iridescence?.UsesLocalizedAlpha == true
                        ? iridescence.DiffuseFadeMaskValue
                        : 0f);
                if (effect.Kind != ModelMaterialEffectKind.None)
                {
                    _gl.Uniform1(_uHasEffectTex, resources.EffectTexture != 0 ? 1 : 0);
                    _gl.Uniform1(_uHasEmissionTex, resources.EmissionTexture != 0 ? 1 : 0);
                    _gl.Uniform1(_uHasEmissionMask, resources.EmissionMaskTexture != 0 ? 1 : 0);
                    _gl.Uniform2(_uEffectScrollSpeed, effect.ScrollSpeed.X, effect.ScrollSpeed.Y);
                    _gl.Uniform2(_uEffectTiling, effect.Tiling.X, effect.Tiling.Y);
                    _gl.Uniform4(_uEffectColor, effect.Color.X, effect.Color.Y, effect.Color.Z, effect.Color.W);
                    _gl.Uniform1(_uEffectStrength, effect.Strength);
                    _gl.Uniform1(_uFlowIntensity, effect.FlowIntensity);
                    _gl.Uniform4(_uFresnelColor, effect.FresnelColor.X, effect.FresnelColor.Y, effect.FresnelColor.Z, effect.FresnelColor.W);
                    _gl.Uniform1(_uFresnelPower, effect.FresnelPower);
                    _gl.Uniform1(_uFresnelStrength, effect.FresnelStrength);
                    _gl.Uniform1(_uDissolveThreshold, effect.DissolveThreshold);
                    _gl.Uniform1(_uDissolveSoftness, effect.DissolveSoftness);
                    _gl.Uniform4(_uBloomColor, effect.BloomColor.X, effect.BloomColor.Y, effect.BloomColor.Z, effect.BloomColor.W);
                    _gl.Uniform1(_uBloomIntensity, effect.BloomIntensity);
                    _gl.Uniform1(_uPulseRate, effect.PulseRate);
                    _gl.Uniform1(_uPulseMax, effect.PulseMax);
                    _gl.Uniform1(_uPulseOffset, effect.PulseOffset);
                    _gl.Uniform1(_uGradientSharpness, effect.GradientSharpness);
                    _gl.Uniform2(_uEmissionScrollSpeed, effect.EmissionScrollSpeed.X, effect.EmissionScrollSpeed.Y);
                    _gl.Uniform2(_uEmissionTiling, effect.EmissionTiling.X, effect.EmissionTiling.Y);
                    _gl.Uniform4(_uEmissionColor, effect.EmissionColor.X, effect.EmissionColor.Y, effect.EmissionColor.Z, effect.EmissionColor.W);
                    _gl.Uniform1(_uEmissionStrength, effect.EmissionStrength);
                    _gl.Uniform1(_uEmissionChannel, effect.EmissionChannel);
                    _gl.Uniform3(_uWaveDirection, effect.WaveDirection);
                    _gl.Uniform1(_uWaveSpeed, effect.WaveSpeed);
                    _gl.Uniform1(_uWaveFrequency, effect.WaveFrequency);
                    _gl.Uniform1(_uWaveIntensity, effect.WaveIntensity);
                    _gl.Uniform2(_uFresnelNoiseTiling, effect.FresnelNoiseTiling.X, effect.FresnelNoiseTiling.Y);
                    _gl.Uniform2(_uFresnelNoiseSpeed, effect.FresnelNoiseSpeed.X, effect.FresnelNoiseSpeed.Y);
                }

                bool hasLightmap = resources.LightmapTexture != 0 && resources.LightmapVbo != 0;
                _gl.Uniform1(_uHasLightmap, hasLightmap ? 1 : 0);
                _gl.ActiveTexture(TextureUnit.Texture1);
                _gl.BindTexture(
                    TextureTarget.Texture2D,
                    hasLightmap ? resources.LightmapTexture : 0);
                _gl.ActiveTexture(TextureUnit.Texture2);
                _gl.BindTexture(
                    TextureTarget.Texture2D,
                    resources.EffectTexture);
                _gl.ActiveTexture(TextureUnit.Texture3);
                _gl.BindTexture(
                    TextureTarget.Texture2D,
                    resources.EffectMaskTexture != 0 ? resources.EffectMaskTexture : _resources.WhiteTexture);
                _gl.ActiveTexture(TextureUnit.Texture4);
                _gl.BindTexture(
                    TextureTarget.Texture2D,
                    resources.EmissionTexture);
                _gl.Uniform1(_uEmissionMask, 5);
                _gl.ActiveTexture(TextureUnit.Texture5);
                _gl.BindTexture(
                    TextureTarget.Texture2D,
                    resources.EmissionMaskTexture != 0 ? resources.EmissionMaskTexture : _resources.WhiteTexture);
                _gl.ActiveTexture(TextureUnit.Texture6);
                _gl.BindTexture(
                    TextureTarget.Texture2D,
                    resources.IridescenceTexture != 0 ? resources.IridescenceTexture : _resources.WhiteTexture);
                _gl.ActiveTexture(TextureUnit.Texture7);
                _gl.BindTexture(
                    TextureTarget.Texture2D,
                    resources.IridescenceMaskTexture != 0 ? resources.IridescenceMaskTexture : _resources.WhiteTexture);
                _gl.ActiveTexture(TextureUnit.Texture0);

                _drawElements?.Invoke(
                    (uint)PrimitiveType.Triangles,
                    resources.IndexCount,
                    (uint)DrawElementsType.UnsignedInt,
                    IntPtr.Zero);
            }
        }

        private void UnbindSceneTextures()
        {
            _gl.ActiveTexture(TextureUnit.Texture7);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.ActiveTexture(TextureUnit.Texture6);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.ActiveTexture(TextureUnit.Texture5);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.ActiveTexture(TextureUnit.Texture4);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.ActiveTexture(TextureUnit.Texture3);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.ActiveTexture(TextureUnit.Texture2);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
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

        private static Vector3 NormalizeOrDefault(Vector3 value) =>
            value.LengthSquared() > 1e-6f ? Vector3.Normalize(value) : Vector3.UnitY;

        public void QueueRelease(SceneModel model) => _resources?.QueueRelease(model);

        public void ProcessPendingReleases()
        {
            if (_ready)
                _resources.ProcessPendingReleases();
        }

        internal static void PremultiplyBgra(Span<byte> pixels) =>
            GlMeshResourceCache.PremultiplyBgra(pixels);

        public void Dispose()
        {
            if (!_ready) return;

            _resources.Dispose();
            if (_boneBuffer != 0)
                _gl.DeleteBuffer(_boneBuffer);
            _boneBuffer = 0;
            if (_program != 0)
                _gl.DeleteProgram(_program);
            _ready = false;
        }
    }
}
