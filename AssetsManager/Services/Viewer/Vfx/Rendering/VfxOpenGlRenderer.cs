using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Services.Viewer.Vfx.Runtime;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    /// <summary>
    /// Draws effect billboards and mesh primitives from prepared playback state.
    /// </summary>
    public sealed class VfxOpenGlRenderer : IDisposable
    {
        private GL _gl = null!;
        private uint _program, _vao, _quadVbo, _instVbo;
        private int _uViewProj, _uCamRight, _uCamUp, _uTexDiv, _uTexSize, _uTex, _uEmitterUvOffset;
        private int _uTexMult, _uHasTexMult, _uTexDivMult, _uTexSizeMult, _uUvScrollRateMult, _uFlipUMult, _uFlipVMult;
        private int _uUvTransformCenter, _uUvTransformCenterMult, _uAddressMode, _uAddressModeMult, _uClampUvMult;
        private int _uIsDistortion, _uDistortionTex, _uSceneTex, _uViewportSize, _uDistortionStrength;
        private int _uSceneDepthTex, _uHasSoftParticle, _uSoftParticleParams;
        private int _uReflectionTex, _uHasReflection, _uReflectionOpacity, _uReflectionColor;
        private int _uDirectionOriented, _uArbitraryQuad;
        private int _uPrimitiveKind;
        private int _uAlphaCutoff, _uAlphaTest, _uDeriveAlphaFromRgb, _uEmissiveStrength, _uIsMultiply, _uFlipU, _uFlipV, _uClampUv;
        private int _uColorMap, _uHasColor, _uColorRenderFlags, _uIsAdditive, _uModulationFactor;
        private int _uPaletteMap, _uHasPalette, _uPaletteCount, _uPaletteMixMask;
        private int _uColorLookUpTypeX, _uColorLookUpTypeY, _uColorLookUpScales, _uColorLookUpOffsets;
        private int _uErosionTex, _uHasErosion, _uErosionFeatherIn, _uErosionFeatherOut;
        private int _uPlacementRight, _uPlacementUp, _uPlacementForward, _uIsGroundLayer;
        private int _instCapFloats;
        private bool _ready;
        private VfxTextureResourceCache _textures = null!;
        private VfxSceneCapture _capture = null!;
        private VfxMeshResourceCache _meshResources = null!;
        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
        private delegate void DrawElementsDelegate(uint mode, int count, uint type, IntPtr indices);
        private DrawElementsDelegate _drawElements = null!;
        private const int Stride = VfxPlaybackRuntime.InstanceStride;
        private bool _gles;
        public void Initialize(GL gl)
        {
            _gl = gl;
            var proc = gl.Context.GetProcAddress("glDrawElements");
            if (proc == IntPtr.Zero)
                throw new NotSupportedException("The active OpenGL context does not expose glDrawElements.");
            _drawElements = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<DrawElementsDelegate>(proc);
            bool gles = GlShaderCompiler.UsesEmbeddedProfile(gl);
            _gles = gles;
            _program = GlShaderCompiler.CreateProgram(gl, gles, VfxShaderSource.ParticleVertex, VfxShaderSource.ParticleFragment);
            _uViewProj = gl.GetUniformLocation(_program, "uViewProj");
            _uCamRight = gl.GetUniformLocation(_program, "uCamRight");
            _uCamUp = gl.GetUniformLocation(_program, "uCamUp");
            _uTexDiv = gl.GetUniformLocation(_program, "uTexDiv");
            _uTexSize = gl.GetUniformLocation(_program, "uTexSize");
            _uTex = gl.GetUniformLocation(_program, "uTex");
            _uTexMult = gl.GetUniformLocation(_program, "uTexMult");
            _uHasTexMult = gl.GetUniformLocation(_program, "uHasTexMult");
            _uTexDivMult = gl.GetUniformLocation(_program, "uTexDivMult");
            _uTexSizeMult = gl.GetUniformLocation(_program, "uTexSizeMult");
            _uUvScrollRateMult = gl.GetUniformLocation(_program, "uUvScrollRateMult");
            _uFlipUMult = gl.GetUniformLocation(_program, "uFlipUMult");
            _uFlipVMult = gl.GetUniformLocation(_program, "uFlipVMult");
            _uUvTransformCenter = gl.GetUniformLocation(_program, "uUvTransformCenter");
            _uUvTransformCenterMult = gl.GetUniformLocation(_program, "uUvTransformCenterMult");
            _uAddressMode = gl.GetUniformLocation(_program, "uAddressMode");
            _uAddressModeMult = gl.GetUniformLocation(_program, "uAddressModeMult");
            _uClampUvMult = gl.GetUniformLocation(_program, "uClampUvMult");
            _uEmitterUvOffset = gl.GetUniformLocation(_program, "uEmitterUvOffset");
            _uIsDistortion = gl.GetUniformLocation(_program, "uIsDistortion");
            _uDistortionTex = gl.GetUniformLocation(_program, "uDistortionTex");
            _uSceneTex = gl.GetUniformLocation(_program, "uSceneTex");
            _uViewportSize = gl.GetUniformLocation(_program, "uViewportSize");
            _uDistortionStrength = gl.GetUniformLocation(_program, "uDistortionStrength");
            _uSceneDepthTex = gl.GetUniformLocation(_program, "uSceneDepthTex");
            _uHasSoftParticle = gl.GetUniformLocation(_program, "uHasSoftParticle");
            _uSoftParticleParams = gl.GetUniformLocation(_program, "uSoftParticleParams");
            _uReflectionTex = gl.GetUniformLocation(_program, "uReflectionTex");
            _uHasReflection = gl.GetUniformLocation(_program, "uHasReflection");
            _uReflectionOpacity = gl.GetUniformLocation(_program, "uReflectionOpacity");
            _uReflectionColor = gl.GetUniformLocation(_program, "uReflectionColor");
            _uDirectionOriented = gl.GetUniformLocation(_program, "uDirectionOriented");
            _uArbitraryQuad = gl.GetUniformLocation(_program, "uArbitraryQuad");
            _uPrimitiveKind = gl.GetUniformLocation(_program, "uPrimitiveKind");
            _uAlphaCutoff = gl.GetUniformLocation(_program, "uAlphaCutoff");
            _uAlphaTest = gl.GetUniformLocation(_program, "uAlphaTest");
            _uDeriveAlphaFromRgb = gl.GetUniformLocation(_program, "uDeriveAlphaFromRgb");
            _uEmissiveStrength = gl.GetUniformLocation(_program, "uEmissiveStrength");
            _uIsMultiply = gl.GetUniformLocation(_program, "uIsMultiply");
            _uColorMap = gl.GetUniformLocation(_program, "uColorMap");
            _uHasColor = gl.GetUniformLocation(_program, "uHasColor");
            _uColorRenderFlags = gl.GetUniformLocation(_program, "uColorRenderFlags");
            _uIsAdditive = gl.GetUniformLocation(_program, "uIsAdditive");
            _uModulationFactor = gl.GetUniformLocation(_program, "uModulationFactor");
            _uPaletteMap = gl.GetUniformLocation(_program, "uPaletteMap");
            _uHasPalette = gl.GetUniformLocation(_program, "uHasPalette");
            _uPaletteCount = gl.GetUniformLocation(_program, "uPaletteCount");
            _uPaletteMixMask = gl.GetUniformLocation(_program, "uPaletteMixMask");
            _uColorLookUpTypeX = gl.GetUniformLocation(_program, "uColorLookUpTypeX");
            _uColorLookUpTypeY = gl.GetUniformLocation(_program, "uColorLookUpTypeY");
            _uColorLookUpScales = gl.GetUniformLocation(_program, "uColorLookUpScales");
            _uColorLookUpOffsets = gl.GetUniformLocation(_program, "uColorLookUpOffsets");
            _uFlipU = gl.GetUniformLocation(_program, "uFlipU");
            _uFlipV = gl.GetUniformLocation(_program, "uFlipV");
            _uClampUv = gl.GetUniformLocation(_program, "uClampUv");
            _uErosionTex = gl.GetUniformLocation(_program, "uErosionTex");
            _uHasErosion = gl.GetUniformLocation(_program, "uHasErosion");
            _uErosionFeatherIn = gl.GetUniformLocation(_program, "uErosionFeatherIn");
            _uErosionFeatherOut = gl.GetUniformLocation(_program, "uErosionFeatherOut");
            _uPlacementRight = gl.GetUniformLocation(_program, "uPlacementRight");
            _uPlacementUp = gl.GetUniformLocation(_program, "uPlacementUp");
            _uPlacementForward = gl.GetUniformLocation(_program, "uPlacementForward");
            _uIsGroundLayer = gl.GetUniformLocation(_program, "uIsGroundLayer");
            _vao = gl.GenVertexArray();
            gl.BindVertexArray(_vao);
            // static base quad (4 corners, drawn as a triangle fan)
            float[] quad = { -0.5f, -0.5f, 0.5f, -0.5f, 0.5f, 0.5f, -0.5f, 0.5f };
            _quadVbo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);
            gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(quad), BufferUsageARB.StaticDraw);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), IntPtr.Zero);

            // per-instance buffer (filled per emitter each frame)
            _instVbo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instVbo);
            uint bstride = Stride * sizeof(float);

            gl.EnableVertexAttribArray(1); gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, bstride, new IntPtr(0));
            gl.EnableVertexAttribArray(2); gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, bstride, new IntPtr(3 * sizeof(float)));
            gl.EnableVertexAttribArray(3); gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, bstride, new IntPtr(5 * sizeof(float)));
            gl.EnableVertexAttribArray(4); gl.VertexAttribPointer(4, 2, VertexAttribPointerType.Float, false, bstride, new IntPtr(9 * sizeof(float)));
            gl.EnableVertexAttribArray(5); gl.VertexAttribPointer(5, 4, VertexAttribPointerType.Float, false, bstride, new IntPtr(11 * sizeof(float)));
            gl.EnableVertexAttribArray(6); gl.VertexAttribPointer(6, 3, VertexAttribPointerType.Float, false, bstride, new IntPtr(15 * sizeof(float)));
            gl.EnableVertexAttribArray(7); gl.VertexAttribPointer(7, 2, VertexAttribPointerType.Float, false, bstride, new IntPtr(19 * sizeof(float)));
            gl.EnableVertexAttribArray(8); gl.VertexAttribPointer(8, 2, VertexAttribPointerType.Float, false, bstride, new IntPtr(21 * sizeof(float)));
            gl.EnableVertexAttribArray(9); gl.VertexAttribPointer(9, 1, VertexAttribPointerType.Float, false, bstride, new IntPtr(23 * sizeof(float)));
            gl.EnableVertexAttribArray(10); gl.VertexAttribPointer(10, 1, VertexAttribPointerType.Float, false, bstride, new IntPtr(24 * sizeof(float)));
            gl.EnableVertexAttribArray(11); gl.VertexAttribPointer(11, 4, VertexAttribPointerType.Float, false, bstride, new IntPtr(25 * sizeof(float)));
            gl.EnableVertexAttribArray(12); gl.VertexAttribPointer(12, 2, VertexAttribPointerType.Float, false, bstride, new IntPtr(29 * sizeof(float)));
            gl.EnableVertexAttribArray(13); gl.VertexAttribPointer(13, 2, VertexAttribPointerType.Float, false, bstride, new IntPtr(31 * sizeof(float)));
            gl.EnableVertexAttribArray(14); gl.VertexAttribPointer(14, 1, VertexAttribPointerType.Float, false, bstride, new IntPtr(33 * sizeof(float)));
            gl.EnableVertexAttribArray(15); gl.VertexAttribPointer(15, 2, VertexAttribPointerType.Float, false, bstride, new IntPtr(34 * sizeof(float)));

            gl.VertexAttribDivisor(1, 1);
            gl.VertexAttribDivisor(2, 1);
            gl.VertexAttribDivisor(3, 1);
            gl.VertexAttribDivisor(4, 1);
            gl.VertexAttribDivisor(5, 1);
            gl.VertexAttribDivisor(6, 1);
            gl.VertexAttribDivisor(7, 1);
            gl.VertexAttribDivisor(8, 1);
            gl.VertexAttribDivisor(9, 1);
            gl.VertexAttribDivisor(10, 1);
            gl.VertexAttribDivisor(11, 1);
            gl.VertexAttribDivisor(12, 1);
            gl.VertexAttribDivisor(13, 1);
            gl.VertexAttribDivisor(14, 1);
            gl.VertexAttribDivisor(15, 1);

            gl.BindVertexArray(0);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

            _textures = new VfxTextureResourceCache(gl);
            _capture = new VfxSceneCapture(gl);
            _meshResources = new VfxMeshResourceCache(gl);
            _ready = true;
        }

        public uint UploadTexture(byte[] bgra, int width, int height)
            => _textures.Upload(bgra, width, height);

        public void CaptureScene(uint width, uint height, bool captureColor, bool captureDepth)
            => _capture.Capture(width, height, captureColor, captureDepth);

        public void Render(IReadOnlyList<VfxRenderQueueEntry> renderQueue, Matrix4x4 viewProj, Matrix4x4 view)
        {
            if (!_ready || renderQueue is null || renderQueue.Count == 0) return;

            Matrix4x4.Invert(view, out var inv);
            var camRight = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, inv));
            var camUp = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, inv));

            bool depthTest = _gl.IsEnabled(EnableCap.DepthTest);
            bool cullFace = _gl.IsEnabled(EnableCap.CullFace);
            bool polygonOffset = _gl.IsEnabled(EnableCap.PolygonOffsetFill);
            bool blend = _gl.IsEnabled(EnableCap.Blend);
            _gl.GetInteger(GLEnum.DepthWritemask, out int depthWrite);
            _gl.GetInteger(GLEnum.DepthFunc, out int depthFunction);
            _gl.GetInteger(GLEnum.BlendSrcRgb, out int blendSource);
            _gl.GetInteger(GLEnum.BlendDstRgb, out int blendDestination);
            _gl.GetInteger(GLEnum.BlendSrcAlpha, out int blendSourceAlpha);
            _gl.GetInteger(GLEnum.BlendDstAlpha, out int blendDestinationAlpha);
            _gl.GetInteger(GLEnum.BlendEquationRgb, out int blendEquation);
            _gl.GetInteger(GLEnum.BlendEquationAlpha, out int blendEquationAlpha);
            Span<int> colorWriteMask = stackalloc int[4];
            _gl.GetInteger(GLEnum.ColorWritemask, colorWriteMask);
            _gl.GetInteger(GLEnum.CurrentProgram, out int program);
            _gl.GetInteger(GLEnum.VertexArrayBinding, out int vertexArray);
            _gl.GetInteger(GLEnum.ArrayBufferBinding, out int arrayBuffer);
            _gl.GetInteger(GLEnum.ActiveTexture, out int activeTexture);
            var textureBindings = new int[9];
            for (int unit = 0; unit < textureBindings.Length; unit++)
            {
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
                _gl.GetInteger(GLEnum.TextureBinding2D, out textureBindings[unit]);
            }

            try
            {
            _gl.UseProgram(_program);
            _gl.UniformMatrix4(_uViewProj, 1, false, in viewProj.M11);
            _gl.Uniform3(_uCamRight, camRight.X, camRight.Y, camRight.Z);
            _gl.Uniform3(_uCamUp, camUp.X, camUp.Y, camUp.Z);
            _gl.Uniform1(_uTex, 0);
            _gl.Uniform1(_uTexMult, 1);
            _gl.Uniform1(_uColorMap, 7);
            _gl.Uniform1(_uPaletteMap, 8);
            _gl.Uniform1(_uSceneTex, 2);
            _gl.Uniform1(_uDistortionTex, 3);
            _gl.Uniform1(_uErosionTex, 4);
            _gl.Uniform1(_uReflectionTex, 5);
            _gl.Uniform1(_uSceneDepthTex, 6);
            _gl.Uniform2(_uViewportSize, _capture.Width, _capture.Height);

            _gl.BindVertexArray(_vao);
            _gl.ActiveTexture(TextureUnit.Texture0);

            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthMask(false);
            _gl.Disable(EnableCap.CullFace);
            _gl.Disable(EnableCap.PolygonOffsetFill);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendEquation(GLEnum.FuncAdd);

            foreach (VfxRenderQueueEntry entry in renderQueue)
            {
                VfxPlaybackRuntime.EmitterState es = entry.Emitter;
                if (es.InstanceCount == 0) continue;
                if (!es.IsVisible) continue;
                VfxEmitterRenderState emitterRenderState = es.Def.RenderState ?? VfxEmitterRenderState.Default;
                ApplyColorWriteMask(emitterRenderState);
                // Never synthesize an AttachedMesh proxy. Render only geometry that was
                // resolved from the real owner scene and filtered by authored submesh masks.
                if (es.Def.PrimitiveKind == VfxPrimitiveKind.AttachedMesh && es.MeshVao == 0)
                    continue;
                if (es.Def.IsMeshPrimitive && es.MeshVao != 0)
                {
                    ApplyEmitterDepthState(es.Def, isDistortion: false);
                    RenderMeshEmitter(es, viewProj);
                    continue;
                }
                if (!es.Def.IsVisual) continue;
                bool isDistortion = es.Def.Distortion is not null;
                if (isDistortion && (es.DistortionTexture == 0 || _capture.ColorTexture == 0)) continue;

                int floats = es.InstanceCount * Stride;
                _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instVbo);

                var instancesSpan = new ReadOnlySpan<float>(es.Instances, 0, floats);
                if (floats > _instCapFloats)
                {
                    _gl.BufferData(BufferTargetARB.ArrayBuffer, instancesSpan, BufferUsageARB.DynamicDraw);
                    _instCapFloats = floats;
                }
                else
                {
                    _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, instancesSpan);
                }

                ApplyBlendMode(es.Def.BlendMode, isDistortion);

                _gl.Uniform2(_uTexDiv, es.Def.TexDiv.X <= 0 ? 1f : es.Def.TexDiv.X, es.Def.TexDiv.Y <= 0 ? 1f : es.Def.TexDiv.Y);
                _gl.Uniform2(_uTexSize, Math.Max(1f, es.TextureWidth), Math.Max(1f, es.TextureHeight));
                Vector2 emitterUvOffset = es.Def.EmitterUvScrollRate * es.EmitterAge;
                _gl.Uniform2(_uEmitterUvOffset, emitterUvOffset.X, emitterUvOffset.Y);
                Vector2 uvCenter = EffectiveCenter(es.Def.UvTransformCenter);
                _gl.Uniform2(_uUvTransformCenter, uvCenter.X, uvCenter.Y);
                _gl.Uniform1(_uHasTexMult, es.TextureMult != 0 ? 1 : 0);
                var multDiv = es.Def.TextureMultTexDiv;
                _gl.Uniform2(_uTexDivMult, multDiv.X <= 0 ? 1f : multDiv.X, multDiv.Y <= 0 ? 1f : multDiv.Y);
                _gl.Uniform2(
                    _uTexSizeMult,
                    Math.Max(1f, es.TextureMultWidth),
                    Math.Max(1f, es.TextureMultHeight));
                Vector2 emitterUvOffsetMult = es.Def.TextureMultEmitterUvScrollRate * es.EmitterAge;
                _gl.Uniform2(_uUvScrollRateMult, emitterUvOffsetMult.X, emitterUvOffsetMult.Y);
                Vector2 uvCenterMult = EffectiveCenter(es.Def.TextureMultTransformCenter);
                _gl.Uniform2(_uUvTransformCenterMult, uvCenterMult.X, uvCenterMult.Y);
                _gl.Uniform1(_uFlipUMult, es.Def.TextureMultFlipU ? 1 : 0);
                _gl.Uniform1(_uFlipVMult, es.Def.TextureMultFlipV ? 1 : 0);
                _gl.Uniform1(_uClampUvMult, es.Def.TextureMultClampUvScroll ? 1 : 0);
                bool directional = es.Def.IsDirectionOriented || es.Def.PrimitiveKind is
                    VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail or VfxPrimitiveKind.Ray or VfxPrimitiveKind.Beam;
                bool arbitrary = es.Def.IsArbitraryQuad || es.Def.IsLocalOrientation || es.Def.ParticleIsLocalOrientation || es.Def.PrimitiveKind is
                    VfxPrimitiveKind.ArbitraryTrail or VfxPrimitiveKind.PlanarProjection;
                _gl.Uniform1(_uDirectionOriented, directional ? 1 : 0);
                _gl.Uniform1(_uArbitraryQuad, arbitrary ? 1 : 0);
                bool groundLayer = es.Def.IsGroundLayer ||
                    es.Def.IsFollowingTerrain ||
                    es.Def.PrimitiveKind == VfxPrimitiveKind.PlanarProjection ||
                    IsGroundLikeBirthRotation(es.Def.BirthRotation);
                _gl.Uniform1(_uIsGroundLayer, groundLayer ? 1 : 0);
                _gl.Uniform1(_uPrimitiveKind, (int)es.Def.PrimitiveKind);
                var renderState = es.Def.RenderState ?? VfxEmitterRenderState.Default;
                ApplyEmitterDepthState(es.Def, isDistortion);
                _gl.Uniform1(_uAlphaCutoff, renderState.AlphaCutoff);
                _gl.Uniform1(
                    _uAlphaTest,
                    VfxBlendModes.ShouldAlphaTest(es.Def.BlendMode, renderState.AlphaReference) ? 1 : 0);
                _gl.Uniform1(_uDeriveAlphaFromRgb, es.DeriveAlphaFromRgb ? 1 : 0);
                _gl.Uniform1(_uEmissiveStrength, VfxBlendModes.ResolveEmissiveStrength(es.Def.BlendMode));
                _gl.Uniform1(
                    _uIsMultiply,
                    !isDistortion && VfxBlendModes.GetDescriptor(es.Def.BlendMode).NeutralizeTransparentRgb ? 1 : 0);
                Vector4 modulationFactor = es.Def.ModulationFactor ?? Vector4.One;
                _gl.Uniform4(
                    _uModulationFactor,
                    modulationFactor.X,
                    modulationFactor.Y,
                    modulationFactor.Z,
                    modulationFactor.W);
                _gl.Uniform1(_uHasColor, es.ColorGradientTexture != 0 ? 1 : 0);
                _gl.Uniform1(
                    _uColorRenderFlags,
                    VfxBlendModes.ResolveColorRenderFlags(
                        es.Def.ColorRenderFlags,
                        !string.IsNullOrWhiteSpace(es.Def.ParticleColorTexturePath)));
                VfxPaletteDefinition palette = es.Def.PaletteDefinition;
                _gl.Uniform1(_uHasPalette, es.PaletteTexture != 0 ? 1 : 0);
                _gl.Uniform1(_uPaletteCount, Math.Max(1, palette?.PaletteCount ?? 1));
                Vector4 paletteMask = palette?.PaletteSourceMixColor ?? Vector4.UnitX;
                _gl.Uniform4(_uPaletteMixMask, paletteMask.X, paletteMask.Y, paletteMask.Z, paletteMask.W);
                _gl.Uniform1(_uIsAdditive, VfxBlendModes.IsAdditive(es.Def.BlendMode) ? 1 : 0);
                _gl.Uniform1(_uColorLookUpTypeX, es.Def.ColorLookUpTypeX ?? 0);
                _gl.Uniform1(_uColorLookUpTypeY, es.Def.ColorLookUpTypeY ?? 0);
                Vector2 colorLookUpScales = es.Def.ColorLookUpScales == Vector2.Zero
                    ? Vector2.One
                    : es.Def.ColorLookUpScales;
                _gl.Uniform2(_uColorLookUpScales, colorLookUpScales.X, colorLookUpScales.Y);
                _gl.Uniform2(_uColorLookUpOffsets, es.Def.ColorLookUpOffsets.X, es.Def.ColorLookUpOffsets.Y);
                _gl.Uniform1(_uFlipU, renderState.FlipU ? 1 : 0);
                _gl.Uniform1(_uFlipV, renderState.FlipV ? 1 : 0);
                _gl.Uniform1(_uClampUv, renderState.ClampUvScroll ? 1 : 0);
                _gl.Uniform1(_uAddressMode, renderState.TextureAddressMode);
                _gl.Uniform1(_uAddressModeMult, es.Def.TextureMultAddressMode);
                _gl.Uniform1(_uIsDistortion, isDistortion ? 1 : 0);
                _gl.Uniform1(_uDistortionStrength, es.Def.Distortion?.Strength ?? 0f);
                _gl.Uniform1(_uHasErosion, es.ErosionTexture != 0 ? 1 : 0);
                _gl.Uniform1(_uErosionFeatherIn, es.Def.AlphaErosion?.FeatherIn ?? 0f);
                _gl.Uniform1(_uErosionFeatherOut, es.Def.AlphaErosion?.FeatherOut ?? 0f);
                _gl.Uniform1(_uHasSoftParticle, ShouldUseSoftParticles(es.Def, _capture.DepthTexture != 0) ? 1 : 0);
                VfxSoftParticleDefinition soft = es.Def.SoftParticle;
                _gl.Uniform4(
                    _uSoftParticleParams,
                    soft?.BeginIn ?? 0f,
                    soft?.DeltaIn ?? 0f,
                    soft?.BeginOut ?? 0f,
                    soft?.DeltaOut ?? 0f);
                _gl.Uniform1(_uHasReflection, es.ReflectionTexture != 0 ? 1 : 0);
                VfxReflectionDefinition reflection = es.Def.Reflection;
                _gl.Uniform2(
                    _uReflectionOpacity,
                    reflection?.DirectOpacity ?? 0f,
                    reflection?.GlancingOpacity ?? 0f);
                Vector4 reflectionColor = reflection?.ReflectionFresnelColor ?? Vector4.One;
                _gl.Uniform4(
                    _uReflectionColor,
                    reflectionColor.X,
                    reflectionColor.Y,
                    reflectionColor.Z,
                    reflectionColor.W);
                _gl.Uniform3(_uPlacementRight, es.PlacementRight.X, es.PlacementRight.Y, es.PlacementRight.Z);
                _gl.Uniform3(_uPlacementUp, es.PlacementUp.X, es.PlacementUp.Y, es.PlacementUp.Z);
                _gl.Uniform3(_uPlacementForward, es.PlacementForward.X, es.PlacementForward.Y, es.PlacementForward.Z);
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.Texture2D, es.Texture != 0 ? es.Texture : _textures.FallbackTransparentTexture);
                ApplyAddressMode(renderState.TextureAddressMode);
                ApplyTextureSampling(es.Def.IsTexturePixelated);
                if (es.TextureMult != 0)
                {
                    _gl.ActiveTexture(TextureUnit.Texture1);
                    _gl.BindTexture(TextureTarget.Texture2D, es.TextureMult);
                    ApplyAddressMode(es.Def.TextureMultAddressMode);
                    _gl.ActiveTexture(TextureUnit.Texture0);
                }
                if (_capture.ColorTexture != 0)
                {
                    _gl.ActiveTexture(TextureUnit.Texture2);
                    _gl.BindTexture(TextureTarget.Texture2D, _capture.ColorTexture);
                    _gl.ActiveTexture(TextureUnit.Texture0);
                }
                if (es.DistortionTexture != 0)
                {
                    _gl.ActiveTexture(TextureUnit.Texture3);
                    _gl.BindTexture(TextureTarget.Texture2D, es.DistortionTexture);
                    ApplyAddressMode(renderState.TextureAddressMode);
                    _gl.ActiveTexture(TextureUnit.Texture0);
                }
                if (es.ErosionTexture != 0)
                {
                    _gl.ActiveTexture(TextureUnit.Texture4);
                    _gl.BindTexture(TextureTarget.Texture2D, es.ErosionTexture);
                    ApplyAddressMode(es.Def.AlphaErosion?.AddressMode ?? renderState.TextureAddressMode);
                    _gl.ActiveTexture(TextureUnit.Texture0);
                }
                if (es.ReflectionTexture != 0)
                {
                    _gl.ActiveTexture(TextureUnit.Texture5);
                    _gl.BindTexture(TextureTarget.Texture2D, es.ReflectionTexture);
                    ApplyAddressMode(renderState.TextureAddressMode);
                    _gl.ActiveTexture(TextureUnit.Texture0);
                }
                if (_capture.DepthTexture != 0)
                {
                    _gl.ActiveTexture(TextureUnit.Texture6);
                    _gl.BindTexture(TextureTarget.Texture2D, _capture.DepthTexture);
                    _gl.ActiveTexture(TextureUnit.Texture0);
                }
                _gl.ActiveTexture(TextureUnit.Texture7);
                _gl.BindTexture(TextureTarget.Texture2D, es.ColorGradientTexture != 0
                    ? es.ColorGradientTexture
                    : _textures.FallbackTransparentTexture);
                ApplyAddressMode(1);
                ApplyTextureSampling(false);
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + 8));
                _gl.BindTexture(TextureTarget.Texture2D, es.PaletteTexture != 0
                    ? es.PaletteTexture
                    : _textures.FallbackTransparentTexture);
                ApplyAddressMode(1);
                ApplyTextureSampling(false);
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.DrawArraysInstanced(PrimitiveType.TriangleFan, 0, 4, (uint)es.InstanceCount);
            }

            }
            finally
            {
                _gl.DepthMask(depthWrite != 0);
                _gl.DepthFunc((DepthFunction)depthFunction);
                _gl.BlendEquationSeparate((GLEnum)blendEquation, (GLEnum)blendEquationAlpha);
                _gl.BlendFuncSeparate(
                    (BlendingFactor)blendSource,
                    (BlendingFactor)blendDestination,
                    (BlendingFactor)blendSourceAlpha,
                    (BlendingFactor)blendDestinationAlpha);
                _gl.ColorMask(
                    colorWriteMask[0] != 0,
                    colorWriteMask[1] != 0,
                    colorWriteMask[2] != 0,
                    colorWriteMask[3] != 0);
                if (depthTest) _gl.Enable(EnableCap.DepthTest); else _gl.Disable(EnableCap.DepthTest);
                if (cullFace) _gl.Enable(EnableCap.CullFace); else _gl.Disable(EnableCap.CullFace);
                if (polygonOffset) _gl.Enable(EnableCap.PolygonOffsetFill); else _gl.Disable(EnableCap.PolygonOffsetFill);
                if (blend) _gl.Enable(EnableCap.Blend); else _gl.Disable(EnableCap.Blend);
                for (int unit = 0; unit < textureBindings.Length; unit++)
                {
                    _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
                    _gl.BindTexture(TextureTarget.Texture2D, (uint)textureBindings[unit]);
                }
                _gl.ActiveTexture((TextureUnit)activeTexture);
                _gl.BindVertexArray((uint)vertexArray);
                _gl.BindBuffer(BufferTargetARB.ArrayBuffer, (uint)arrayBuffer);
                _gl.UseProgram((uint)program);
            }
        }

        private void ApplyAddressMode(int addressMode)
        {
            var wrap = addressMode switch
            {
                1 => TextureWrapMode.ClampToEdge,
                2 => TextureWrapMode.MirroredRepeat,
                3 => TextureWrapMode.ClampToBorder,
                _ => TextureWrapMode.Repeat,
            };
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrap);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrap);
        }

        private void ApplyEmitterDepthState(VfxEmitterDefinition definition, bool isDistortion)
        {
            var renderState = definition.RenderState ?? VfxEmitterRenderState.Default;
            bool writeDepth = !isDistortion && VfxBlendModes.ShouldWriteDepth(
                definition.BlendMode,
                renderState.AlphaReference);
            _gl.DepthMask(writeDepth);

            if (definition.DepthBiasFactors is { } bias)
            {
                _gl.Enable(EnableCap.PolygonOffsetFill);
                _gl.PolygonOffset(bias.X, bias.Y);
            }
            else
            {
                _gl.Disable(EnableCap.PolygonOffsetFill);
            }
        }

        private void ApplyTextureSampling(bool pixelated)
        {
            _gl.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)(pixelated ? TextureMinFilter.Nearest : TextureMinFilter.LinearMipmapLinear));
            _gl.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)(pixelated ? TextureMagFilter.Nearest : TextureMagFilter.Linear));
        }

        private void ApplyBlendMode(int blendMode, bool distortion = false)
        {
            VfxBlendModeDescriptor descriptor = VfxBlendModes.GetDescriptor(
                distortion ? VfxAuthoredDefaults.BlendMode : blendMode);
            _gl.BlendEquationSeparate(
                ToOpenGl(descriptor.RgbEquation),
                ToOpenGl(descriptor.AlphaEquation));
            _gl.BlendFuncSeparate(
                ToOpenGl(descriptor.SourceRgb),
                ToOpenGl(descriptor.DestinationRgb),
                ToOpenGl(descriptor.SourceAlpha),
                ToOpenGl(descriptor.DestinationAlpha));
        }

        private void ApplyColorWriteMask(VfxEmitterRenderState renderState)
        {
            bool writeColor = !renderState.WriteAlphaOnly;
            _gl.ColorMask(writeColor, writeColor, writeColor, true);
        }

        private static BlendingFactor ToOpenGl(VfxBlendFactor factor) => factor switch
        {
            VfxBlendFactor.Zero => BlendingFactor.Zero,
            VfxBlendFactor.One => BlendingFactor.One,
            VfxBlendFactor.SourceAlpha => BlendingFactor.SrcAlpha,
            VfxBlendFactor.OneMinusSourceAlpha => BlendingFactor.OneMinusSrcAlpha,
            VfxBlendFactor.DestinationColor => BlendingFactor.DstColor,
            _ => throw new ArgumentOutOfRangeException(nameof(factor), factor, null)
        };

        private static GLEnum ToOpenGl(VfxBlendEquationKind equation) => equation switch
        {
            VfxBlendEquationKind.Add => GLEnum.FuncAdd,
            _ => throw new ArgumentOutOfRangeException(nameof(equation), equation, null)
        };

        private static Vector2 EffectiveCenter(Vector2 center)
            => center == Vector2.Zero ? new Vector2(0.5f, 0.5f) : center;

        public void ClearTextures()
        {
            if (!_ready) return;
            _textures.Clear();
            ReleaseMeshes();
        }

        public void Dispose()
        {
            if (!_ready) return;
            _textures.Dispose();
            _gl.DeleteBuffer(_quadVbo);
            _gl.DeleteBuffer(_instVbo);
            _gl.DeleteVertexArray(_vao);
            _gl.DeleteProgram(_program);
            if (_meshProgram != 0) _gl.DeleteProgram(_meshProgram);
            _meshProgram = 0;
            _meshResources.Dispose();
            _capture.Dispose();
            _ready = false;
        }

        private uint _meshProgram;
        private int _muViewProj, _muWorldPos, _muScale, _muRotation, _muColor, _muTex, _muEmitterUvOffset;
        private int _muTexDiv, _muTexSize, _muFrame, _muAddressMode, _muClampUv, _muUvTransformCenter;
        private int _muTexMult, _muHasTexMult, _muTexDivMult, _muTexSizeMult, _muUvOffsetMult, _muUvScaleMult, _muUvRotationMult;
        private int _muTextureMultFrame, _muEmitterUvOffsetMult, _muFlipUMult, _muFlipVMult;
        private int _muAddressModeMult, _muClampUvMult, _muUvTransformCenterMult;
        private int _muPlacementRight, _muPlacementUp, _muPlacementForward;
        private int _muAlphaCutoff, _muAlphaTest, _muDeriveAlphaFromRgb, _muEmissiveStrength, _muIsMultiply, _muColorMap, _muHasColor, _muColorRenderFlags, _muIsAdditive, _muModulationFactor, _muColorLookUpTypeX, _muColorLookUpTypeY, _muColorLookUpScales, _muColorLookUpOffsets, _muFlipU, _muFlipV;
        private int _muPaletteMap, _muHasPalette, _muPaletteCount, _muPaletteSelector, _muPaletteMixMask;
        private int _muBirthUvOffset, _muUvScale, _muUvRotation;
        private int _muErosionTex, _muHasErosion, _muErosionDrive, _muErosionFeatherIn, _muErosionFeatherOut, _muErosionMixer;
        private int _muReflectionTex, _muHasReflection, _muReflectionOpacity, _muReflectionColor;
        private int _muSceneDepthTex, _muHasSoftParticle, _muSoftParticleParams, _muViewportSize;

        private void EnsureMeshProgram()
        {
            if (_meshProgram == 0)
            {
                _meshProgram = GlShaderCompiler.CreateProgram(_gl, _gles, VfxShaderSource.MeshVertex, VfxShaderSource.MeshFragment);
                _muViewProj = _gl.GetUniformLocation(_meshProgram, "uViewProj");
                _muWorldPos = _gl.GetUniformLocation(_meshProgram, "uWorldPos");
                _muScale = _gl.GetUniformLocation(_meshProgram, "uScale");
                _muRotation = _gl.GetUniformLocation(_meshProgram, "uRotation");
                _muColor = _gl.GetUniformLocation(_meshProgram, "uColor");
                _muTex = _gl.GetUniformLocation(_meshProgram, "uTex");
                _muEmitterUvOffset = _gl.GetUniformLocation(_meshProgram, "uEmitterUvOffset");
                _muTexDiv = _gl.GetUniformLocation(_meshProgram, "uTexDiv");
                _muTexSize = _gl.GetUniformLocation(_meshProgram, "uTexSize");
                _muFrame = _gl.GetUniformLocation(_meshProgram, "uFrame");
                _muAddressMode = _gl.GetUniformLocation(_meshProgram, "uAddressMode");
                _muClampUv = _gl.GetUniformLocation(_meshProgram, "uClampUv");
                _muUvTransformCenter = _gl.GetUniformLocation(_meshProgram, "uUvTransformCenter");
                _muTexMult = _gl.GetUniformLocation(_meshProgram, "uTexMult");
                _muHasTexMult = _gl.GetUniformLocation(_meshProgram, "uHasTexMult");
                _muTexDivMult = _gl.GetUniformLocation(_meshProgram, "uTexDivMult");
                _muTexSizeMult = _gl.GetUniformLocation(_meshProgram, "uTexSizeMult");
                _muUvOffsetMult = _gl.GetUniformLocation(_meshProgram, "uUvOffsetMult");
                _muUvScaleMult = _gl.GetUniformLocation(_meshProgram, "uUvScaleMult");
                _muUvRotationMult = _gl.GetUniformLocation(_meshProgram, "uUvRotationMult");
                _muTextureMultFrame = _gl.GetUniformLocation(_meshProgram, "uTextureMultFrame");
                _muEmitterUvOffsetMult = _gl.GetUniformLocation(_meshProgram, "uEmitterUvOffsetMult");
                _muFlipUMult = _gl.GetUniformLocation(_meshProgram, "uFlipUMult");
                _muFlipVMult = _gl.GetUniformLocation(_meshProgram, "uFlipVMult");
                _muAddressModeMult = _gl.GetUniformLocation(_meshProgram, "uAddressModeMult");
                _muClampUvMult = _gl.GetUniformLocation(_meshProgram, "uClampUvMult");
                _muUvTransformCenterMult = _gl.GetUniformLocation(_meshProgram, "uUvTransformCenterMult");
                _muPlacementRight = _gl.GetUniformLocation(_meshProgram, "uPlacementRight");
                _muPlacementUp = _gl.GetUniformLocation(_meshProgram, "uPlacementUp");
                _muPlacementForward = _gl.GetUniformLocation(_meshProgram, "uPlacementForward");
                _muAlphaCutoff = _gl.GetUniformLocation(_meshProgram, "uAlphaCutoff");
                _muAlphaTest = _gl.GetUniformLocation(_meshProgram, "uAlphaTest");
                _muDeriveAlphaFromRgb = _gl.GetUniformLocation(_meshProgram, "uDeriveAlphaFromRgb");
                _muEmissiveStrength = _gl.GetUniformLocation(_meshProgram, "uEmissiveStrength");
                _muIsMultiply = _gl.GetUniformLocation(_meshProgram, "uIsMultiply");
                _muColorMap = _gl.GetUniformLocation(_meshProgram, "uColorMap");
                _muHasColor = _gl.GetUniformLocation(_meshProgram, "uHasColor");
                _muColorRenderFlags = _gl.GetUniformLocation(_meshProgram, "uColorRenderFlags");
                _muIsAdditive = _gl.GetUniformLocation(_meshProgram, "uIsAdditive");
                _muModulationFactor = _gl.GetUniformLocation(_meshProgram, "uModulationFactor");
                _muPaletteMap = _gl.GetUniformLocation(_meshProgram, "uPaletteMap");
                _muHasPalette = _gl.GetUniformLocation(_meshProgram, "uHasPalette");
                _muPaletteCount = _gl.GetUniformLocation(_meshProgram, "uPaletteCount");
                _muPaletteSelector = _gl.GetUniformLocation(_meshProgram, "uPaletteSelector");
                _muPaletteMixMask = _gl.GetUniformLocation(_meshProgram, "uPaletteMixMask");
                _muColorLookUpTypeX = _gl.GetUniformLocation(_meshProgram, "uColorLookUpTypeX");
                _muColorLookUpTypeY = _gl.GetUniformLocation(_meshProgram, "uColorLookUpTypeY");
                _muColorLookUpScales = _gl.GetUniformLocation(_meshProgram, "uColorLookUpScales");
                _muColorLookUpOffsets = _gl.GetUniformLocation(_meshProgram, "uColorLookUpOffsets");
                _muFlipU = _gl.GetUniformLocation(_meshProgram, "uFlipU");
                _muFlipV = _gl.GetUniformLocation(_meshProgram, "uFlipV");
                _muBirthUvOffset = _gl.GetUniformLocation(_meshProgram, "uBirthUvOffset");
                _muUvScale = _gl.GetUniformLocation(_meshProgram, "uUvScale");
                _muUvRotation = _gl.GetUniformLocation(_meshProgram, "uUvRotation");
                _muErosionTex = _gl.GetUniformLocation(_meshProgram, "uErosionTex");
                _muHasErosion = _gl.GetUniformLocation(_meshProgram, "uHasErosion");
                _muErosionDrive = _gl.GetUniformLocation(_meshProgram, "uErosionDrive");
                _muErosionFeatherIn = _gl.GetUniformLocation(_meshProgram, "uErosionFeatherIn");
                _muErosionFeatherOut = _gl.GetUniformLocation(_meshProgram, "uErosionFeatherOut");
                _muErosionMixer = _gl.GetUniformLocation(_meshProgram, "uErosionMixer");
                _muReflectionTex = _gl.GetUniformLocation(_meshProgram, "uReflectionTex");
                _muHasReflection = _gl.GetUniformLocation(_meshProgram, "uHasReflection");
                _muReflectionOpacity = _gl.GetUniformLocation(_meshProgram, "uReflectionOpacity");
                _muReflectionColor = _gl.GetUniformLocation(_meshProgram, "uReflectionColor");
                _muSceneDepthTex = _gl.GetUniformLocation(_meshProgram, "uSceneDepthTex");
                _muHasSoftParticle = _gl.GetUniformLocation(_meshProgram, "uHasSoftParticle");
                _muSoftParticleParams = _gl.GetUniformLocation(_meshProgram, "uSoftParticleParams");
                _muViewportSize = _gl.GetUniformLocation(_meshProgram, "uViewportSize");
            }
        }

        public void UploadEmitterMesh(
            VfxPlaybackRuntime.EmitterState es,
            float[] positions,
            float[] uvs,
            float[] colors,
            uint[] indices = null)
        {
            if (!_ready) return;
            EnsureMeshProgram();
            _meshResources.Upload(es, positions, uvs, colors, indices);
        }

        private void ReleaseMeshes()
            => _meshResources.Clear();

        private void UpdateEmitterMeshPositions(VfxPlaybackRuntime.EmitterState es, float[] positions)
        {
            if (_ready)
                _meshResources.UpdatePositions(es, positions);
        }

        private void RenderMeshEmitter(VfxPlaybackRuntime.EmitterState es, Matrix4x4 viewProj)
        {
            if (es.MeshVao == 0 || es.MeshVertexCount == 0) return;
            if (es.MeshAnimation != null)
                UpdateEmitterMeshPositions(es, es.MeshAnimation.Evaluate(es.EmitterAge));
            bool cullFace = _gl.IsEnabled(EnableCap.CullFace);
            EnsureMeshProgram();
            _gl.UseProgram(_meshProgram);
            _gl.BindVertexArray(es.MeshVao);
            _gl.UniformMatrix4(_muViewProj, 1, false, in viewProj.M11);
            _gl.Uniform1(_muTex, 0);
            _gl.Uniform1(_muTexMult, 1);
            _gl.Uniform1(_muColorMap, 7);
            _gl.Uniform1(_muPaletteMap, 8);
            _gl.Uniform1(_muErosionTex, 4);
            _gl.Uniform1(_muReflectionTex, 5);
            _gl.Uniform1(_muSceneDepthTex, 6);
            Vector2 texDiv = es.Def.TexDiv;
            _gl.Uniform2(_muTexDiv, texDiv.X <= 0f ? 1f : texDiv.X, texDiv.Y <= 0f ? 1f : texDiv.Y);
            _gl.Uniform2(_muTexSize, Math.Max(1f, es.TextureWidth), Math.Max(1f, es.TextureHeight));
            Vector2 uvCenter = EffectiveCenter(es.Def.UvTransformCenter);
            _gl.Uniform2(_muUvTransformCenter, uvCenter.X, uvCenter.Y);
            _gl.Uniform1(_muHasTexMult, es.TextureMult != 0 ? 1 : 0);
            Vector2 textureMultTexDiv = es.Def.TextureMultTexDiv;
            _gl.Uniform2(
                _muTexDivMult,
                textureMultTexDiv.X <= 0f ? 1f : textureMultTexDiv.X,
                textureMultTexDiv.Y <= 0f ? 1f : textureMultTexDiv.Y);
            _gl.Uniform2(
                _muTexSizeMult,
                Math.Max(1f, es.TextureMultWidth),
                Math.Max(1f, es.TextureMultHeight));
            Vector2 uvCenterMult = EffectiveCenter(es.Def.TextureMultTransformCenter);
            _gl.Uniform2(_muUvTransformCenterMult, uvCenterMult.X, uvCenterMult.Y);
            Vector2 emitterUvOffsetMult = es.Def.TextureMultEmitterUvScrollRate * es.EmitterAge;
            _gl.Uniform2(_muEmitterUvOffsetMult, emitterUvOffsetMult.X, emitterUvOffsetMult.Y);
            _gl.Uniform1(_muFlipUMult, es.Def.TextureMultFlipU ? 1 : 0);
            _gl.Uniform1(_muFlipVMult, es.Def.TextureMultFlipV ? 1 : 0);
            _gl.Uniform1(_muAddressModeMult, es.Def.TextureMultAddressMode);
            _gl.Uniform1(_muClampUvMult, es.Def.TextureMultClampUvScroll ? 1 : 0);
            _gl.Uniform1(_muHasErosion, es.ErosionTexture != 0 ? 1 : 0);
            _gl.Uniform1(_muErosionFeatherIn, es.Def.AlphaErosion?.FeatherIn ?? 0f);
            _gl.Uniform1(_muErosionFeatherOut, es.Def.AlphaErosion?.FeatherOut ?? 0f);
            _gl.Uniform3(_muPlacementRight, es.PlacementRight.X, es.PlacementRight.Y, es.PlacementRight.Z);
            _gl.Uniform3(_muPlacementUp, es.PlacementUp.X, es.PlacementUp.Y, es.PlacementUp.Z);
            _gl.Uniform3(_muPlacementForward, es.PlacementForward.X, es.PlacementForward.Y, es.PlacementForward.Z);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, es.Texture != 0 ? es.Texture : _textures.FallbackTransparentTexture);
            var renderState = es.Def.RenderState ?? VfxEmitterRenderState.Default;
            ApplyAddressMode(renderState.TextureAddressMode);
            _gl.Uniform1(_muAlphaCutoff, renderState.AlphaCutoff);
            _gl.Uniform1(
                _muAlphaTest,
                VfxBlendModes.ShouldAlphaTest(es.Def.BlendMode, renderState.AlphaReference) ? 1 : 0);
            _gl.Uniform1(_muDeriveAlphaFromRgb, es.DeriveAlphaFromRgb ? 1 : 0);
            _gl.Uniform1(_muEmissiveStrength, VfxBlendModes.ResolveEmissiveStrength(es.Def.BlendMode));
            _gl.Uniform1(
                _muIsMultiply,
                VfxBlendModes.GetDescriptor(es.Def.BlendMode).NeutralizeTransparentRgb ? 1 : 0);
            Vector4 meshModulationFactor = es.Def.ModulationFactor ?? Vector4.One;
            _gl.Uniform4(
                _muModulationFactor,
                meshModulationFactor.X,
                meshModulationFactor.Y,
                meshModulationFactor.Z,
                meshModulationFactor.W);
            _gl.Uniform1(_muHasColor, es.ColorGradientTexture != 0 ? 1 : 0);
            _gl.Uniform1(
                _muColorRenderFlags,
                VfxBlendModes.ResolveColorRenderFlags(
                    es.Def.ColorRenderFlags,
                    !string.IsNullOrWhiteSpace(es.Def.ParticleColorTexturePath)));
            VfxPaletteDefinition meshPalette = es.Def.PaletteDefinition;
            _gl.Uniform1(_muHasPalette, es.PaletteTexture != 0 ? 1 : 0);
            _gl.Uniform1(_muPaletteCount, Math.Max(1, meshPalette?.PaletteCount ?? 1));
            Vector4 meshPaletteMask = meshPalette?.PaletteSourceMixColor ?? Vector4.UnitX;
            _gl.Uniform4(_muPaletteMixMask, meshPaletteMask.X, meshPaletteMask.Y, meshPaletteMask.Z, meshPaletteMask.W);
            _gl.Uniform1(_muIsAdditive, VfxBlendModes.IsAdditive(es.Def.BlendMode) ? 1 : 0);
            _gl.Uniform1(_muColorLookUpTypeX, es.Def.ColorLookUpTypeX ?? 0);
            _gl.Uniform1(_muColorLookUpTypeY, es.Def.ColorLookUpTypeY ?? 0);
            Vector2 meshColorLookUpScales = es.Def.ColorLookUpScales == Vector2.Zero
                ? Vector2.One
                : es.Def.ColorLookUpScales;
            _gl.Uniform2(_muColorLookUpScales, meshColorLookUpScales.X, meshColorLookUpScales.Y);
            _gl.Uniform2(_muColorLookUpOffsets, es.Def.ColorLookUpOffsets.X, es.Def.ColorLookUpOffsets.Y);
            _gl.Uniform1(_muFlipU, renderState.FlipU ? 1 : 0);
            _gl.Uniform1(_muFlipV, renderState.FlipV ? 1 : 0);
            _gl.Uniform1(_muAddressMode, renderState.TextureAddressMode);
            _gl.Uniform1(_muClampUv, renderState.ClampUvScroll ? 1 : 0);
            ApplyTextureSampling(es.Def.IsTexturePixelated);
            if (es.TextureMult != 0)
            {
                _gl.ActiveTexture(TextureUnit.Texture1);
                _gl.BindTexture(TextureTarget.Texture2D, es.TextureMult);
                ApplyAddressMode(es.Def.TextureMultAddressMode);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            if (es.ErosionTexture != 0)
            {
                _gl.ActiveTexture(TextureUnit.Texture4);
                _gl.BindTexture(TextureTarget.Texture2D, es.ErosionTexture);
                ApplyAddressMode(es.Def.AlphaErosion?.AddressMode ?? renderState.TextureAddressMode);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            _gl.Uniform1(_muHasReflection, es.ReflectionTexture != 0 ? 1 : 0);
            VfxReflectionDefinition reflection = es.Def.Reflection;
            _gl.Uniform2(
                _muReflectionOpacity,
                reflection?.DirectOpacity ?? 0f,
                reflection?.GlancingOpacity ?? 0f);
            Vector4 reflectionColor = reflection?.ReflectionFresnelColor ?? Vector4.One;
            _gl.Uniform4(
                _muReflectionColor,
                reflectionColor.X,
                reflectionColor.Y,
                reflectionColor.Z,
                reflectionColor.W);
            if (es.ReflectionTexture != 0)
            {
                _gl.ActiveTexture(TextureUnit.Texture5);
                _gl.BindTexture(TextureTarget.Texture2D, es.ReflectionTexture);
                ApplyAddressMode(renderState.TextureAddressMode);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            _gl.Uniform1(_muHasSoftParticle, ShouldUseSoftParticles(es.Def, _capture.DepthTexture != 0) ? 1 : 0);
            VfxSoftParticleDefinition soft = es.Def.SoftParticle;
            _gl.Uniform4(
                _muSoftParticleParams,
                soft?.BeginIn ?? 0f,
                soft?.DeltaIn ?? 0f,
                soft?.BeginOut ?? 0f,
                soft?.DeltaOut ?? 0f);
            _gl.Uniform2(_muViewportSize, _capture.Width, _capture.Height);
            if (_capture.DepthTexture != 0)
            {
                _gl.ActiveTexture(TextureUnit.Texture6);
                _gl.BindTexture(TextureTarget.Texture2D, _capture.DepthTexture);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            _gl.ActiveTexture(TextureUnit.Texture7);
            _gl.BindTexture(TextureTarget.Texture2D, es.ColorGradientTexture != 0
                ? es.ColorGradientTexture
                : _textures.FallbackTransparentTexture);
            ApplyAddressMode(1);
            ApplyTextureSampling(false);
            _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + 8));
            _gl.BindTexture(TextureTarget.Texture2D, es.PaletteTexture != 0
                ? es.PaletteTexture
                : _textures.FallbackTransparentTexture);
            ApplyAddressMode(1);
            ApplyTextureSampling(false);
            _gl.ActiveTexture(TextureUnit.Texture0);
            // VFX meshes can be thin or single-sided. Attached owner submeshes also use
            // authored particle material state here, so culling would hide valid surfaces.
            _gl.Disable(EnableCap.CullFace);
            ApplyBlendMode(es.Def.BlendMode);

            Vector2 emitterUvOffset = es.Def.EmitterUvScrollRate * es.EmitterAge;
            _gl.Uniform2(_muEmitterUvOffset, emitterUvOffset.X, emitterUvOffset.Y);
            for (int i = 0; i < es.InstanceCount; i++)
            {
                int o = i * Stride;
                _gl.Uniform3(_muWorldPos, es.Instances[o], es.Instances[o + 1], es.Instances[o + 2]);
                float scaleX = ClampScale(es.Instances[o + 3]);
                float scaleY = ClampScale(es.Instances[o + 4]);
                float scaleZ = ClampScale(es.Instances[o + 18]);
                _gl.Uniform3(_muScale, scaleX, scaleY, scaleZ);
                Vector3 meshRotation = new(
                    es.Instances[o + 15],
                    es.Instances[o + 16],
                    es.Instances[o + 17]);
                _gl.Uniform3(
                    _muRotation,
                    meshRotation.X,
                    meshRotation.Y,
                    meshRotation.Z);
                _gl.Uniform4(_muColor, es.Instances[o + 5], es.Instances[o + 6], es.Instances[o + 7], es.Instances[o + 8]);
                _gl.Uniform2(_muBirthUvOffset, es.Instances[o + 19], es.Instances[o + 20]);
                _gl.Uniform2(_muUvScale, es.Instances[o + 21], es.Instances[o + 22]);
                _gl.Uniform1(_muUvRotation, es.Instances[o + 23]);
                _gl.Uniform1(_muErosionDrive, es.Instances[o + 24]);
                _gl.Uniform4(_muErosionMixer, es.Instances[o + 25], es.Instances[o + 26], es.Instances[o + 27], es.Instances[o + 28]);
                _gl.Uniform2(_muUvOffsetMult, es.Instances[o + 29], es.Instances[o + 30]);
                _gl.Uniform2(_muUvScaleMult, es.Instances[o + 31], es.Instances[o + 32]);
                _gl.Uniform1(_muUvRotationMult, es.Instances[o + 33]);
                _gl.Uniform1(_muFrame, es.Instances[o + 10]);
                _gl.Uniform1(_muTextureMultFrame, es.Instances[o + 34]);
                _gl.Uniform1(_muPaletteSelector, es.Instances[o + 35]);

                if (es.MeshIndexCount > 0)
                {
                    if (_drawElements != null)
                    {
                        _drawElements((uint)PrimitiveType.Triangles, es.MeshIndexCount, (uint)DrawElementsType.UnsignedInt, IntPtr.Zero);
                    }
                }
                else _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)es.MeshVertexCount);
            }
            if (cullFace) _gl.Enable(EnableCap.CullFace);
            else _gl.Disable(EnableCap.CullFace);
            _gl.UseProgram(_program);
            _gl.BindVertexArray(_vao);
        }

        private static float ClampScale(float value)
            => float.IsFinite(value) ? value : 1f;

        internal static bool ShouldUseSoftParticles(VfxEmitterDefinition definition, bool hasSceneDepth)
            => hasSceneDepth &&
               definition?.SoftParticle != null &&
               !definition.IsGroundLayer &&
               !definition.IsFollowingTerrain &&
               definition.PrimitiveKind != VfxPrimitiveKind.PlanarProjection &&
               !IsGroundLikeBirthRotation(definition.BirthRotation);

        internal static bool IsGroundLikeBirthRotation(VfxCurve3? birthRotation)
        {
            if (birthRotation is not { } curve) return false;
            Vector3 rotation = curve.Sample(0f);
            float tiltX = MathF.Abs(MathF.Abs(rotation.X) - 90f);
            float tiltY = MathF.Abs(MathF.Abs(rotation.Y) - 90f);
            return (tiltX < 45f && tiltY < 45f) ||
                   MathF.Abs(rotation.X - 270f) < 45f ||
                   (MathF.Abs(MathF.Abs(rotation.X) - 90f) < 45f && MathF.Abs(rotation.Z) < 45f);
        }









    }
}
