using System;
using AssetsManager.Utils;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using AssetsManager.Utils.Rendering;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Runtime;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    /// <summary>
    /// Draws effect billboards and mesh primitives from prepared playback state.
    /// </summary>
    public sealed partial class VfxOpenGlRenderer : IDisposable
    {
        private GL _gl = null!;
        private GlDrawBindings _drawBindings;
        internal bool CacheDrawBindings { get; set; } = true;
        internal (int Programs, int UniformBuffers) LastDrawBindingCounts =>
            (_drawBindings.ProgramBindCount, _drawBindings.UniformBufferBindCount);
        private VfxShaderUniforms _particleUniforms, _stockParticleUniforms, _meshUniforms, _stockMeshUniforms;
        private uint _program, _vao, _quadVbo, _instVbo, _trailVao, _trailVbo;
        private readonly VfxTrailGeometry _trailGeometry = new();
        private readonly VfxBeamGeometry _beamGeometry = new();
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
        private Vector4 _depthProjectionValue;
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
            _drawBindings = new GlDrawBindings(gl);
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
            _depthProjectionValue = new Vector4(projection.M33, projection.M43, projection.M34, projection.M44);

            GlStateSnapshot ownedState = _renderBatchDepth == 0 ? CaptureGlState() : null;

            ResetEmitterDrawScratch();
            try
            {
            _drawBindings.Begin(CacheDrawBindings);
            _gameShaders.ParticleDrawBindings = _drawBindings;
            if (useWireframe)
                _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);

            _drawBindings.UseProgram(_program);
            _gl.UniformMatrix4(_particleUniforms.ViewProj, 1, false, in viewProj.M11);
            _particleUniforms.Uniform3(_particleUniforms.CamRight, camRight.X, camRight.Y, camRight.Z);
            _particleUniforms.Uniform3(_particleUniforms.CamUp, camUp.X, camUp.Y, camUp.Z);
            _particleUniforms.Uniform3(_particleUniforms.CamPos, camPos.X, camPos.Y, camPos.Z);
            _particleUniforms.Uniform1(_particleUniforms.Tex, 0);
            _particleUniforms.Uniform1(_particleUniforms.TexMult, 1);
            _particleUniforms.Uniform1(_particleUniforms.ColorMap, 7);
            _particleUniforms.Uniform1(_particleUniforms.PaletteMap, 8);
            _particleUniforms.Uniform1(_particleUniforms.SceneTex, 2);
            _particleUniforms.Uniform1(_particleUniforms.DistortionTex, 3);
            _particleUniforms.Uniform1(_particleUniforms.ErosionTex, 4);
            _particleUniforms.Uniform1(_particleUniforms.SceneDepthTex, 6);
            _particleUniforms.Uniform2(_particleUniforms.ViewportSize, (float)_capture.Width, (float)_capture.Height);
            _particleUniforms.Uniform4(_particleUniforms.DepthProjection, _depthProjectionValue.X, _depthProjectionValue.Y,
                _depthProjectionValue.Z, _depthProjectionValue.W);
            _particleUniforms.Uniform1(_particleUniforms.WireframePass, useWireframe ? 1 : 0);
            _particleUniforms.Uniform4(
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

            _gameFrame = new AssetsManager.Services.Viewer.Rendering.GameShaders.GameShaderRuntime.Frame(view, projection, camPos, 0f, Sun);
            _gameViewProjection = view * projection;
            _gameCameraRight = camRight;
            _gameCameraUp = camUp;
            _cameraPrograms.Clear();

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
                _gameShaders.ParticleDrawBindings = null;
                _drawBindings.End();
                ResetEmitterDrawScratch();
                // Each emitter establishes its draw state. Restore the caller at the batch boundary,
                // avoiding a driver query and an unnecessary stock-program switch after each draw.
                _particleUniforms = _stockParticleUniforms;
                _meshUniforms = _stockMeshUniforms;
                if (useWireframe)
                    _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);

                if (ownedState != null)
                    RestoreGlState(ownedState);
            }
        }


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
            if (_projectionTerrainProgram != 0) _gl.DeleteProgram(_projectionTerrainProgram);
            _projectionTerrainProgram = 0;
            _terrainDepthTexture = 0;
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
            _ready = false;
        }

        private const uint OwnerBoneBinding = VfxShaderSource.BoneTransformsBinding;
        private uint _meshProgram;
        private uint _meshBoneBuffer;
        private Matrix4x4[] _ownerSkinningMatrices;
        private Matrix4x4 _ownerWorldTransform = Matrix4x4.Identity;
        private int _ownerSkinningCount;

    }
}
