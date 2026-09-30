using System;
using AssetsManager.Utils;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    /// <summary>
    /// Draws effect billboards and mesh primitives from prepared playback state.
    /// </summary>
    public sealed partial class VfxOpenGlRenderer : IDisposable
    {
        private GL _gl = null!;
        private VfxShaderUniforms _particleUniforms, _stockParticleUniforms, _meshUniforms, _stockMeshUniforms;
        private uint _program, _vao, _quadVbo, _instVbo, _trailVao, _trailVbo;
        private readonly VfxTrailGeometry _trailGeometry = new();
        private readonly VfxBeamGeometry _beamGeometry = new();
        private int _instCapFloats;
        private int _trailCapFloats;
        private bool _ready;
        private VfxTextureResourceCache _textures = null!;
        private GlSceneCapture _capture = null!;
        private VfxMeshResourceCache _meshResources = null!;
        private float[] _groupedInstances = Array.Empty<float>();
        private float[] _sortedInstances = Array.Empty<float>();
        private float[] _instanceDepths = Array.Empty<float>();
        private int[] _instanceOrder = Array.Empty<int>();
        private readonly Dictionary<(object Graph, string Path, int SourceOrder, int Pass), int> _emitterUsed = new();
        private readonly Dictionary<(object Graph, string Path, int SourceOrder), VfxPlaybackRuntime.EmitterState> _firstSourceByEmitter = new();
        private readonly Dictionary<(object Graph, string Path, int SourceOrder), List<VfxPlaybackRuntime.EmitterState>> _sortedQuadGroups = new();
        private readonly HashSet<(object Graph, string Path, int SourceOrder, int Pass)> _renderedSortedQuadGroups = new();
        private readonly List<List<VfxPlaybackRuntime.EmitterState>> _quadSourceLists = new();
        private int _quadSourceListCount;
        private readonly List<VfxRenderQueue.PassDraw> _particlePassDraws = new();
        private Func<VfxPlaybackRuntime.EmitterState, int> _particlePassCountSelector;
        private Func<VfxPlaybackRuntime.EmitterState, int, bool> _particleTransparencySelector;
        private bool _drawingWireframe;
        private readonly HashSet<uint> _ownerHiddenSubmeshes = new();
        private AssetsManager.Services.Viewer.Rendering.GameShaders.GameShaderRuntime.DrawElementsDelegate _drawElements = null!;
        private const int Stride = VfxPlaybackRuntime.InstanceStride;
        private const int QuadsPerEmitter = 4096;
        private const int MeshesPerEmitter = 512;
        private const int BeamsPerEmitter = 256;
        private const int AttachedMeshesPerEmitter = 8;
        // LTK draws wireframe twins with the preview accent and a dedicated alpha.
        private static readonly Vector3 PreviewWireColor = new(92f / 255f, 133f / 255f, 1f);
        private bool _gles;
        private Vector2 _depthProjectionValue;
        private int _renderBatchDepth;
        private GlStateSnapshot _renderBatchState;

        private sealed class GlStateSnapshot
        {
            internal bool DepthTest;
            internal bool CullFace;
            internal int CullMode;
            internal int FrontFace;
            internal bool PolygonOffset;
            internal bool Blend;
            internal bool StencilTest;
            internal int DepthWrite;
            internal int DepthFunction;
            internal int BlendSource;
            internal int BlendDestination;
            internal int BlendSourceAlpha;
            internal int BlendDestinationAlpha;
            internal int BlendEquation;
            internal int BlendEquationAlpha;
            internal int StencilFrontFunction;
            internal int StencilFrontReference;
            internal int StencilFrontValueMask;
            internal int StencilFrontWriteMask;
            internal int StencilFrontFail;
            internal int StencilFrontDepthFail;
            internal int StencilFrontDepthPass;
            internal int StencilBackFunction;
            internal int StencilBackReference;
            internal int StencilBackValueMask;
            internal int StencilBackWriteMask;
            internal int StencilBackFail;
            internal int StencilBackDepthFail;
            internal int StencilBackDepthPass;
            internal readonly int[] ColorWriteMask = new int[4];
            internal int Program;
            internal int VertexArray;
            internal int ArrayBuffer;
            internal int ActiveTexture;
            internal readonly int[] TextureBindings = new int[32];
            internal readonly int[] SamplerBindings = new int[32];
            internal readonly int[] CubeBindings = new int[32];
            internal readonly int[] ArrayBindings = new int[32];
            internal readonly int[] VolumeBindings = new int[32];
        }

        private sealed class RenderBatchScope : IDisposable
        {
            private VfxOpenGlRenderer _owner;

            internal RenderBatchScope(VfxOpenGlRenderer owner) => _owner = owner;

            public void Dispose()
            {
                VfxOpenGlRenderer owner = _owner;
                _owner = null;
                owner?.EndRenderBatch();
            }
        }

        public void Initialize(GL gl, AppSettings settings = null)
        {
            _gl = gl;
            var proc = gl.Context.GetProcAddress("glDrawElements");
            if (proc == IntPtr.Zero)
                throw new NotSupportedException("The active OpenGL context does not expose glDrawElements.");
            _drawElements = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<AssetsManager.Services.Viewer.Rendering.GameShaders.GameShaderRuntime.DrawElementsDelegate>(proc);
            bool gles = GlShaderCompiler.UsesEmbeddedProfile(gl);
            _gles = gles;
            _gameShaders = new AssetsManager.Services.Viewer.Rendering.GameShaders.GameShaderRuntime(gl, gles, settings);
            _program = GlShaderCompiler.CreateProgram(gl, gles, VfxShaderSource.ParticleVertex, VfxShaderSource.ParticleFragment);
            _particleUniforms = _stockParticleUniforms = new VfxShaderUniforms(gl, _program);

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
            gl.EnableVertexAttribArray(6); gl.VertexAttribPointer(6, 4, VertexAttribPointerType.Float, false, bstride, new IntPtr(15 * sizeof(float)));
            // Pack the authored 45-float instance record into at most 14 instance attributes.
            // OpenGL guarantees only 16 generic attributes; location 0 is the quad corner.
            gl.EnableVertexAttribArray(7); gl.VertexAttribPointer(7, 4, VertexAttribPointerType.Float, false, bstride, new IntPtr(19 * sizeof(float)));
            gl.EnableVertexAttribArray(8); gl.VertexAttribPointer(8, 4, VertexAttribPointerType.Float, false, bstride, new IntPtr(23 * sizeof(float)));
            gl.EnableVertexAttribArray(9); gl.VertexAttribPointer(9, 2, VertexAttribPointerType.Float, false, bstride, new IntPtr(27 * sizeof(float)));
            gl.EnableVertexAttribArray(10); gl.VertexAttribPointer(10, 4, VertexAttribPointerType.Float, false, bstride, new IntPtr(29 * sizeof(float)));
            gl.EnableVertexAttribArray(11); gl.VertexAttribPointer(11, 3, VertexAttribPointerType.Float, false, bstride, new IntPtr(33 * sizeof(float)));
            gl.EnableVertexAttribArray(12); gl.VertexAttribPointer(12, 3, VertexAttribPointerType.Float, false, bstride, new IntPtr(36 * sizeof(float)));
            gl.EnableVertexAttribArray(13); gl.VertexAttribPointer(13, 3, VertexAttribPointerType.Float, false, bstride, new IntPtr(39 * sizeof(float)));
            gl.EnableVertexAttribArray(14); gl.VertexAttribPointer(14, 3, VertexAttribPointerType.Float, false, bstride, new IntPtr(42 * sizeof(float)));

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

            gl.BindVertexArray(0);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

            _textures = new VfxTextureResourceCache(gl);
            _trailVao = gl.GenVertexArray();
            _trailVbo = gl.GenBuffer();
            gl.BindVertexArray(_trailVao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _trailVbo);
            int[] sizes = { 2, 3, 2, 4, 2, 4, 4, 4, 4, 2, 4, 3, 3, 3, 3 };
            int[] offsets = { 0, 2, 5, 7, 11, 13, 17, 21, 25, 29, 31, 35, 38, 41, 44 };
            for (uint attribute = 0; attribute < sizes.Length; attribute++)
            {
                gl.EnableVertexAttribArray(attribute);
                gl.VertexAttribPointer(attribute, sizes[attribute], VertexAttribPointerType.Float, false,
                    VfxTrailGeometry.VertexStride * sizeof(float), new IntPtr(offsets[attribute] * sizeof(float)));
            }
            gl.BindVertexArray(0);
            _capture = new GlSceneCapture(gl);
            _meshResources = new VfxMeshResourceCache(gl);
            _ready = true;
        }

        public uint UploadTexture(ReadOnlySpan<byte> bgra, int width, int height)
            => _textures.Upload(bgra, width, height);

        internal uint UploadCubeMap(VfxCubeMapData cube)
            => _textures.UploadCube(cube);

        public void CaptureScene(uint width, uint height, bool captureColor, bool captureDepth)
            => _capture.Capture(width, height, captureColor, captureDepth);

        internal bool SupportsWireframe => !_gles;

        /// <summary>
        /// Preserves the surrounding viewer GL state once for a group of VFX passes. Render() remains
        /// self-contained outside a batch, while shaded/wire/distortion passes can avoid repeating
        /// synchronous glGet state queries within the same frame.
        /// </summary>
        internal IDisposable BeginRenderBatch()
        {
            if (!_ready)
                return new RenderBatchScope(null);
            if (_renderBatchDepth++ == 0)
                _renderBatchState = CaptureGlState();
            return new RenderBatchScope(this);
        }

        private void EndRenderBatch()
        {
            if (_renderBatchDepth <= 0)
                return;
            _renderBatchDepth--;
            if (_renderBatchDepth != 0)
                return;

            GlStateSnapshot state = _renderBatchState;
            _renderBatchState = null;
            if (state != null)
                RestoreGlState(state);
        }

        private GlStateSnapshot CaptureGlState()
        {
            var state = new GlStateSnapshot
            {
                DepthTest = _gl.IsEnabled(EnableCap.DepthTest),
                CullFace = _gl.IsEnabled(EnableCap.CullFace),
                PolygonOffset = _gl.IsEnabled(EnableCap.PolygonOffsetFill),
                Blend = _gl.IsEnabled(EnableCap.Blend),
                StencilTest = _gl.IsEnabled(EnableCap.StencilTest)
            };
            _gl.GetInteger(GLEnum.DepthWritemask, out state.DepthWrite);
            _gl.GetInteger(GLEnum.DepthFunc, out state.DepthFunction);
            _gl.GetInteger(GLEnum.BlendSrcRgb, out state.BlendSource);
            _gl.GetInteger(GLEnum.BlendDstRgb, out state.BlendDestination);
            _gl.GetInteger(GLEnum.BlendSrcAlpha, out state.BlendSourceAlpha);
            _gl.GetInteger(GLEnum.BlendDstAlpha, out state.BlendDestinationAlpha);
            _gl.GetInteger(GLEnum.BlendEquationRgb, out state.BlendEquation);
            _gl.GetInteger(GLEnum.BlendEquationAlpha, out state.BlendEquationAlpha);
            _gl.GetInteger(GLEnum.StencilFunc, out state.StencilFrontFunction);
            _gl.GetInteger(GLEnum.StencilRef, out state.StencilFrontReference);
            _gl.GetInteger(GLEnum.StencilValueMask, out state.StencilFrontValueMask);
            _gl.GetInteger(GLEnum.StencilWritemask, out state.StencilFrontWriteMask);
            _gl.GetInteger(GLEnum.StencilFail, out state.StencilFrontFail);
            _gl.GetInteger(GLEnum.StencilPassDepthFail, out state.StencilFrontDepthFail);
            _gl.GetInteger(GLEnum.StencilPassDepthPass, out state.StencilFrontDepthPass);
            _gl.GetInteger(GLEnum.StencilBackFunc, out state.StencilBackFunction);
            _gl.GetInteger(GLEnum.StencilBackRef, out state.StencilBackReference);
            _gl.GetInteger(GLEnum.StencilBackValueMask, out state.StencilBackValueMask);
            _gl.GetInteger(GLEnum.StencilBackWritemask, out state.StencilBackWriteMask);
            _gl.GetInteger(GLEnum.StencilBackFail, out state.StencilBackFail);
            _gl.GetInteger(GLEnum.StencilBackPassDepthFail, out state.StencilBackDepthFail);
            _gl.GetInteger(GLEnum.StencilBackPassDepthPass, out state.StencilBackDepthPass);
            _gl.GetInteger(GLEnum.ColorWritemask, state.ColorWriteMask);
            _gl.GetInteger(GLEnum.CurrentProgram, out state.Program);
            _gl.GetInteger(GLEnum.VertexArrayBinding, out state.VertexArray);
            _gl.GetInteger(GLEnum.ArrayBufferBinding, out state.ArrayBuffer);
            _gl.GetInteger(GLEnum.CullFaceMode, out state.CullMode);
            _gl.GetInteger(GLEnum.FrontFace, out state.FrontFace);
            _gl.GetInteger(GLEnum.ActiveTexture, out state.ActiveTexture);
            for (int unit = 0; unit < state.TextureBindings.Length; unit++)
            {
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
                _gl.GetInteger(GLEnum.SamplerBinding, out state.SamplerBindings[unit]);
                _gl.GetInteger(GLEnum.TextureBinding2D, out state.TextureBindings[unit]);
                _gl.GetInteger(GLEnum.TextureBindingCubeMap, out state.CubeBindings[unit]);
                _gl.GetInteger(GLEnum.TextureBinding2DArray, out state.ArrayBindings[unit]);
                _gl.GetInteger(GLEnum.TextureBinding3D, out state.VolumeBindings[unit]);
            }
            _gl.ActiveTexture((TextureUnit)state.ActiveTexture);
            return state;
        }

        private void RestoreGlState(GlStateSnapshot state)
        {
            _gl.CullFace((TriangleFace)state.CullMode);
            _gl.FrontFace((FrontFaceDirection)state.FrontFace);
            _gl.DepthMask(state.DepthWrite != 0);
            _gl.DepthFunc((DepthFunction)state.DepthFunction);
            _gl.BlendEquationSeparate((GLEnum)state.BlendEquation, (GLEnum)state.BlendEquationAlpha);
            _gl.BlendFuncSeparate(
                (BlendingFactor)state.BlendSource,
                (BlendingFactor)state.BlendDestination,
                (BlendingFactor)state.BlendSourceAlpha,
                (BlendingFactor)state.BlendDestinationAlpha);
            _gl.ColorMask(
                state.ColorWriteMask[0] != 0,
                state.ColorWriteMask[1] != 0,
                state.ColorWriteMask[2] != 0,
                state.ColorWriteMask[3] != 0);
            _gl.StencilFuncSeparate(
                TriangleFace.Front,
                (StencilFunction)state.StencilFrontFunction,
                state.StencilFrontReference,
                (uint)state.StencilFrontValueMask);
            _gl.StencilMaskSeparate(TriangleFace.Front, (uint)state.StencilFrontWriteMask);
            _gl.StencilOpSeparate(
                TriangleFace.Front,
                (StencilOp)state.StencilFrontFail,
                (StencilOp)state.StencilFrontDepthFail,
                (StencilOp)state.StencilFrontDepthPass);
            _gl.StencilFuncSeparate(
                TriangleFace.Back,
                (StencilFunction)state.StencilBackFunction,
                state.StencilBackReference,
                (uint)state.StencilBackValueMask);
            _gl.StencilMaskSeparate(TriangleFace.Back, (uint)state.StencilBackWriteMask);
            _gl.StencilOpSeparate(
                TriangleFace.Back,
                (StencilOp)state.StencilBackFail,
                (StencilOp)state.StencilBackDepthFail,
                (StencilOp)state.StencilBackDepthPass);
            if (state.DepthTest) _gl.Enable(EnableCap.DepthTest); else _gl.Disable(EnableCap.DepthTest);
            if (state.CullFace) _gl.Enable(EnableCap.CullFace); else _gl.Disable(EnableCap.CullFace);
            if (state.PolygonOffset) _gl.Enable(EnableCap.PolygonOffsetFill); else _gl.Disable(EnableCap.PolygonOffsetFill);
            if (state.Blend) _gl.Enable(EnableCap.Blend); else _gl.Disable(EnableCap.Blend);
            if (state.StencilTest) _gl.Enable(EnableCap.StencilTest); else _gl.Disable(EnableCap.StencilTest);
            for (int unit = 0; unit < state.TextureBindings.Length; unit++)
            {
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
                _gl.BindSampler((uint)unit, (uint)state.SamplerBindings[unit]);
                _gl.BindTexture(TextureTarget.Texture2D, (uint)state.TextureBindings[unit]);
                _gl.BindTexture(TextureTarget.TextureCubeMap, (uint)state.CubeBindings[unit]);
                _gl.BindTexture(TextureTarget.Texture2DArray, (uint)state.ArrayBindings[unit]);
                _gl.BindTexture(TextureTarget.Texture3D, (uint)state.VolumeBindings[unit]);
            }
            _gl.ActiveTexture((TextureUnit)state.ActiveTexture);
            _gl.BindVertexArray((uint)state.VertexArray);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, (uint)state.ArrayBuffer);
            _gl.UseProgram((uint)state.Program);
        }

        public void Render(IReadOnlyList<VfxRenderQueueEntry> renderQueue, Matrix4x4 viewProj, Matrix4x4 view,
            bool wireframePass = false,
            float wireframeOpacity = 1f)
        {
            if (!_ready || renderQueue is null || renderQueue.Count == 0) return;
            bool useWireframe = wireframePass && SupportsWireframe;
            float wireOpacity = Math.Clamp(wireframeOpacity, 0f, 1f);

            Matrix4x4.Invert(view, out var inv);
            var camRight = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, inv));
            var camUp = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, inv));
            var camPos = inv.Translation;
            Matrix4x4 projection = inv * viewProj;
            _depthProjectionValue = new Vector2(projection.M33, projection.M43);

            GlStateSnapshot ownedState = _renderBatchDepth == 0 ? CaptureGlState() : null;

            ResetEmitterDrawScratch();
            try
            {
            if (useWireframe)
                _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);

            _gl.UseProgram(_program);
            _gl.UniformMatrix4(_particleUniforms.ViewProj, 1, false, in viewProj.M11);
            _gl.Uniform3(_particleUniforms.CamRight, camRight.X, camRight.Y, camRight.Z);
            _gl.Uniform3(_particleUniforms.CamUp, camUp.X, camUp.Y, camUp.Z);
            _gl.Uniform3(_particleUniforms.CamPos, camPos.X, camPos.Y, camPos.Z);
            _gl.Uniform1(_particleUniforms.Tex, 0);
            _gl.Uniform1(_particleUniforms.TexMult, 1);
            _gl.Uniform1(_particleUniforms.ColorMap, 7);
            _gl.Uniform1(_particleUniforms.PaletteMap, 8);
            _gl.Uniform1(_particleUniforms.SceneTex, 2);
            _gl.Uniform1(_particleUniforms.DistortionTex, 3);
            _gl.Uniform1(_particleUniforms.ErosionTex, 4);
            _gl.Uniform1(_particleUniforms.SceneDepthTex, 6);
            _gl.Uniform2(_particleUniforms.ViewportSize, (float)_capture.Width, (float)_capture.Height);
            _gl.Uniform2(_particleUniforms.DepthProjection, _depthProjectionValue.X, _depthProjectionValue.Y);
            _gl.Uniform1(_particleUniforms.WireframePass, useWireframe ? 1 : 0);
            _gl.Uniform4(
                _particleUniforms.WireframeColor,
                PreviewWireColor.X,
                PreviewWireColor.Y,
                PreviewWireColor.Z,
                wireOpacity);

            _gl.BindVertexArray(_vao);
            _gl.ActiveTexture(TextureUnit.Texture0);

            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthMask(false);
            _gl.Disable(EnableCap.CullFace);
            _gl.Disable(EnableCap.PolygonOffsetFill);
            _gl.Disable(EnableCap.StencilTest);
            _gl.ColorMask(true, true, true, true);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendEquation(GLEnum.FuncAdd);

            Matrix4x4.Invert(view, out var invView);
            Matrix4x4 particleProjection = invView * viewProj;
            _gameFrame = new AssetsManager.Services.Viewer.Rendering.GameShaders.GameShaderRuntime.Frame(view, particleProjection, camPos, 0f, Sun);

            Dictionary<(object Graph, string Path, int SourceOrder, int Pass), int> emitterUsed = _emitterUsed;
            Dictionary<(object Graph, string Path, int SourceOrder), VfxPlaybackRuntime.EmitterState> firstSourceByEmitter = _firstSourceByEmitter;
            Dictionary<(object Graph, string Path, int SourceOrder), List<VfxPlaybackRuntime.EmitterState>> sortedQuadGroups = _sortedQuadGroups;
            foreach (VfxRenderQueueEntry candidate in renderQueue)
            {
                VfxPlaybackRuntime.EmitterState candidateEmitter = candidate.Emitter;
                var key = (candidateEmitter.RenderGraphKey ?? candidateEmitter,
                    candidateEmitter.RenderPath ?? string.Empty,
                    candidateEmitter.SourceOrder);
                firstSourceByEmitter.TryAdd(key, candidateEmitter);
                if (candidateEmitter.InstanceCount <= 0 || !candidateEmitter.IsVisible ||
                    !ShouldSortInstances(candidateEmitter.Def, 2)) continue;
                if (!sortedQuadGroups.TryGetValue(key, out List<VfxPlaybackRuntime.EmitterState> sources))
                {
                    sources = RentQuadSourceList();
                    sortedQuadGroups[key] = sources;
                }
                sources.Add(candidateEmitter);
            }
            HashSet<(object Graph, string Path, int SourceOrder, int Pass)> renderedSortedQuadGroups = _renderedSortedQuadGroups;

            _drawingWireframe = useWireframe;
            _particlePassCountSelector ??= emitter => ParticlePassCount(emitter, emitter.Def.IsMeshPrimitive, _drawingWireframe);
            _particleTransparencySelector ??= (emitter, pass) => ParticlePassTransparent(emitter, pass, _drawingWireframe);
            VfxRenderQueue.BuildPassesInto(renderQueue, _particlePassDraws,
                _particlePassCountSelector, _particleTransparencySelector);
            foreach (VfxRenderQueue.PassDraw draw in _particlePassDraws)
            {
                VfxPlaybackRuntime.EmitterState es = draw.Entry.Emitter;
                int passIndex = draw.PassIndex;
                if (es.InstanceCount == 0) continue;
                if (!es.IsVisible) continue;
                if (useWireframe)
                    _gl.Disable(EnableCap.CullFace);

                // LTK keeps WriteAlphaOnly/stencil as inspector metadata. Its VFX preview has no
                // gameplay stencil buffer, so applying either here would hide or recolor effects
                // that LTK deliberately draws as ordinary RGBA.
                _gl.ColorMask(true, true, true, true);
                _gl.Disable(EnableCap.StencilTest);
                // Never synthesize an AttachedMesh proxy. Render only geometry that was
                // resolved from the real owner scene and filtered by authored submesh masks.
                if (es.Def.IsMeshPrimitive && es.MeshVao == 0)
                    continue;

                // LTK allocates one draw component per graph/path/emitter definition. Every live
                // source of that child path shares the same fixed draw budget and palette phase.
                var emitterKey = (es.RenderGraphKey ?? es, es.RenderPath ?? string.Empty, es.SourceOrder);
                var passKey = (emitterKey.Item1, emitterKey.Item2, emitterKey.Item3, passIndex);
                VfxPlaybackRuntime.EmitterState paletteSource = firstSourceByEmitter.GetValueOrDefault(emitterKey) ?? es;
                float sharedPalettePhase = ResolveEmitterPhase(es.Def, paletteSource.EmitterAge);
                int renderInstanceCount;
                ReadOnlySpan<float> instancesSpan;
                if (sortedQuadGroups.TryGetValue(emitterKey, out List<VfxPlaybackRuntime.EmitterState> quadSources))
                {
                    // Quads.tsx gathers every live source first and sorts the combined set once.
                    if (!renderedSortedQuadGroups.Add(passKey)) continue;
                    EnsureInstanceSortCapacity(QuadsPerEmitter, QuadsPerEmitter * Stride);
                    renderInstanceCount = VfxRenderQueue.CopyQuadSourcesBackToFront(
                        quadSources,
                        QuadsPerEmitter,
                        Stride,
                        view,
                        _groupedInstances,
                        _sortedInstances,
                        _instanceDepths,
                        _instanceOrder);
                    if (renderInstanceCount == 0) continue;
                    instancesSpan = new ReadOnlySpan<float>(_sortedInstances, 0, renderInstanceCount * Stride);
                    emitterUsed[passKey] = renderInstanceCount;
                }
                else
                {
                    int alreadyUsed = emitterUsed.GetValueOrDefault(passKey);
                    renderInstanceCount = ResolveEmitterDrawCount(es.Def, alreadyUsed, es.InstanceCount);
                    if (renderInstanceCount == 0) continue;
                    emitterUsed[passKey] = alreadyUsed + renderInstanceCount;
                    instancesSpan = es.PrepareInstances(renderInstanceCount);
                }

                if (es.Def.IsMeshPrimitive && es.MeshVao != 0)
                {
                    bool meshDistortion = es.Def.DrawsAsDistortion && !useWireframe;
                    ApplyEmitterDepthState(es.Def, meshDistortion);
                    if (useWireframe)
                    {
                        _gl.DepthMask(false);
                        _gl.DepthFunc(DepthFunction.Lequal);
                    }
                    if (useWireframe)
                        ApplyWireframeBlend();
                    else
                        ApplyEmitterBlendState(es.Def, meshDistortion);
                    RenderMeshEmitter(
                        es,
                        viewProj,
                        camPos,
                        camUp,
                        instancesSpan,
                        renderInstanceCount,
                        sharedPalettePhase,
                        useWireframe,
                        wireOpacity, passIndex);
                    continue;
                }
                if (es.Def.DrawsAsProjection)
                {
                    RenderProjection(es, instancesSpan, renderInstanceCount, viewProj, useWireframe, wireOpacity);
                    continue;
                }
                RenderQuadEmitter(es, instancesSpan, renderInstanceCount, sharedPalettePhase, useWireframe, wireOpacity, camPos, camRight, camUp, passIndex);
            }

            }
            finally
            {
                ResetEmitterDrawScratch();
                if (useWireframe)
                    _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);

                if (ownedState != null)
                    RestoreGlState(ownedState);
            }
        }


        private void RenderQuadEmitter(VfxPlaybackRuntime.EmitterState es, ReadOnlySpan<float> instancesSpan, int renderInstanceCount, float sharedPalettePhase, bool useWireframe, float wireOpacity, Vector3 camPos, Vector3 camRight, Vector3 camUp, int passIndex)
        {
            if (!es.Def.IsVisual) return;
            bool native = UseGameParticle(es, false, passIndex, useWireframe);
            int floats = renderInstanceCount * Stride;
            bool isDistortion = es.Def.DrawsAsDistortion && !useWireframe;
            bool warpsFrame = isDistortion && es.Def.Distortion.Strength != 0f;
            if (warpsFrame && _capture.ColorTexture == 0) return;

            var renderState = es.Def.RenderState ?? VfxEmitterRenderState.Default;
            _gl.Uniform2(_particleUniforms.TexDiv, es.Def.TexDiv.X <= 0 ? 1f : es.Def.TexDiv.X, es.Def.TexDiv.Y <= 0 ? 1f : es.Def.TexDiv.Y);
            _gl.Uniform2(_particleUniforms.TexSize, Math.Max(1f, es.TextureWidth), Math.Max(1f, es.TextureHeight));
            Vector2 emitterUvOffset = VfxUvSemantics.Periodic(
                es.Def.EmitterUvScrollRate * es.RenderTime,
                renderState.TextureAddressMode);
            _gl.Uniform2(_particleUniforms.EmitterUvOffset, emitterUvOffset.X, emitterUvOffset.Y);
            Vector2 uvCenter = es.Def.UvTransformCenter;
            _gl.Uniform2(_particleUniforms.UvTransformCenter, uvCenter.X, uvCenter.Y);
            _gl.Uniform1(_particleUniforms.HasTexMult, es.TextureMult != 0 ? 1 : 0);
            var multDiv = es.Def.TextureMultTexDiv;
            _gl.Uniform2(_particleUniforms.TexDivMult, multDiv.X <= 0 ? 1f : multDiv.X, multDiv.Y <= 0 ? 1f : multDiv.Y);
            _gl.Uniform2(
                _particleUniforms.TexSizeMult,
                Math.Max(1f, es.TextureMultWidth),
                Math.Max(1f, es.TextureMultHeight));
            Vector2 emitterUvOffsetMult = VfxUvSemantics.Periodic(
                es.Def.TextureMultEmitterUvScrollRate * es.RenderTime,
                es.Def.TextureMultAddressMode);
            _gl.Uniform2(_particleUniforms.UvScrollRateMult, emitterUvOffsetMult.X, emitterUvOffsetMult.Y);
            Vector2 uvCenterMult = es.Def.TextureMultTransformCenter;
            _gl.Uniform2(_particleUniforms.UvTransformCenterMult, uvCenterMult.X, uvCenterMult.Y);
            _gl.Uniform1(_particleUniforms.FlipUMult, es.Def.TextureMultFlipU ? 1 : 0);
            _gl.Uniform1(_particleUniforms.FlipVMult, es.Def.TextureMultFlipV ? 1 : 0);
            _gl.Uniform1(_particleUniforms.ClampUvMult, es.Def.TextureMultClampUvScroll ? 1 : 0);
            bool directional = ShouldDirectionOrientBillboard(es.Def);
            bool arbitrary = es.Def.IsArbitraryQuad ||
                es.Def.PrimitiveKind == VfxPrimitiveKind.ArbitraryTrail;
            _gl.Uniform1(_particleUniforms.DirectionOriented, directional ? 1 : 0);
            _gl.Uniform1(_particleUniforms.ArbitraryQuad, arbitrary ? 1 : 0);
            _gl.Uniform1(_particleUniforms.LegacyOrientation, es.Def.LegacyOrientation);
            _gl.Uniform1(_particleUniforms.PivotUp, es.Def.LegacyScaleUpFromOrigin ? 1 : 0);
            bool groundLayer = ShouldProjectToGround(es.Def);
            _gl.Uniform1(_particleUniforms.IsGroundLayer, groundLayer ? 1 : 0);
            _gl.Uniform1(_particleUniforms.PrimitiveKind, (int)es.Def.PrimitiveKind);
            bool ribbonPrimitive = es.Def.PrimitiveKind is VfxPrimitiveKind.CameraTrail or
                VfxPrimitiveKind.ArbitraryTrail or VfxPrimitiveKind.Beam or VfxPrimitiveKind.CameraSegmentBeam;
            _gl.Uniform1(_particleUniforms.DepthPushPull, ribbonPrimitive ? 0f : es.Def.DepthPushPull);
            ApplyEmitterDepthState(es.Def, isDistortion);
            if (useWireframe)
            {
                _gl.DepthMask(false);
                _gl.DepthFunc(DepthFunction.Lequal);
            }
            if (useWireframe)
                ApplyWireframeBlend();
            else
                ApplyEmitterBlendState(es.Def, isDistortion);
            if (!useWireframe)
                ApplyParticleCullState(es.Def);
            ModelMaterialDefinition customMaterial = es.Def.HasResolvedCustomMaterial ? es.Def.CustomMaterial : null;
            ApplyCustomMaterialUniforms(
                customMaterial,
                _particleUniforms.UseCustomMaterial,
                _particleUniforms.MaterialTint,
                _particleUniforms.MaterialRepeat,
                _particleUniforms.MaterialAddressU,
                _particleUniforms.MaterialAddressV,
                _particleUniforms.MaterialPremultiplied);
            float alphaCutoff = customMaterial?.AlphaCutoff ?? renderState.AlphaCutoff;
            _gl.Uniform1(_particleUniforms.AlphaCutoff, alphaCutoff);
            _gl.Uniform1(
                _particleUniforms.AlphaTest,
                customMaterial is not null
                    ? (alphaCutoff > 0f ? 1 : 0)
                    : (VfxBlendModes.ShouldAlphaTest(es.Def.BlendMode, renderState.AlphaReference) ? 1 : 0));
            _gl.Uniform1(_particleUniforms.EmissiveStrength, VfxBlendModes.ResolveEmissiveStrength(es.Def.BlendMode));
            bool hasMultLayer = HasTextureMultLayer(es.Def);
            bool useColorRamp = ShouldUseColorRamp(es.Def, es.ColorGradientTexture != 0);
            _gl.Uniform1(_particleUniforms.HasColor, useColorRamp ? 1 : 0);
            _gl.Uniform1(_particleUniforms.RampAtMult, useColorRamp && hasMultLayer ? 1 : 0);
            _gl.Uniform1(_particleUniforms.UvMode, es.Def.UvMode);
            _gl.Uniform1(
                _particleUniforms.ColorRenderFlags,
                VfxBlendModes.ResolveColorRenderFlags(
                    es.Def.ColorRenderFlags,
                    !string.IsNullOrWhiteSpace(es.Def.ParticleColorTexturePath)));
            VfxPaletteDefinition palette = es.Def.PaletteDefinition;
            bool hasPalette = es.PaletteTexture != 0 && palette is { PaletteCount: > 0 };
            _gl.Uniform1(_particleUniforms.HasPalette, hasPalette ? 1 : 0);
            _gl.Uniform1(_particleUniforms.PaletteCount, Math.Max(1, palette?.PaletteCount ?? 1));
            _gl.Uniform1(_particleUniforms.PaletteAddressMode, palette?.AddressMode ?? 0);
            _gl.Uniform1(_particleUniforms.PaletteSelector, PaletteSelectorAtZero(palette));
            Vector4 paletteMask = palette?.PaletteSourceMixColor ?? Vector4.Zero;
            _gl.Uniform4(_particleUniforms.PaletteMixMask, paletteMask.X, paletteMask.Y, paletteMask.Z, paletteMask.W);
            Vector2 paletteScroll = new(
                palette?.ScrollU?.Sample(sharedPalettePhase) ?? 0f,
                palette?.ScrollV?.Sample(sharedPalettePhase) ?? 0f);
            _gl.Uniform2(_particleUniforms.PaletteScroll, paletteScroll.X, paletteScroll.Y);
            _gl.Uniform1(_particleUniforms.ColorLookUpTypeX, es.Def.ColorLookUpTypeX ?? 0);
            _gl.Uniform1(_particleUniforms.ColorLookUpTypeY, es.Def.ColorLookUpTypeY ?? 0);
            Vector2 colorLookUpScales = es.Def.ColorLookUpScales;
            _gl.Uniform2(_particleUniforms.ColorLookUpScales, colorLookUpScales.X, colorLookUpScales.Y);
            _gl.Uniform2(_particleUniforms.ColorLookUpOffsets, es.Def.ColorLookUpOffsets.X, es.Def.ColorLookUpOffsets.Y);
            _gl.Uniform1(_particleUniforms.FlipU, renderState.FlipU ? 1 : 0);
            _gl.Uniform1(_particleUniforms.FlipV, renderState.FlipV ? 1 : 0);
            _gl.Uniform1(_particleUniforms.ClampUv, renderState.ClampUvScroll ? 1 : 0);
            _gl.Uniform1(_particleUniforms.AddressMode, renderState.TextureAddressMode);
            _gl.Uniform1(_particleUniforms.AddressModeMult, es.Def.TextureMultAddressMode);
            _gl.Uniform1(_particleUniforms.IsDistortion, isDistortion ? 1 : 0);
            _gl.Uniform1(_particleUniforms.DistortionStrength, es.Def.Distortion?.Strength ?? 0f);
            VfxAlphaErosionDefinition erosionDefinition = es.Def.AlphaErosion;
            bool erosionEnabled = erosionDefinition is not null && es.Def.UvMode != 2;
            bool hasErosionMap = erosionEnabled && es.ErosionTexture != 0;
            Vector4 erosionDefault = erosionDefinition is not null && string.IsNullOrWhiteSpace(erosionDefinition.TexturePath)
                ? Vector4.One
                : Vector4.Zero;
            _gl.Uniform1(_particleUniforms.HasErosion, erosionEnabled ? 1 : 0);
            _gl.Uniform1(_particleUniforms.HasErosionMap, hasErosionMap ? 1 : 0);
            _gl.Uniform1(_particleUniforms.ErosionAddressMode, erosionDefinition?.AddressMode ?? 0);
            _gl.Uniform4(_particleUniforms.ErosionDefault, erosionDefault.X, erosionDefault.Y, erosionDefault.Z, erosionDefault.W);
            _gl.Uniform1(_particleUniforms.ErosionFeatherIn, erosionDefinition?.FeatherIn ?? 0f);
            _gl.Uniform1(_particleUniforms.ErosionFeatherOut, erosionDefinition?.FeatherOut ?? 0f);
            _gl.Uniform1(_particleUniforms.ErosionSliceWidth, erosionDefinition?.SliceWidth ?? 1.5f);
            bool useSoftParticles = ShouldUseSoftParticles(es.Def, _capture.DepthTexture != 0);
            _gl.Uniform1(_particleUniforms.HasSoftParticle, useSoftParticles ? 1 : 0);
            Vector4 softParams = ResolveSoftParticleParams(es.Def.SoftParticle);
            Vector4 softControl = ResolveSoftParticleControl(es.Def.BlendMode);
            _gl.Uniform4(_particleUniforms.SoftParticleParams, softParams.X, softParams.Y, softParams.Z, softParams.W);
            _gl.Uniform4(_particleUniforms.SoftParticleControl, softControl.X, softControl.Y, softControl.Z, softControl.W);
            _gl.Uniform3(_particleUniforms.PlacementRight, es.PlacementRight.X, es.PlacementRight.Y, es.PlacementRight.Z);
            _gl.Uniform3(_particleUniforms.PlacementUp, es.PlacementUp.X, es.PlacementUp.Y, es.PlacementUp.Z);
            _gl.Uniform3(_particleUniforms.PlacementForward, es.PlacementForward.X, es.PlacementForward.Y, es.PlacementForward.Z);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, es.Texture != 0 ? es.Texture : _textures.FallbackTransparentTexture);
            _gl.Uniform1(_particleUniforms.HasTex, ShouldSampleBaseTexture(es.Def, es.Texture) ? 1 : 0);
            ApplyAddressMode(2);
            ApplyTextureSampling();
            if (es.TextureMult != 0)
            {
                _gl.ActiveTexture(TextureUnit.Texture1);
                _gl.BindTexture(TextureTarget.Texture2D, es.TextureMult);
                ApplyAddressMode(2);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            if (_capture.ColorTexture != 0)
            {
                _gl.ActiveTexture(TextureUnit.Texture2);
                _gl.BindTexture(TextureTarget.Texture2D, _capture.ColorTexture);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            if (isDistortion)
            {
                // A missing distortion map contributes zero coverage. The transparent
                // fallback therefore preserves the draw and alpha-test path without warping.
                _gl.ActiveTexture(TextureUnit.Texture3);
                _gl.BindTexture(
                    TextureTarget.Texture2D,
                    es.DistortionTexture != 0 ? es.DistortionTexture : _textures.FallbackTransparentTexture);
                ApplyAddressMode(2);
                ApplyTextureSampling();
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            if (es.ErosionTexture != 0)
            {
                _gl.ActiveTexture(TextureUnit.Texture4);
                _gl.BindTexture(TextureTarget.Texture2D, es.ErosionTexture);
                ApplyAddressMode(2);
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
            ApplyAddressMode(2);
            ApplyTextureSampling();
            _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + 8));
            _gl.BindTexture(TextureTarget.Texture2D, es.PaletteTexture != 0
                ? es.PaletteTexture
                : _textures.FallbackTransparentTexture);
            ApplyAddressMode(2);
            ApplyTextureSampling();
            _gl.ActiveTexture(TextureUnit.Texture0);
            if (native) BindGameParticle(es, false, passIndex, sharedPalettePhase);
            if (es.Def.PrimitiveKind is VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail)
            {
                int vertices = _trailGeometry.Build(
                    es,
                    ResolveCameraForward(camRight, camUp),
                    renderInstanceCount);
                if (vertices > 0)
                {
                    _gl.BindVertexArray(_trailVao);
                    _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _trailVbo);
                    UploadTrailVertices(new ReadOnlySpan<float>(
                        _trailGeometry.Vertices,
                        0,
                        vertices * VfxTrailGeometry.VertexStride));
                    if (native) _gameShaders.DrawBoundArrays(PrimitiveType.Triangles, vertices);
                    else _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)vertices);
                    _gl.BindVertexArray(_vao);
                }
            }
            else if (es.Def.PrimitiveKind is VfxPrimitiveKind.Beam or VfxPrimitiveKind.CameraSegmentBeam)
            {
                // LTK suppresses a beam ribbon when the same primitive resolves mMesh.
                if (!string.IsNullOrWhiteSpace(es.Def.MeshPath))
                    return;
                int vertices = _beamGeometry.Build(es, camPos, renderInstanceCount);
                if (vertices > 0)
                {
                    _gl.BindVertexArray(_trailVao);
                    _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _trailVbo);
                    UploadTrailVertices(new ReadOnlySpan<float>(
                        _beamGeometry.Vertices,
                        0,
                        vertices * VfxBeamGeometry.VertexStride));
                    if (native) _gameShaders.DrawBoundArrays(PrimitiveType.Triangles, vertices);
                    else _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)vertices);
                    _gl.BindVertexArray(_vao);
                }
            }
            else
            {
                _gl.BindVertexArray(_vao);
                _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instVbo);
                if (floats > _instCapFloats)
                {
                    _gl.BufferData(BufferTargetARB.ArrayBuffer, instancesSpan, BufferUsageARB.DynamicDraw);
                    _instCapFloats = floats;
                }
                else
                {
                    _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, instancesSpan);
                }
                if (native) _gameShaders.DrawBoundArrays(PrimitiveType.TriangleFan, 4, (uint)renderInstanceCount);
                else _gl.DrawArraysInstanced(PrimitiveType.TriangleFan, 0, 4, (uint)renderInstanceCount);
            }
            _particleUniforms = _stockParticleUniforms;
            _gl.UseProgram(_program);
        }

        private void UploadTrailVertices(ReadOnlySpan<float> vertices)
        {
            if (vertices.Length > _trailCapFloats)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer, vertices, BufferUsageARB.DynamicDraw);
                _trailCapFloats = vertices.Length;
                return;
            }

            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, vertices);
        }

        private void ResetEmitterDrawScratch()
        {
            _emitterUsed.Clear();
            _particlePassDraws.Clear();
            _firstSourceByEmitter.Clear();
            _sortedQuadGroups.Clear();
            _renderedSortedQuadGroups.Clear();
            foreach (List<VfxPlaybackRuntime.EmitterState> sources in _quadSourceLists)
                sources.Clear();
            _quadSourceListCount = 0;
        }

        private List<VfxPlaybackRuntime.EmitterState> RentQuadSourceList()
        {
            if (_quadSourceListCount == _quadSourceLists.Count)
                _quadSourceLists.Add(new List<VfxPlaybackRuntime.EmitterState>());
            return _quadSourceLists[_quadSourceListCount++];
        }

        private void ApplyAddressMode(int addressMode)
        {
            var wrap = addressMode switch
            {
                1 => TextureWrapMode.MirroredRepeat,
                2 => TextureWrapMode.ClampToEdge,
                3 => TextureWrapMode.ClampToBorder,
                _ => TextureWrapMode.Repeat,
            };
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrap);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrap);
        }

        private void ApplyEmitterDepthState(VfxEmitterDefinition definition, bool isDistortion)
        {
            ModelMaterialDefinition customMaterial = definition.HasResolvedCustomMaterial ? definition.CustomMaterial : null;
            if (customMaterial is not null)
            {
                _gl.DepthMask(customMaterial.RenderState.DepthWrite);
                if (customMaterial.RenderState.DepthTest) _gl.Enable(EnableCap.DepthTest);
                else _gl.Disable(EnableCap.DepthTest);
            }
            else
            {
                var renderState = definition.RenderState ?? VfxEmitterRenderState.Default;
                bool writeDepth = !isDistortion && VfxBlendModes.ShouldWriteDepth(
                    definition.BlendMode,
                    renderState.AlphaReference);
                _gl.DepthMask(writeDepth);
                if (VfxBlendModes.ShouldTestDepth(definition.MiscRenderFlags)) _gl.Enable(EnableCap.DepthTest);
                else _gl.Disable(EnableCap.DepthTest);
            }

            // Three.js ShaderMaterial defaults to LessEqualDepth, and LTK never overrides it
            // for VFX materials. Keep equal-depth fragments eligible instead of inheriting
            // OpenGL's default LESS from the surrounding viewer.
            _gl.DepthFunc(DepthFunction.Lequal);

            Vector2? bias = ResolvePolygonOffset(definition);
            if (bias is { } polygonBias)
            {
                _gl.Enable(EnableCap.PolygonOffsetFill);
                _gl.PolygonOffset(polygonBias.X, polygonBias.Y);
            }
            else
            {
                _gl.Disable(EnableCap.PolygonOffsetFill);
            }
        }

        private void EnsureInstanceSortCapacity(int instanceCount, int floatCount)
        {
            if (_groupedInstances.Length < floatCount)
                _groupedInstances = new float[Math.Max(floatCount, Stride * 64)];
            if (_sortedInstances.Length < floatCount)
                _sortedInstances = new float[Math.Max(floatCount, Stride * 64)];
            if (_instanceDepths.Length < instanceCount)
                _instanceDepths = new float[Math.Max(instanceCount, 64)];
            if (_instanceOrder.Length < instanceCount)
                _instanceOrder = new int[Math.Max(instanceCount, 64)];
        }

        private void ApplyTextureSampling()
        {
            // LTK's VFX texture loader always uses linear filtering. isTexturePixelated is
            // inspector metadata in 1.19.6 and does not change the particle material sampler.
            _gl.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            _gl.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
        }

        private void ApplyWireframeBlend()
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendEquationSeparate(GLEnum.FuncAdd, GLEnum.FuncAdd);
            _gl.BlendFuncSeparate(
                BlendingFactor.SrcAlpha,
                BlendingFactor.OneMinusSrcAlpha,
                BlendingFactor.One,
                BlendingFactor.OneMinusSrcAlpha);
        }

        private void ApplyEmitterBlendState(VfxEmitterDefinition definition, bool distortion = false)
        {
            if (definition.HasResolvedCustomMaterial)
            {
                ApplyCustomMaterialBlend(definition);
                return;
            }
            ApplyBlendMode(definition.BlendMode, distortion);
        }

        private void ApplyCustomMaterialBlend(VfxEmitterDefinition definition)
        {
            ModelMaterialRenderState state = definition.CustomMaterial.RenderState;
            if (state.Blending == ModelMaterialBlendMode.Opaque)
            {
                _gl.Disable(EnableCap.Blend);
                return;
            }

            _gl.Enable(EnableCap.Blend);
            _gl.BlendEquation(GLEnum.FuncAdd);
            _gl.BlendFunc(
                ToOpenGl(definition.CustomMaterialSourceBlendFactor),
                ToOpenGl(definition.CustomMaterialDestinationBlendFactor));
        }

        private void ApplyParticleCullState(VfxEmitterDefinition definition)
        {
            if (!definition.HasResolvedCustomMaterial)
            {
                // LTK's ordinary quad/ribbon particle materials are DoubleSide.
                _gl.Disable(EnableCap.CullFace);
                return;
            }
            ApplyCustomMaterialCull(definition.CustomMaterial.RenderState);
        }

        private void ApplyCustomMaterialCull(ModelMaterialRenderState state)
        {
            if (state.DoubleSided)
            {
                _gl.Disable(EnableCap.CullFace);
                return;
            }
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(state.Inverted ? TriangleFace.Front : TriangleFace.Back);
        }

        private void ApplyCustomMaterialUniforms(
            ModelMaterialDefinition material,
            int useLocation,
            int tintLocation,
            int repeatLocation,
            int addressULocation,
            int addressVLocation,
            int premultipliedLocation)
        {
            bool enabled = material is not null;
            _gl.Uniform1(useLocation, enabled ? 1 : 0);
            Vector4 tint = material?.Color ?? Vector4.One;
            Vector2 repeat = material?.UvRepeat ?? Vector2.One;
            _gl.Uniform4(tintLocation, tint.X, tint.Y, tint.Z, tint.W);
            _gl.Uniform2(repeatLocation, repeat.X, repeat.Y);
            _gl.Uniform1(addressULocation, enabled ? (int)material.WrapU : 0);
            _gl.Uniform1(addressVLocation, enabled ? (int)material.WrapV : 0);
            _gl.Uniform1(premultipliedLocation, enabled && material.RenderState.PremultipliedAlpha ? 1 : 0);
        }

        internal static BlendingFactor ToOpenGl(VfxCustomMaterialBlendFactor factor) => factor switch
        {
            VfxCustomMaterialBlendFactor.Zero => BlendingFactor.Zero,
            VfxCustomMaterialBlendFactor.One => BlendingFactor.One,
            VfxCustomMaterialBlendFactor.SourceColor => BlendingFactor.SrcColor,
            VfxCustomMaterialBlendFactor.OneMinusSourceColor => BlendingFactor.OneMinusSrcColor,
            VfxCustomMaterialBlendFactor.DestinationColor => BlendingFactor.DstColor,
            VfxCustomMaterialBlendFactor.OneMinusDestinationColor => BlendingFactor.OneMinusDstColor,
            VfxCustomMaterialBlendFactor.SourceAlpha => BlendingFactor.SrcAlpha,
            VfxCustomMaterialBlendFactor.OneMinusSourceAlpha => BlendingFactor.OneMinusSrcAlpha,
            _ => throw new ArgumentOutOfRangeException(nameof(factor), factor, null)
        };

        private void ApplyBlendMode(int blendMode, bool distortion = false)
        {
            VfxBlendModeDescriptor descriptor = VfxBlendModes.GetDrawDescriptor(blendMode, distortion);
            if (descriptor.Kind == VfxBlendModeKind.Opaque)
            {
                _gl.Disable(EnableCap.Blend);
                return;
            }

            _gl.Enable(EnableCap.Blend);
            _gl.BlendEquationSeparate(
                ToOpenGl(descriptor.RgbEquation),
                ToOpenGl(descriptor.AlphaEquation));
            _gl.BlendFuncSeparate(
                ToOpenGl(descriptor.SourceRgb),
                ToOpenGl(descriptor.DestinationRgb),
                ToOpenGl(descriptor.SourceAlpha),
                ToOpenGl(descriptor.DestinationAlpha));
        }

        private static BlendingFactor ToOpenGl(VfxBlendFactor factor) => factor switch
        {
            VfxBlendFactor.Zero => BlendingFactor.Zero,
            VfxBlendFactor.One => BlendingFactor.One,
            VfxBlendFactor.SourceAlpha => BlendingFactor.SrcAlpha,
            VfxBlendFactor.OneMinusSourceAlpha => BlendingFactor.OneMinusSrcAlpha,
            VfxBlendFactor.DestinationColor => BlendingFactor.DstColor,
            VfxBlendFactor.OneMinusSourceColor => BlendingFactor.OneMinusSrcColor,
            VfxBlendFactor.DestinationAlpha => BlendingFactor.DstAlpha,
            VfxBlendFactor.OneMinusDestinationAlpha => BlendingFactor.OneMinusDstAlpha,
            _ => throw new ArgumentOutOfRangeException(nameof(factor), factor, null)
        };

        private static GLEnum ToOpenGl(VfxBlendEquationKind equation) => equation switch
        {
            VfxBlendEquationKind.Add => GLEnum.FuncAdd,
            VfxBlendEquationKind.Min => GLEnum.Min,
            VfxBlendEquationKind.Max => GLEnum.Max,
            _ => throw new ArgumentOutOfRangeException(nameof(equation), equation, null)
        };

        internal MapSunData Sun { get; set; }

        public void ClearTextures()
        {
            _gameShaders?.ClearParticlePrograms();
            _gameUniforms.Clear();
            if (!_ready) return;
            _textures.Clear();
            ReleaseMeshes();
        }

        public void Dispose()
        {
            _gameShaders?.Dispose();
            _gameUniforms.Clear();
            if (!_ready) return;
            _textures.Dispose();
            _gl.DeleteBuffer(_trailVbo);
            _gl.DeleteVertexArray(_trailVao);
            _gl.DeleteBuffer(_quadVbo);
            _gl.DeleteBuffer(_instVbo);
            _gl.DeleteVertexArray(_vao);
            _gl.DeleteProgram(_program);
            if (_projectionProgram != 0) _gl.DeleteProgram(_projectionProgram);
            _projectionProgram = 0;
            if (_meshBoneBuffer != 0) _gl.DeleteBuffer(_meshBoneBuffer);
            _meshBoneBuffer = 0;
            _ownerSkinningMatrices = null;
            _ownerWorldTransform = Matrix4x4.Identity;
            _ownerSkinningCount = 0;
            if (_meshProgram != 0) _gl.DeleteProgram(_meshProgram);
            _meshProgram = 0;
            _meshResources.Dispose();
            _capture.Dispose();
            ResetEmitterDrawScratch();
            _quadSourceLists.Clear();
            _groupedInstances = Array.Empty<float>();
            _sortedInstances = Array.Empty<float>();
            _instanceDepths = Array.Empty<float>();
            _instanceOrder = Array.Empty<int>();
            _ownerHiddenSubmeshes.Clear();
            _instCapFloats = 0;
            _trailCapFloats = 0;
            _ready = false;
        }

        private const uint OwnerBoneBinding = 1;
        private uint _meshProgram;
        private uint _meshBoneBuffer;
        private Matrix4x4[] _ownerSkinningMatrices;
        private Matrix4x4 _ownerWorldTransform = Matrix4x4.Identity;
        private int _ownerSkinningCount;

        private void EnsureMeshProgram()
        {
            if (_meshProgram == 0)
            {
                _meshProgram = GlShaderCompiler.CreateProgram(_gl, _gles, VfxShaderSource.MeshVertex, VfxShaderSource.MeshFragment);
            _meshUniforms = _stockMeshUniforms = new VfxShaderUniforms(_gl, _meshProgram);

                uint boneBlock = _gl.GetUniformBlockIndex(_meshProgram, "VfxBoneTransforms");
                if (boneBlock != uint.MaxValue)
                    _gl.UniformBlockBinding(_meshProgram, boneBlock, OwnerBoneBinding);
                _meshBoneBuffer = _gl.GenBuffer();
                _gl.BindBuffer(BufferTargetARB.UniformBuffer, _meshBoneBuffer);
                _gl.BufferData(
                    BufferTargetARB.UniformBuffer,
                    new ReadOnlySpan<float>(new float[GpuSkinningData.MaxBones * 16]),
                    BufferUsageARB.DynamicDraw);
                _gl.BindBufferBase(BufferTargetARB.UniformBuffer, OwnerBoneBinding, _meshBoneBuffer);
                _gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);
            }
        }

        internal bool HasEmitterMesh(float[] positions, bool skinning)
            => _ready && _meshResources?.Contains(positions, skinning) == true;

        public void UploadEmitterMesh(
            VfxPlaybackRuntime.EmitterState es,
            float[] positions,
            float[] normals,
            float[] uvs,
            float[] colors,
            uint[] indices = null,
            float[] boneIndices = null,
            float[] boneWeights = null)
        {
            if (!_ready) return;
            EnsureMeshProgram();
            _meshResources.Upload(es, positions, normals, uvs, colors, indices, boneIndices, boneWeights);
        }

        internal void SetOwnerSkinningMatrices(Matrix4x4[] matrices)
        {
            _ownerSkinningMatrices = matrices;
            _ownerSkinningCount = Math.Min(matrices?.Length ?? 0, GpuSkinningData.MaxBones);
            if (!_ready || _ownerSkinningCount == 0)
                return;

            EnsureMeshProgram();
            UploadMeshBonePalette(new ReadOnlySpan<Matrix4x4>(matrices, 0, _ownerSkinningCount));
        }

        internal void SetOwnerWorldTransform(Matrix4x4 transform)
            => _ownerWorldTransform = transform;

        private void UploadMeshBonePalette(ReadOnlySpan<Matrix4x4> matrices)
        {
            if (_meshBoneBuffer == 0 || matrices.Length == 0) return;
            int count = Math.Min(matrices.Length, GpuSkinningData.MaxBones);
            _gl.BindBuffer(BufferTargetARB.UniformBuffer, _meshBoneBuffer);
            _gl.BufferSubData(BufferTargetARB.UniformBuffer, 0, matrices[..count]);
            _gl.BindBufferBase(BufferTargetARB.UniformBuffer, OwnerBoneBinding, _meshBoneBuffer);
            _gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);
        }

        internal void SetOwnerHiddenSubmeshes(IEnumerable<uint> hashes)
        {
            _ownerHiddenSubmeshes.Clear();
            if (hashes == null) return;
            foreach (uint hash in hashes)
                _ownerHiddenSubmeshes.Add(hash);
        }

        private void ReleaseMeshes()
            => _meshResources.Clear();

        private void RenderMeshEmitter(
            VfxPlaybackRuntime.EmitterState es,
            Matrix4x4 viewProj,
            Vector3 camPos,
            Vector3 camUp,
            ReadOnlySpan<float> instances,
            int instanceCount,
            float sharedPalettePhase,
            bool wireframePass,
            float wireframeOpacity, int passIndex = 0)
        {
            if (es.MeshVao == 0 || es.MeshVertexCount == 0) return;
            bool isDistortion = es.Def.DrawsAsDistortion && !wireframePass;
            bool warpsFrame = isDistortion && es.Def.Distortion.Strength != 0f;
            if (warpsFrame && _capture.ColorTexture == 0) return;
            bool cullFace = _gl.IsEnabled(EnableCap.CullFace);
            EnsureMeshProgram();
            bool native = UseGameParticle(es, true, passIndex, wireframePass);
            _gl.Uniform1(_meshUniforms.WireframePass, wireframePass ? 1 : 0);
            _gl.Uniform4(
                _meshUniforms.WireframeColor,
                PreviewWireColor.X,
                PreviewWireColor.Y,
                PreviewWireColor.Z,
                Math.Clamp(wireframeOpacity, 0f, 1f));
            _gl.BindVertexArray(es.MeshVao);
            _gl.UniformMatrix4(_meshUniforms.ViewProj, 1, false, in viewProj.M11);
            _gl.UniformMatrix4(_meshUniforms.OwnerWorld, 1, false, in _ownerWorldTransform.M11);
            _gl.Uniform3(_meshUniforms.CamPos, camPos.X, camPos.Y, camPos.Z);
            _gl.Uniform3(_meshUniforms.CamUp, camUp.X, camUp.Y, camUp.Z);
            bool attachedMesh = es.Def.PrimitiveKind == VfxPrimitiveKind.AttachedMesh;
            bool useOwnerSkinning = attachedMesh && es.MeshHasSkinning && _ownerSkinningCount > 0;
            bool useParticleMeshSkinning = !attachedMesh && es.MeshHasSkinning && es.MeshAnimation is not null;
            bool useSkinning = useOwnerSkinning || useParticleMeshSkinning;
            _gl.Uniform1(_meshUniforms.UseSkinning, useSkinning ? 1 : 0);
            if (useOwnerSkinning && _ownerSkinningMatrices is { Length: > 0 })
                UploadMeshBonePalette(new ReadOnlySpan<Matrix4x4>(_ownerSkinningMatrices, 0, _ownerSkinningCount));

            // Direction-oriented mesh particles take precedence over camera alignment in LTK.
            bool cameraAlignedMesh = !attachedMesh && !es.Def.IsDirectionOriented &&
                (es.Def.MeshAlignPitchToCamera || es.Def.MeshAlignYawToCamera);
            _gl.Uniform1(_meshUniforms.AlignPitchToCamera, cameraAlignedMesh && es.Def.MeshAlignPitchToCamera ? 1 : 0);
            _gl.Uniform1(_meshUniforms.AlignYawToCamera, cameraAlignedMesh && es.Def.MeshAlignYawToCamera ? 1 : 0);
            _gl.Uniform1(_meshUniforms.MeshSkinned, es.Def.MeshIsSkinned ? 1 : 0);
            _gl.Uniform1(_meshUniforms.IsGroundLayer, es.Def.IsGroundLayer ? 1 : 0);
            _gl.Uniform1(_meshUniforms.Tex, 0);
            _gl.Uniform1(_meshUniforms.TexMult, 1);
            _gl.Uniform1(_meshUniforms.ColorMap, 7);
            _gl.Uniform1(_meshUniforms.PaletteMap, 8);
            _gl.Uniform1(_meshUniforms.ErosionTex, 4);
            _gl.Uniform1(_meshUniforms.ReflectionTex, 5);
            _gl.Uniform1(_meshUniforms.SceneDepthTex, 6);
            Vector2 texDiv = es.Def.TexDiv;
            _gl.Uniform2(_meshUniforms.TexDiv, texDiv.X <= 0f ? 1f : texDiv.X, texDiv.Y <= 0f ? 1f : texDiv.Y);
            _gl.Uniform2(_meshUniforms.TexSize, Math.Max(1f, es.TextureWidth), Math.Max(1f, es.TextureHeight));
            Vector2 uvCenter = es.Def.UvTransformCenter;
            _gl.Uniform2(_meshUniforms.UvTransformCenter, uvCenter.X, uvCenter.Y);
            _gl.Uniform1(_meshUniforms.HasTexMult, es.TextureMult != 0 ? 1 : 0);
            Vector2 textureMultTexDiv = es.Def.TextureMultTexDiv;
            _gl.Uniform2(
                _meshUniforms.TexDivMult,
                textureMultTexDiv.X <= 0f ? 1f : textureMultTexDiv.X,
                textureMultTexDiv.Y <= 0f ? 1f : textureMultTexDiv.Y);
            _gl.Uniform2(
                _meshUniforms.TexSizeMult,
                Math.Max(1f, es.TextureMultWidth),
                Math.Max(1f, es.TextureMultHeight));
            Vector2 uvCenterMult = es.Def.TextureMultTransformCenter;
            _gl.Uniform2(_meshUniforms.UvTransformCenterMult, uvCenterMult.X, uvCenterMult.Y);
            Vector2 emitterUvOffsetMult = VfxUvSemantics.Periodic(
                    es.Def.TextureMultEmitterUvScrollRate * es.RenderTime,
                    es.Def.TextureMultAddressMode);
            _gl.Uniform2(_meshUniforms.EmitterUvOffsetMult, emitterUvOffsetMult.X, emitterUvOffsetMult.Y);
            _gl.Uniform1(_meshUniforms.FlipUMult, es.Def.TextureMultFlipU ? 1 : 0);
            _gl.Uniform1(_meshUniforms.FlipVMult, es.Def.TextureMultFlipV ? 1 : 0);
            _gl.Uniform1(_meshUniforms.AddressModeMult, es.Def.TextureMultAddressMode);
            _gl.Uniform1(_meshUniforms.ClampUvMult, es.Def.TextureMultClampUvScroll ? 1 : 0);
            VfxAlphaErosionDefinition meshErosion = es.Def.AlphaErosion;
            bool meshErosionEnabled = meshErosion is not null;
            bool meshHasErosionMap = meshErosionEnabled && es.ErosionTexture != 0;
            Vector4 meshErosionDefault = meshErosion is not null && string.IsNullOrWhiteSpace(meshErosion.TexturePath)
                ? Vector4.One
                : Vector4.Zero;
            _gl.Uniform1(_meshUniforms.HasErosion, meshErosionEnabled ? 1 : 0);
            _gl.Uniform1(_meshUniforms.HasErosionMap, meshHasErosionMap ? 1 : 0);
            _gl.Uniform1(_meshUniforms.ErosionAddressMode, meshErosion?.AddressMode ?? 0);
            _gl.Uniform4(_meshUniforms.ErosionDefault, meshErosionDefault.X, meshErosionDefault.Y, meshErosionDefault.Z, meshErosionDefault.W);
            _gl.Uniform1(_meshUniforms.ErosionFeatherIn, meshErosion?.FeatherIn ?? 0f);
            _gl.Uniform1(_meshUniforms.ErosionFeatherOut, meshErosion?.FeatherOut ?? 0f);
            _gl.Uniform1(_meshUniforms.ErosionSliceWidth, meshErosion?.SliceWidth ?? 1.5f);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, es.Texture != 0 ? es.Texture : _textures.FallbackTransparentTexture);
            _gl.Uniform1(_meshUniforms.HasTex, ShouldSampleBaseTexture(es.Def, es.Texture) ? 1 : 0);
            var renderState = es.Def.RenderState ?? VfxEmitterRenderState.Default;
            ModelMaterialDefinition customMaterial = es.Def.HasResolvedCustomMaterial ? es.Def.CustomMaterial : null;
            ApplyCustomMaterialUniforms(
                customMaterial,
                _meshUniforms.UseCustomMaterial,
                _meshUniforms.MaterialTint,
                _meshUniforms.MaterialRepeat,
                _meshUniforms.MaterialAddressU,
                _meshUniforms.MaterialAddressV,
                _meshUniforms.MaterialPremultiplied);
            ApplyAddressMode(2);
            float alphaCutoff = customMaterial?.AlphaCutoff ?? renderState.AlphaCutoff;
            _gl.Uniform1(_meshUniforms.AlphaCutoff, alphaCutoff);
            _gl.Uniform1(
                _meshUniforms.AlphaTest,
                customMaterial is not null
                    ? (alphaCutoff > 0f ? 1 : 0)
                    : (VfxBlendModes.ShouldAlphaTest(es.Def.BlendMode, renderState.AlphaReference) ? 1 : 0));
            _gl.Uniform1(_meshUniforms.EmissiveStrength, VfxBlendModes.ResolveEmissiveStrength(es.Def.BlendMode));
            _gl.Uniform1(_meshUniforms.IsDistortion, isDistortion ? 1 : 0);
            _gl.Uniform1(_meshUniforms.DistortionStrength, es.Def.Distortion?.Strength ?? 0f);
            _gl.Uniform1(_meshUniforms.DistortionTex, 2);
            _gl.Uniform1(_meshUniforms.SceneTex, 3);
            if (warpsFrame)
            {
                _gl.ActiveTexture(TextureUnit.Texture2);
                _gl.BindTexture(
                    TextureTarget.Texture2D,
                    es.DistortionTexture != 0 ? es.DistortionTexture : _textures.FallbackTransparentTexture);
                ApplyAddressMode(2);
                ApplyTextureSampling();
                _gl.ActiveTexture(TextureUnit.Texture3);
                _gl.BindTexture(TextureTarget.Texture2D, _capture.ColorTexture);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            _gl.Uniform1(_meshUniforms.HasColor, 0);
            _gl.Uniform1(_meshUniforms.RampAtMult, 0);
            _gl.Uniform1(_meshUniforms.UvMode, es.Def.UvMode);
            _gl.Uniform1(
                _meshUniforms.ColorRenderFlags,
                VfxBlendModes.ResolveColorRenderFlags(
                    es.Def.ColorRenderFlags,
                    !string.IsNullOrWhiteSpace(es.Def.ParticleColorTexturePath)));
            VfxPaletteDefinition meshPalette = es.Def.PaletteDefinition;
            bool meshHasPalette = es.PaletteTexture != 0 && meshPalette is { PaletteCount: > 0 };
            _gl.Uniform1(_meshUniforms.HasPalette, meshHasPalette ? 1 : 0);
            _gl.Uniform1(_meshUniforms.PaletteCount, Math.Max(1, meshPalette?.PaletteCount ?? 1));
            _gl.Uniform1(_meshUniforms.PaletteAddressMode, meshPalette?.AddressMode ?? 0);
            _gl.Uniform1(_meshUniforms.PaletteSelector, PaletteSelectorAtZero(meshPalette));
            Vector4 meshPaletteMask = meshPalette?.PaletteSourceMixColor ?? Vector4.Zero;
            _gl.Uniform4(_meshUniforms.PaletteMixMask, meshPaletteMask.X, meshPaletteMask.Y, meshPaletteMask.Z, meshPaletteMask.W);
            Vector2 meshPaletteScroll = new(
                meshPalette?.ScrollU?.Sample(sharedPalettePhase) ?? 0f,
                meshPalette?.ScrollV?.Sample(sharedPalettePhase) ?? 0f);
            _gl.Uniform2(_meshUniforms.PaletteScroll, meshPaletteScroll.X, meshPaletteScroll.Y);
            _gl.Uniform1(_meshUniforms.ColorLookUpTypeX, es.Def.ColorLookUpTypeX ?? 0);
            _gl.Uniform1(_meshUniforms.ColorLookUpTypeY, es.Def.ColorLookUpTypeY ?? 0);
            Vector2 meshColorLookUpScales = es.Def.ColorLookUpScales;
            _gl.Uniform2(_meshUniforms.ColorLookUpScales, meshColorLookUpScales.X, meshColorLookUpScales.Y);
            _gl.Uniform2(_meshUniforms.ColorLookUpOffsets, es.Def.ColorLookUpOffsets.X, es.Def.ColorLookUpOffsets.Y);
            _gl.Uniform1(_meshUniforms.FlipU, renderState.FlipU ? 1 : 0);
            _gl.Uniform1(_meshUniforms.FlipV, renderState.FlipV ? 1 : 0);
            _gl.Uniform1(_meshUniforms.AddressMode, renderState.TextureAddressMode);
            _gl.Uniform1(_meshUniforms.ClampUv, renderState.ClampUvScroll ? 1 : 0);
            ApplyTextureSampling();
            if (es.TextureMult != 0)
            {
                _gl.ActiveTexture(TextureUnit.Texture1);
                _gl.BindTexture(TextureTarget.Texture2D, es.TextureMult);
                ApplyAddressMode(2);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            if (es.ErosionTexture != 0)
            {
                _gl.ActiveTexture(TextureUnit.Texture4);
                _gl.BindTexture(TextureTarget.Texture2D, es.ErosionTexture);
                ApplyAddressMode(2);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            VfxReflectionDefinition reflection = es.Def.Reflection;
            bool hasReflectionCube = reflection is not null && es.ReflectionTexture != 0;
            _gl.Uniform1(_meshUniforms.HasReflection, hasReflectionCube ? 1 : 0);
            _gl.Uniform1(_meshUniforms.AttachedMesh, attachedMesh ? 1 : 0);

            Vector4 fresnelColor = reflection?.FresnelColor ?? Vector4.Zero;
            _gl.Uniform4(
                _meshUniforms.Fresnel,
                fresnelColor.X,
                fresnelColor.Y,
                fresnelColor.Z,
                reflection?.Fresnel ?? 1f);
            _gl.Uniform4(
                _meshUniforms.Reflection,
                reflection?.ReflectionFresnel ?? 1f,
                reflection?.DirectOpacity ?? 0f,
                reflection?.GlancingOpacity ?? 1f,
                0f);
            Vector4 reflectionColor = reflection?.ReflectionFresnelColor ?? Vector4.One;
            _gl.Uniform4(
                _meshUniforms.ReflectionColor,
                reflectionColor.X,
                reflectionColor.Y,
                reflectionColor.Z,
                reflectionColor.W);
            if (hasReflectionCube)
            {
                _gl.ActiveTexture(TextureUnit.Texture5);
                _gl.BindTexture(TextureTarget.TextureCubeMap, es.ReflectionTexture);
                _gl.ActiveTexture(TextureUnit.Texture0);
            }
            bool meshUsesSoftParticles = ShouldUseSoftParticles(es.Def, _capture.DepthTexture != 0);
            _gl.Uniform1(_meshUniforms.HasSoftParticle, meshUsesSoftParticles ? 1 : 0);
            Vector4 meshSoftParams = ResolveSoftParticleParams(es.Def.SoftParticle);
            Vector4 meshSoftControl = ResolveSoftParticleControl(es.Def.BlendMode);
            _gl.Uniform4(_meshUniforms.SoftParticleParams, meshSoftParams.X, meshSoftParams.Y, meshSoftParams.Z, meshSoftParams.W);
            _gl.Uniform4(_meshUniforms.SoftParticleControl, meshSoftControl.X, meshSoftControl.Y, meshSoftControl.Z, meshSoftControl.W);
            _gl.Uniform2(_meshUniforms.DepthProjection, _depthProjectionValue.X, _depthProjectionValue.Y);
            _gl.Uniform2(_meshUniforms.ViewportSize, (float)_capture.Width, (float)_capture.Height);
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
            ApplyAddressMode(2);
            ApplyTextureSampling();
            _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + 8));
            _gl.BindTexture(TextureTarget.Texture2D, es.PaletteTexture != 0
                ? es.PaletteTexture
                : _textures.FallbackTransparentTexture);
            ApplyAddressMode(2);
            ApplyTextureSampling();
            _gl.ActiveTexture(TextureUnit.Texture0);
            if (wireframePass)
            {
                // LTK's wire twin is double-sided and alpha-blended independently from the
                // authored material, so mesh edges cannot inherit culling or additive modes.
                _gl.Disable(EnableCap.CullFace);
                _gl.DepthMask(false);
                _gl.DepthFunc(DepthFunction.Lequal);
                ApplyWireframeBlend();
            }
            else
            {
                if (customMaterial is not null)
                {
                    ApplyCustomMaterialCull(customMaterial.RenderState);
                }
                else
                {
                    // Riot meshes cull backfaces unless the authored emitter explicitly opts out.
                    if (es.Def.RenderState?.DisableBackfaceCull == true)
                        _gl.Disable(EnableCap.CullFace);
                    else
                    {
                        _gl.Enable(EnableCap.CullFace);
                        _gl.CullFace(TriangleFace.Back);
                    }
                }
                ApplyEmitterBlendState(es.Def, isDistortion);
            }

            Vector2 emitterUvOffset = VfxUvSemantics.Periodic(
                    es.Def.EmitterUvScrollRate * es.RenderTime,
                    renderState.TextureAddressMode);
            _gl.Uniform2(_meshUniforms.EmitterUvOffset, emitterUvOffset.X, emitterUvOffset.Y);
            for (int i = 0; i < instanceCount; i++)
            {
                int o = i * Stride;
                _gl.Uniform3(_meshUniforms.WorldPos, instances[o], instances[o + 1], instances[o + 2]);
                _gl.Uniform3(_meshUniforms.PlacementRight, instances[o + 36], instances[o + 37], instances[o + 38]);
                _gl.Uniform3(_meshUniforms.PlacementUp, instances[o + 39], instances[o + 40], instances[o + 41]);
                _gl.Uniform3(_meshUniforms.PlacementForward, instances[o + 42], instances[o + 43], instances[o + 44]);
                Vector3 orbitRotation = i < es.Particles.Count
                    ? es.Particles[i].BirthOrbitalVelocity * es.Particles[i].Age
                    : Vector3.Zero;
                _gl.Uniform3(_meshUniforms.OrbitRotation, orbitRotation.X, orbitRotation.Y, orbitRotation.Z);
                float ownerScale = attachedMesh && float.IsFinite(es.MeshOwnerScale) && es.MeshOwnerScale > 0f
                    ? es.MeshOwnerScale
                    : 1f;
                float scaleX = ClampScale(instances[o + 3]) * ownerScale;
                float scaleY = ClampScale(instances[o + 4]) * ownerScale;
                float scaleZ = ClampScale(instances[o + 18]) * ownerScale;
                _gl.Uniform3(_meshUniforms.Scale, scaleX, scaleY, scaleZ);
                Vector3 meshRotation = new(
                    instances[o + 15],
                    instances[o + 16],
                    instances[o + 17]);
                _gl.Uniform3(
                    _meshUniforms.Rotation,
                    meshRotation.X,
                    meshRotation.Y,
                    meshRotation.Z);
                _gl.Uniform3(_meshUniforms.GameLookupDrivers, instances[o + 11],
                    new Vector3(instances[o + 12], instances[o + 13], instances[o + 14]).Length(), instances[o + 35]);
                _gl.Uniform4(_meshUniforms.Color, instances[o + 5], instances[o + 6], instances[o + 7], instances[o + 8]);
                _gl.Uniform2(_meshUniforms.BirthUvOffset, instances[o + 19], instances[o + 20]);
                _gl.Uniform2(_meshUniforms.UvScale, instances[o + 21], instances[o + 22]);
                _gl.Uniform1(_meshUniforms.UvRotation, instances[o + 23]);
                _gl.Uniform1(_meshUniforms.ErosionDrive, instances[o + 24]);
                _gl.Uniform4(_meshUniforms.ErosionMixer, instances[o + 25], instances[o + 26], instances[o + 27], instances[o + 28]);
                _gl.Uniform2(_meshUniforms.UvOffsetMult, instances[o + 29], instances[o + 30]);
                _gl.Uniform2(_meshUniforms.UvScaleMult, instances[o + 31], instances[o + 32]);
                _gl.Uniform1(_meshUniforms.UvRotationMult, instances[o + 33]);
                _gl.Uniform1(_meshUniforms.Frame, instances[o + 10]);

                if (useParticleMeshSkinning && i < es.Particles.Count)
                    UploadMeshBonePalette(es.MeshAnimation.EvaluatePalette(es.Particles[i].Age));

                if (native) BindGameParticle(es, true, passIndex, sharedPalettePhase);
                if (es.MeshIndexCount > 0)
                {
                    if (_drawElements != null)
                    {
                        if (attachedMesh && es.MeshRanges is { Length: > 0 })
                        {
                            bool narrowed = HasAttachedDrawMatch(es.MeshRanges, es.Def.SubmeshesToDraw);
                            foreach (VfxMeshRangeData range in es.MeshRanges)
                            {
                                if (!ShouldDrawAttachedRange(
                                        range.Hash,
                                        narrowed,
                                        es.Def.SubmeshesToDraw,
                                        es.Def.SubmeshesToDrawAlways,
                                        _ownerHiddenSubmeshes))
                                {
                                    continue;
                                }

                                if (native) _gameShaders.DrawBoundPass(_drawElements, range.IndexCount, new IntPtr(range.StartIndex * sizeof(uint)));
                                else _drawElements((uint)PrimitiveType.Triangles, range.IndexCount, (uint)DrawElementsType.UnsignedInt,
                                    new IntPtr(range.StartIndex * sizeof(uint)));
                            }
                        }
                        else
                        {
                            if (native) _gameShaders.DrawBoundPass(_drawElements, es.MeshIndexCount, IntPtr.Zero);
                            else _drawElements((uint)PrimitiveType.Triangles, es.MeshIndexCount, (uint)DrawElementsType.UnsignedInt, IntPtr.Zero);
                        }
                    }
                }
                else if (native) _gameShaders.DrawBoundArrays(PrimitiveType.Triangles, es.MeshVertexCount);
                else _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)es.MeshVertexCount);
            }
            if (cullFace) _gl.Enable(EnableCap.CullFace);
            else _gl.Disable(EnableCap.CullFace);
            _meshUniforms = _stockMeshUniforms;
            _particleUniforms = _stockParticleUniforms;
            _gl.UseProgram(_program);
            _gl.BindVertexArray(_vao);
        }

        internal static float PaletteSelectorAtZero(VfxPaletteDefinition palette)
            => VfxShaderParameterUtils.SamplePaletteSelectorAtZero(palette);

        internal static float PaletteRowNormalized(VfxPaletteDefinition palette)
            => VfxShaderParameterUtils.ResolvePaletteRowNormalized(palette);

        internal static Vector3 ResolveCameraForward(Vector3 cameraRight, Vector3 cameraUp)
            => VfxGeometryUtils.ResolveCameraForward(cameraRight, cameraUp);

        internal static bool ShouldDirectionOrientBillboard(VfxEmitterDefinition definition)
            => VfxGeometryUtils.ShouldDirectionOrientBillboard(definition);

        internal static int ResolveEmitterDrawCount(
            VfxEmitterDefinition definition,
            int alreadyUsed,
            int instanceCount)
        {
            if (definition is null || instanceCount <= 0) return 0;

            int used = Math.Max(0, alreadyUsed);
            if (definition.DrawsAsTrail)
            {
                if (used >= VfxTrailGeometry.TrailPointsPerEmitter) return 0;
                int perSource = VfxTrailGeometry.ResolvePointCount(instanceCount);
                int remaining = VfxTrailGeometry.TrailPointsPerEmitter - used;
                // ribbon.writeTrail rejects a whole strand when its vertices do not fit. It
                // never truncates that source merely to consume the tail of the shared buffer;
                // a later, smaller source may still fit into the same remaining capacity.
                return perSource <= remaining ? perSource : 0;
            }

            int limit = definition.PrimitiveKind == VfxPrimitiveKind.AttachedMesh
                ? AttachedMeshesPerEmitter
                : definition.IsMeshPrimitive
                    ? MeshesPerEmitter
                    : definition.DrawsAsBeam
                        ? BeamsPerEmitter
                        : definition.DrawsAsQuad || definition.DrawsAsProjection
                            ? QuadsPerEmitter
                            : int.MaxValue;

            if (limit == int.MaxValue) return instanceCount;
            if (used >= limit) return 0;
            return Math.Min(instanceCount, limit - used);
        }

        internal static int ResolveAttachedMeshDrawCount(int alreadyUsed, int instanceCount)
        {
            if (instanceCount <= 0 || alreadyUsed >= AttachedMeshesPerEmitter) return 0;
            return Math.Min(instanceCount, Math.Max(0, AttachedMeshesPerEmitter - Math.Max(0, alreadyUsed)));
        }

        internal static bool ShouldSortInstances(VfxEmitterDefinition definition, int instanceCount)
            => definition is not null &&
               instanceCount > 1 &&
               definition.DrawsAsQuad &&
               (definition.HasResolvedCustomMaterial
                   ? definition.CustomMaterial.RenderState.Blending != ModelMaterialBlendMode.Opaque
                   : VfxBlendModes.ShouldSortBackToFront(definition.BlendMode));

        private static float ClampScale(float value)
            => float.IsFinite(value) ? value : 1f;

        private static bool HasAttachedDrawMatch(
            IReadOnlyList<VfxMeshRangeData> ranges,
            IReadOnlyList<uint> draw)
        {
            if (ranges == null || draw == null || draw.Count == 0) return false;
            for (int rangeIndex = 0; rangeIndex < ranges.Count; rangeIndex++)
            {
                if (ContainsHash(draw, ranges[rangeIndex].Hash)) return true;
            }
            return false;
        }

        private static bool ContainsHash(IReadOnlyList<uint> values, uint hash)
        {
            if (values == null) return false;
            for (int index = 0; index < values.Count; index++)
            {
                if (values[index] == hash) return true;
            }
            return false;
        }

        internal static bool ShouldDrawAttachedRange(
            uint hash,
            bool narrowed,
            IReadOnlyList<uint> draw,
            IReadOnlyList<uint> always,
            ISet<uint> hidden)
            => ((!narrowed || ContainsHash(draw, hash)) && !(hidden?.Contains(hash) ?? false)) ||
               ContainsHash(always, hash);

        internal static bool ShouldSampleBaseTexture(VfxEmitterDefinition definition, uint textureHandle)
            => VfxGeometryUtils.ShouldSampleBaseTexture(definition, textureHandle);

        internal static float ResolveEmitterPhase(VfxEmitterDefinition definition, float age)
            => VfxGeometryUtils.ResolveEmitterPhase(definition, age);

        internal static Vector2? ResolvePolygonOffset(VfxEmitterDefinition definition)
            => VfxGeometryUtils.ResolvePolygonOffset(definition);

        internal static bool HasTextureMultLayer(VfxEmitterDefinition definition)
            => VfxGeometryUtils.HasTextureMultLayer(definition);

        internal static bool ShouldUseColorRamp(VfxEmitterDefinition definition, bool hasColorRampTexture)
            => VfxGeometryUtils.ShouldUseColorRamp(definition, hasColorRampTexture);

        internal static bool ShouldProjectToGround(VfxEmitterDefinition definition)
            => VfxGeometryUtils.ShouldProjectToGround(definition);

        internal static bool ShouldUseSoftParticles(VfxEmitterDefinition definition, bool hasSceneDepth)
            => VfxGeometryUtils.ShouldUseSoftParticles(definition, hasSceneDepth);

        internal static Vector4 ResolveSoftParticleParams(VfxSoftParticleDefinition soft)
            => VfxShaderParameterUtils.ResolveSoftParticleParams(soft);

        internal static Vector4 ResolveSoftParticleControl(int blendMode)
            => VfxShaderParameterUtils.ResolveSoftParticleControl(blendMode);
    }
}
