using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    /// <summary>
    /// Draws effect billboards and mesh primitives from prepared playback state.
    /// </summary>
    public sealed class VfxOpenGlRenderer : IDisposable
    {
        private GL _gl = null!;
        private uint _program, _vao, _quadVbo, _instVbo, _trailVao, _trailVbo;
        private readonly VfxTrailGeometry _trailGeometry = new();
        private readonly VfxBeamGeometry _beamGeometry = new();
        private int _uViewProj, _uCamRight, _uCamUp, _uCamPos, _uDepthPushPull, _uTexDiv, _uTexSize, _uTex, _uHasTex, _uEmitterUvOffset;
        private int _uTexMult, _uHasTexMult, _uTexDivMult, _uTexSizeMult, _uUvScrollRateMult, _uFlipUMult, _uFlipVMult;
        private int _uUvTransformCenter, _uUvTransformCenterMult, _uAddressMode, _uAddressModeMult, _uClampUvMult;
        private int _uIsDistortion, _uDistortionTex, _uSceneTex, _uViewportSize, _uDistortionStrength;
        private int _uSceneDepthTex, _uHasSoftParticle, _uSoftParticleParams, _uSoftParticleControl, _uDepthProjection;
        private int _uDirectionOriented, _uArbitraryQuad, _uLegacyOrientation, _uPivotUp;
        private int _uPrimitiveKind;
        private int _uAlphaCutoff, _uAlphaTest, _uEmissiveStrength, _uFlipU, _uFlipV, _uClampUv;
        private int _uColorMap, _uHasColor, _uRampAtMult, _uUvMode, _uColorRenderFlags;
        private int _uPaletteMap, _uHasPalette, _uPaletteCount, _uPaletteAddressMode, _uPaletteMixMask, _uPaletteScroll;
        private int _uColorLookUpTypeX, _uColorLookUpTypeY, _uColorLookUpScales, _uColorLookUpOffsets;
        private int _uErosionTex, _uHasErosion, _uHasErosionMap, _uErosionAddressMode, _uErosionDefault;
        private int _uErosionFeatherIn, _uErosionFeatherOut, _uErosionSliceWidth;
        private int _uPlacementRight, _uPlacementUp, _uPlacementForward, _uIsGroundLayer;
        private int _instCapFloats;
        private bool _ready;
        private VfxTextureResourceCache _textures = null!;
        private VfxSceneCapture _capture = null!;
        private VfxMeshResourceCache _meshResources = null!;
        private float[] _groupedInstances = Array.Empty<float>();
        private float[] _sortedInstances = Array.Empty<float>();
        private float[] _instanceDepths = Array.Empty<float>();
        private int[] _instanceOrder = Array.Empty<int>();
        private readonly Dictionary<uint, byte> _stencilReferences = new();
        private readonly bool[] _reservedStencilReferences = new bool[256];
        private readonly HashSet<uint> _ownerHiddenSubmeshes = new();
        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
        private delegate void DrawElementsDelegate(uint mode, int count, uint type, IntPtr indices);
        private DrawElementsDelegate _drawElements = null!;
        private const int Stride = VfxPlaybackRuntime.InstanceStride;
        private const int QuadsPerEmitter = 4096;
        private const int MeshesPerEmitter = 512;
        private const int BeamsPerEmitter = 256;
        private const int AttachedMeshesPerEmitter = 8;
        private bool _gles;
        private Vector2 _depthProjectionValue;
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
            _uCamPos = gl.GetUniformLocation(_program, "uCamPos");
            _uDepthPushPull = gl.GetUniformLocation(_program, "uDepthPushPull");
            _uTexDiv = gl.GetUniformLocation(_program, "uTexDiv");
            _uTexSize = gl.GetUniformLocation(_program, "uTexSize");
            _uTex = gl.GetUniformLocation(_program, "uTex");
            _uHasTex = gl.GetUniformLocation(_program, "uHasTex");
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
            _uSoftParticleControl = gl.GetUniformLocation(_program, "uSoftParticleControl");
            _uDepthProjection = gl.GetUniformLocation(_program, "uDepthProjection");
            _uDirectionOriented = gl.GetUniformLocation(_program, "uDirectionOriented");
            _uArbitraryQuad = gl.GetUniformLocation(_program, "uArbitraryQuad");
            _uLegacyOrientation = gl.GetUniformLocation(_program, "uLegacyOrientation");
            _uPivotUp = gl.GetUniformLocation(_program, "uPivotUp");
            _uPrimitiveKind = gl.GetUniformLocation(_program, "uPrimitiveKind");
            _uAlphaCutoff = gl.GetUniformLocation(_program, "uAlphaCutoff");
            _uAlphaTest = gl.GetUniformLocation(_program, "uAlphaTest");
            _uEmissiveStrength = gl.GetUniformLocation(_program, "uEmissiveStrength");
            _uColorMap = gl.GetUniformLocation(_program, "uColorMap");
            _uHasColor = gl.GetUniformLocation(_program, "uHasColor");
            _uRampAtMult = gl.GetUniformLocation(_program, "uRampAtMult");
            _uUvMode = gl.GetUniformLocation(_program, "uUvMode");
            _uColorRenderFlags = gl.GetUniformLocation(_program, "uColorRenderFlags");
            _uPaletteMap = gl.GetUniformLocation(_program, "uPaletteMap");
            _uHasPalette = gl.GetUniformLocation(_program, "uHasPalette");
            _uPaletteCount = gl.GetUniformLocation(_program, "uPaletteCount");
            _uPaletteAddressMode = gl.GetUniformLocation(_program, "uPaletteAddressMode");
            _uPaletteMixMask = gl.GetUniformLocation(_program, "uPaletteMixMask");
            _uPaletteScroll = gl.GetUniformLocation(_program, "uPaletteScroll");
            _uColorLookUpTypeX = gl.GetUniformLocation(_program, "uColorLookUpTypeX");
            _uColorLookUpTypeY = gl.GetUniformLocation(_program, "uColorLookUpTypeY");
            _uColorLookUpScales = gl.GetUniformLocation(_program, "uColorLookUpScales");
            _uColorLookUpOffsets = gl.GetUniformLocation(_program, "uColorLookUpOffsets");
            _uFlipU = gl.GetUniformLocation(_program, "uFlipU");
            _uFlipV = gl.GetUniformLocation(_program, "uFlipV");
            _uClampUv = gl.GetUniformLocation(_program, "uClampUv");
            _uErosionTex = gl.GetUniformLocation(_program, "uErosionTex");
            _uHasErosion = gl.GetUniformLocation(_program, "uHasErosion");
            _uHasErosionMap = gl.GetUniformLocation(_program, "uHasErosionMap");
            _uErosionAddressMode = gl.GetUniformLocation(_program, "uErosionAddressMode");
            _uErosionDefault = gl.GetUniformLocation(_program, "uErosionDefault");
            _uErosionFeatherIn = gl.GetUniformLocation(_program, "uErosionFeatherIn");
            _uErosionFeatherOut = gl.GetUniformLocation(_program, "uErosionFeatherOut");
            _uErosionSliceWidth = gl.GetUniformLocation(_program, "uErosionSliceWidth");
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
            _capture = new VfxSceneCapture(gl);
            _meshResources = new VfxMeshResourceCache(gl);
            _ready = true;
        }

        public uint UploadTexture(byte[] bgra, int width, int height)
            => _textures.Upload(bgra, width, height);

        internal uint UploadCubeMap(VfxCubeMapData cube)
            => _textures.UploadCube(cube);

        public void CaptureScene(uint width, uint height, bool captureColor, bool captureDepth)
            => _capture.Capture(width, height, captureColor, captureDepth);

        public void Render(IReadOnlyList<VfxRenderQueueEntry> renderQueue, Matrix4x4 viewProj, Matrix4x4 view,
            IReadOnlyList<VfxRenderQueueEntry> stencilQueue = null)
        {
            if (!_ready || renderQueue is null || renderQueue.Count == 0) return;

            Matrix4x4.Invert(view, out var inv);
            var camRight = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, inv));
            var camUp = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, inv));
            var camPos = inv.Translation;
            Matrix4x4 projection = inv * viewProj;
            _depthProjectionValue = new Vector2(projection.M33, projection.M43);

            bool depthTest = _gl.IsEnabled(EnableCap.DepthTest);
            bool cullFace = _gl.IsEnabled(EnableCap.CullFace);
            bool polygonOffset = _gl.IsEnabled(EnableCap.PolygonOffsetFill);
            bool blend = _gl.IsEnabled(EnableCap.Blend);
            bool stencilTest = _gl.IsEnabled(EnableCap.StencilTest);
            _gl.GetInteger(GLEnum.DepthWritemask, out int depthWrite);
            _gl.GetInteger(GLEnum.DepthFunc, out int depthFunction);
            _gl.GetInteger(GLEnum.BlendSrcRgb, out int blendSource);
            _gl.GetInteger(GLEnum.BlendDstRgb, out int blendDestination);
            _gl.GetInteger(GLEnum.BlendSrcAlpha, out int blendSourceAlpha);
            _gl.GetInteger(GLEnum.BlendDstAlpha, out int blendDestinationAlpha);
            _gl.GetInteger(GLEnum.BlendEquationRgb, out int blendEquation);
            _gl.GetInteger(GLEnum.BlendEquationAlpha, out int blendEquationAlpha);
            _gl.GetInteger(GLEnum.StencilFunc, out int stencilFrontFunction);
            _gl.GetInteger(GLEnum.StencilRef, out int stencilFrontReference);
            _gl.GetInteger(GLEnum.StencilValueMask, out int stencilFrontValueMask);
            _gl.GetInteger(GLEnum.StencilWritemask, out int stencilFrontWriteMask);
            _gl.GetInteger(GLEnum.StencilFail, out int stencilFrontFail);
            _gl.GetInteger(GLEnum.StencilPassDepthFail, out int stencilFrontDepthFail);
            _gl.GetInteger(GLEnum.StencilPassDepthPass, out int stencilFrontDepthPass);
            _gl.GetInteger(GLEnum.StencilBackFunc, out int stencilBackFunction);
            _gl.GetInteger(GLEnum.StencilBackRef, out int stencilBackReference);
            _gl.GetInteger(GLEnum.StencilBackValueMask, out int stencilBackValueMask);
            _gl.GetInteger(GLEnum.StencilBackWritemask, out int stencilBackWriteMask);
            _gl.GetInteger(GLEnum.StencilBackFail, out int stencilBackFail);
            _gl.GetInteger(GLEnum.StencilBackPassDepthFail, out int stencilBackDepthFail);
            _gl.GetInteger(GLEnum.StencilBackPassDepthPass, out int stencilBackDepthPass);
            Span<int> colorWriteMask = stackalloc int[4];
            _gl.GetInteger(GLEnum.ColorWritemask, colorWriteMask);
            _gl.GetInteger(GLEnum.CurrentProgram, out int program);
            _gl.GetInteger(GLEnum.VertexArrayBinding, out int vertexArray);
            _gl.GetInteger(GLEnum.ArrayBufferBinding, out int arrayBuffer);
            _gl.GetInteger(GLEnum.ActiveTexture, out int activeTexture);
            var textureBindings = new int[9];
            var cubeBindings = new int[9];
            for (int unit = 0; unit < textureBindings.Length; unit++)
            {
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
                _gl.GetInteger(GLEnum.TextureBinding2D, out textureBindings[unit]);
                _gl.GetInteger(GLEnum.TextureBindingCubeMap, out cubeBindings[unit]);
            }

            try
            {
            IReadOnlyDictionary<uint, byte> stencilReferences = BuildStencilReferenceMap(stencilQueue ?? renderQueue);
            _gl.UseProgram(_program);
            _gl.UniformMatrix4(_uViewProj, 1, false, in viewProj.M11);
            _gl.Uniform3(_uCamRight, camRight.X, camRight.Y, camRight.Z);
            _gl.Uniform3(_uCamUp, camUp.X, camUp.Y, camUp.Z);
            _gl.Uniform3(_uCamPos, camPos.X, camPos.Y, camPos.Z);
            _gl.Uniform1(_uTex, 0);
            _gl.Uniform1(_uTexMult, 1);
            _gl.Uniform1(_uColorMap, 7);
            _gl.Uniform1(_uPaletteMap, 8);
            _gl.Uniform1(_uSceneTex, 2);
            _gl.Uniform1(_uDistortionTex, 3);
            _gl.Uniform1(_uErosionTex, 4);
            _gl.Uniform1(_uSceneDepthTex, 6);
            _gl.Uniform2(_uViewportSize, (float)_capture.Width, (float)_capture.Height);
            _gl.Uniform2(_uDepthProjection, _depthProjectionValue.X, _depthProjectionValue.Y);

            _gl.BindVertexArray(_vao);
            _gl.ActiveTexture(TextureUnit.Texture0);

            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthMask(false);
            _gl.Disable(EnableCap.CullFace);
            _gl.Disable(EnableCap.PolygonOffsetFill);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendEquation(GLEnum.FuncAdd);

            var emitterUsed = new Dictionary<(object Graph, string Path, int SourceOrder), int>();
            var firstSourceByEmitter = new Dictionary<(object Graph, string Path, int SourceOrder), VfxPlaybackRuntime.EmitterState>();
            var sortedQuadGroups = new Dictionary<(object Graph, string Path, int SourceOrder), List<VfxPlaybackRuntime.EmitterState>>();
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
                    sources = new List<VfxPlaybackRuntime.EmitterState>();
                    sortedQuadGroups[key] = sources;
                }
                sources.Add(candidateEmitter);
            }
            var renderedSortedQuadGroups = new HashSet<(object Graph, string Path, int SourceOrder)>();

            foreach (VfxRenderQueueEntry entry in renderQueue)
            {
                VfxPlaybackRuntime.EmitterState es = entry.Emitter;
                if (es.InstanceCount == 0) continue;
                if (!es.IsVisible) continue;
                VfxEmitterRenderState emitterRenderState = es.Def.RenderState ?? VfxEmitterRenderState.Default;
                ApplyColorWriteMask(emitterRenderState);
                ApplyEmitterStencilState(emitterRenderState, stencilReferences);
                // Never synthesize an AttachedMesh proxy. Render only geometry that was
                // resolved from the real owner scene and filtered by authored submesh masks.
                if (es.Def.IsMeshPrimitive && es.MeshVao == 0)
                    continue;

                // LTK allocates one draw component per graph/path/emitter definition. Every live
                // source of that child path shares the same fixed draw budget and palette phase.
                var emitterKey = (es.RenderGraphKey ?? es, es.RenderPath ?? string.Empty, es.SourceOrder);
                VfxPlaybackRuntime.EmitterState paletteSource = firstSourceByEmitter.GetValueOrDefault(emitterKey) ?? es;
                float sharedPalettePhase = ResolveEmitterPhase(es.Def, paletteSource.EmitterAge);
                int renderInstanceCount;
                ReadOnlySpan<float> instancesSpan;
                if (sortedQuadGroups.TryGetValue(emitterKey, out List<VfxPlaybackRuntime.EmitterState> quadSources))
                {
                    // Quads.tsx gathers every live source first and sorts the combined set once.
                    if (!renderedSortedQuadGroups.Add(emitterKey)) continue;
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
                    emitterUsed[emitterKey] = renderInstanceCount;
                }
                else
                {
                    int alreadyUsed = emitterUsed.GetValueOrDefault(emitterKey);
                    renderInstanceCount = ResolveEmitterDrawCount(es.Def, alreadyUsed, es.InstanceCount);
                    if (renderInstanceCount == 0) continue;
                    emitterUsed[emitterKey] = alreadyUsed + renderInstanceCount;
                    instancesSpan = new ReadOnlySpan<float>(es.Instances, 0, renderInstanceCount * Stride);
                }

                bool attachedMesh = es.Def.PrimitiveKind == VfxPrimitiveKind.AttachedMesh;
                int floats = renderInstanceCount * Stride;

                if (es.Def.IsMeshPrimitive && es.MeshVao != 0)
                {
                    ApplyEmitterDepthState(es.Def, isDistortion: es.Def.Distortion != null);
                    ApplyBlendMode(es.Def.BlendMode, distortion: es.Def.Distortion != null);
                    RenderMeshEmitter(es, viewProj, camPos, camUp, instancesSpan, renderInstanceCount, sharedPalettePhase);
                    continue;
                }
                if (!es.Def.IsVisual) continue;
                bool isDistortion = es.Def.Distortion is not null;
                bool warpsFrame = isDistortion && es.Def.Distortion.Strength != 0f;
                if (warpsFrame && _capture.ColorTexture == 0) continue;

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

                var renderState = es.Def.RenderState ?? VfxEmitterRenderState.Default;
                _gl.Uniform2(_uTexDiv, es.Def.TexDiv.X <= 0 ? 1f : es.Def.TexDiv.X, es.Def.TexDiv.Y <= 0 ? 1f : es.Def.TexDiv.Y);
                _gl.Uniform2(_uTexSize, Math.Max(1f, es.TextureWidth), Math.Max(1f, es.TextureHeight));
                Vector2 emitterUvOffset = VfxUvSemantics.Periodic(
                    es.Def.EmitterUvScrollRate * es.RenderTime,
                    renderState.TextureAddressMode);
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
                Vector2 emitterUvOffsetMult = VfxUvSemantics.Periodic(
                    es.Def.TextureMultEmitterUvScrollRate * es.RenderTime,
                    es.Def.TextureMultAddressMode);
                _gl.Uniform2(_uUvScrollRateMult, emitterUvOffsetMult.X, emitterUvOffsetMult.Y);
                Vector2 uvCenterMult = EffectiveCenter(es.Def.TextureMultTransformCenter);
                _gl.Uniform2(_uUvTransformCenterMult, uvCenterMult.X, uvCenterMult.Y);
                _gl.Uniform1(_uFlipUMult, es.Def.TextureMultFlipU ? 1 : 0);
                _gl.Uniform1(_uFlipVMult, es.Def.TextureMultFlipV ? 1 : 0);
                _gl.Uniform1(_uClampUvMult, es.Def.TextureMultClampUvScroll ? 1 : 0);
                bool directional = es.Def.IsDirectionOriented || es.Def.PrimitiveKind == VfxPrimitiveKind.Ray;
                bool arbitrary = es.Def.IsArbitraryQuad ||
                    es.Def.PrimitiveKind == VfxPrimitiveKind.ArbitraryTrail;
                _gl.Uniform1(_uDirectionOriented, directional ? 1 : 0);
                _gl.Uniform1(_uArbitraryQuad, arbitrary ? 1 : 0);
                _gl.Uniform1(_uLegacyOrientation, es.Def.LegacyOrientation);
                _gl.Uniform1(_uPivotUp, es.Def.LegacyScaleUpFromOrigin ? 1 : 0);
                bool groundLayer = ShouldProjectToGround(es.Def);
                _gl.Uniform1(_uIsGroundLayer, groundLayer ? 1 : 0);
                _gl.Uniform1(_uPrimitiveKind, (int)es.Def.PrimitiveKind);
                bool ribbonPrimitive = es.Def.PrimitiveKind is VfxPrimitiveKind.CameraTrail or
                    VfxPrimitiveKind.ArbitraryTrail or VfxPrimitiveKind.Beam or VfxPrimitiveKind.CameraSegmentBeam;
                _gl.Uniform1(_uDepthPushPull, ribbonPrimitive ? 0f : es.Def.DepthPushPull);
                ApplyEmitterDepthState(es.Def, isDistortion);
                ApplyBlendMode(es.Def.BlendMode, isDistortion);
                _gl.Uniform1(_uAlphaCutoff, renderState.AlphaCutoff);
                _gl.Uniform1(
                    _uAlphaTest,
                    VfxBlendModes.ShouldAlphaTest(es.Def.BlendMode, renderState.AlphaReference) ? 1 : 0);
                _gl.Uniform1(_uEmissiveStrength, VfxBlendModes.ResolveEmissiveStrength(es.Def.BlendMode));
                bool hasMultLayer = !string.IsNullOrWhiteSpace(es.Def.TextureMultPath);
                bool useColorRamp = ShouldUseColorRamp(es.Def, es.ColorGradientTexture != 0);
                _gl.Uniform1(_uHasColor, useColorRamp ? 1 : 0);
                _gl.Uniform1(_uRampAtMult, useColorRamp && hasMultLayer ? 1 : 0);
                _gl.Uniform1(_uUvMode, es.Def.UvMode);
                _gl.Uniform1(
                    _uColorRenderFlags,
                    VfxBlendModes.ResolveColorRenderFlags(
                        es.Def.ColorRenderFlags,
                        !string.IsNullOrWhiteSpace(es.Def.ParticleColorTexturePath)));
                VfxPaletteDefinition palette = es.Def.PaletteDefinition;
                bool hasPalette = es.PaletteTexture != 0 && palette is { PaletteCount: > 0 };
                _gl.Uniform1(_uHasPalette, hasPalette ? 1 : 0);
                _gl.Uniform1(_uPaletteCount, Math.Max(1, palette?.PaletteCount ?? 1));
                _gl.Uniform1(_uPaletteAddressMode, palette?.AddressMode ?? 0);
                Vector4 paletteMask = palette?.PaletteSourceMixColor ?? Vector4.Zero;
                _gl.Uniform4(_uPaletteMixMask, paletteMask.X, paletteMask.Y, paletteMask.Z, paletteMask.W);
                Vector2 paletteScroll = new(
                    palette?.ScrollU?.Sample(sharedPalettePhase) ?? 0f,
                    palette?.ScrollV?.Sample(sharedPalettePhase) ?? 0f);
                _gl.Uniform2(_uPaletteScroll, paletteScroll.X, paletteScroll.Y);
                _gl.Uniform1(_uColorLookUpTypeX, es.Def.ColorLookUpTypeX ?? 0);
                _gl.Uniform1(_uColorLookUpTypeY, es.Def.ColorLookUpTypeY ?? 0);
                Vector2 colorLookUpScales = es.Def.ColorLookUpScales;
                _gl.Uniform2(_uColorLookUpScales, colorLookUpScales.X, colorLookUpScales.Y);
                _gl.Uniform2(_uColorLookUpOffsets, es.Def.ColorLookUpOffsets.X, es.Def.ColorLookUpOffsets.Y);
                _gl.Uniform1(_uFlipU, renderState.FlipU ? 1 : 0);
                _gl.Uniform1(_uFlipV, renderState.FlipV ? 1 : 0);
                _gl.Uniform1(_uClampUv, renderState.ClampUvScroll ? 1 : 0);
                _gl.Uniform1(_uAddressMode, renderState.TextureAddressMode);
                _gl.Uniform1(_uAddressModeMult, es.Def.TextureMultAddressMode);
                _gl.Uniform1(_uIsDistortion, isDistortion ? 1 : 0);
                _gl.Uniform1(_uDistortionStrength, es.Def.Distortion?.Strength ?? 0f);
                VfxAlphaErosionDefinition erosionDefinition = es.Def.AlphaErosion;
                bool erosionEnabled = erosionDefinition is not null && es.Def.UvMode != 2;
                bool hasErosionMap = erosionEnabled && es.ErosionTexture != 0;
                Vector4 erosionDefault = erosionDefinition is not null && string.IsNullOrWhiteSpace(erosionDefinition.TexturePath)
                    ? Vector4.One
                    : Vector4.Zero;
                _gl.Uniform1(_uHasErosion, erosionEnabled ? 1 : 0);
                _gl.Uniform1(_uHasErosionMap, hasErosionMap ? 1 : 0);
                _gl.Uniform1(_uErosionAddressMode, erosionDefinition?.AddressMode ?? 0);
                _gl.Uniform4(_uErosionDefault, erosionDefault.X, erosionDefault.Y, erosionDefault.Z, erosionDefault.W);
                _gl.Uniform1(_uErosionFeatherIn, erosionDefinition?.FeatherIn ?? 0f);
                _gl.Uniform1(_uErosionFeatherOut, erosionDefinition?.FeatherOut ?? 0f);
                _gl.Uniform1(_uErosionSliceWidth, erosionDefinition?.SliceWidth ?? 1.5f);
                bool useSoftParticles = ShouldUseSoftParticles(es.Def, _capture.DepthTexture != 0);
                _gl.Uniform1(_uHasSoftParticle, useSoftParticles ? 1 : 0);
                Vector4 softParams = ResolveSoftParticleParams(es.Def.SoftParticle);
                Vector4 softControl = ResolveSoftParticleControl(es.Def.BlendMode);
                _gl.Uniform4(_uSoftParticleParams, softParams.X, softParams.Y, softParams.Z, softParams.W);
                _gl.Uniform4(_uSoftParticleControl, softControl.X, softControl.Y, softControl.Z, softControl.W);
                _gl.Uniform3(_uPlacementRight, es.PlacementRight.X, es.PlacementRight.Y, es.PlacementRight.Z);
                _gl.Uniform3(_uPlacementUp, es.PlacementUp.X, es.PlacementUp.Y, es.PlacementUp.Z);
                _gl.Uniform3(_uPlacementForward, es.PlacementForward.X, es.PlacementForward.Y, es.PlacementForward.Z);
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.Texture2D, es.Texture != 0 ? es.Texture : _textures.FallbackTransparentTexture);
                _gl.Uniform1(_uHasTex, ShouldSampleBaseTexture(es.Def, es.Texture) ? 1 : 0);
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
                    // LTK keeps a distorting draw alive when its normal map is missing. Its
                    // fallback has alpha zero, so this transparent texture produces the same
                    // zero warp while preserving alpha-test/stencil side effects.
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
                if (es.Def.PrimitiveKind is VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail)
                {
                    int vertices = _trailGeometry.Build(es, Vector3.Normalize(Vector3.Cross(camRight, camUp)));
                    if (vertices > 0)
                    {
                        _gl.BindVertexArray(_trailVao);
                        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _trailVbo);
                        _gl.BufferData(BufferTargetARB.ArrayBuffer,
                            new ReadOnlySpan<float>(_trailGeometry.Vertices, 0, vertices * VfxTrailGeometry.VertexStride), BufferUsageARB.DynamicDraw);
                        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)vertices);
                        _gl.BindVertexArray(_vao);
                    }
                }
                else if (es.Def.PrimitiveKind is VfxPrimitiveKind.Beam or VfxPrimitiveKind.CameraSegmentBeam)
                {
                    // LTK suppresses a beam ribbon when the same primitive resolves mMesh.
                    if (!string.IsNullOrWhiteSpace(es.Def.MeshPath))
                        continue;
                    int vertices = _beamGeometry.Build(es, camPos, renderInstanceCount);
                    if (vertices > 0)
                    {
                        _gl.BindVertexArray(_trailVao);
                        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _trailVbo);
                        _gl.BufferData(BufferTargetARB.ArrayBuffer,
                            new ReadOnlySpan<float>(_beamGeometry.Vertices, 0, vertices * VfxBeamGeometry.VertexStride), BufferUsageARB.DynamicDraw);
                        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)vertices);
                        _gl.BindVertexArray(_vao);
                    }
                }
                else
                    _gl.DrawArraysInstanced(PrimitiveType.TriangleFan, 0, 4, (uint)renderInstanceCount);
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
                _gl.StencilFuncSeparate(
                    TriangleFace.Front,
                    (StencilFunction)stencilFrontFunction,
                    stencilFrontReference,
                    (uint)stencilFrontValueMask);
                _gl.StencilMaskSeparate(TriangleFace.Front, (uint)stencilFrontWriteMask);
                _gl.StencilOpSeparate(
                    TriangleFace.Front,
                    (StencilOp)stencilFrontFail,
                    (StencilOp)stencilFrontDepthFail,
                    (StencilOp)stencilFrontDepthPass);
                _gl.StencilFuncSeparate(
                    TriangleFace.Back,
                    (StencilFunction)stencilBackFunction,
                    stencilBackReference,
                    (uint)stencilBackValueMask);
                _gl.StencilMaskSeparate(TriangleFace.Back, (uint)stencilBackWriteMask);
                _gl.StencilOpSeparate(
                    TriangleFace.Back,
                    (StencilOp)stencilBackFail,
                    (StencilOp)stencilBackDepthFail,
                    (StencilOp)stencilBackDepthPass);
                if (depthTest) _gl.Enable(EnableCap.DepthTest); else _gl.Disable(EnableCap.DepthTest);
                if (cullFace) _gl.Enable(EnableCap.CullFace); else _gl.Disable(EnableCap.CullFace);
                if (polygonOffset) _gl.Enable(EnableCap.PolygonOffsetFill); else _gl.Disable(EnableCap.PolygonOffsetFill);
                if (blend) _gl.Enable(EnableCap.Blend); else _gl.Disable(EnableCap.Blend);
                if (stencilTest) _gl.Enable(EnableCap.StencilTest); else _gl.Disable(EnableCap.StencilTest);
                for (int unit = 0; unit < textureBindings.Length; unit++)
                {
                    _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
                    _gl.BindTexture(TextureTarget.Texture2D, (uint)textureBindings[unit]);
                    _gl.BindTexture(TextureTarget.TextureCubeMap, (uint)cubeBindings[unit]);
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
            var renderState = definition.RenderState ?? VfxEmitterRenderState.Default;
            bool writeDepth = !isDistortion && VfxBlendModes.ShouldWriteDepth(
                definition.BlendMode,
                renderState.AlphaReference);
            _gl.DepthMask(writeDepth);
            if (VfxBlendModes.ShouldTestDepth(definition.MiscRenderFlags)) _gl.Enable(EnableCap.DepthTest);
            else _gl.Disable(EnableCap.DepthTest);

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
            // inspector metadata in 1.19.4 and does not change the particle material sampler.
            _gl.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            _gl.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
        }

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

        private void ApplyColorWriteMask(VfxEmitterRenderState renderState)
        {
            bool writeColor = !renderState.WriteAlphaOnly;
            _gl.ColorMask(writeColor, writeColor, writeColor, true);
        }

        private void ApplyEmitterStencilState(
            VfxEmitterRenderState renderState,
            IReadOnlyDictionary<uint, byte> referenceIds)
        {
            if (!VfxStencilSemantics.TryGetDescriptor(renderState.StencilMode, out VfxStencilDescriptor descriptor) ||
                descriptor.Operation == VfxStencilOperationKind.Disabled)
            {
                _gl.Disable(EnableCap.StencilTest);
                _gl.StencilMask(0);
                return;
            }

            int reference = VfxStencilSemantics.ResolveReference(renderState, referenceIds);
            _gl.Enable(EnableCap.StencilTest);
            _gl.StencilMask(descriptor.WritesStencil ? 0xFFu : 0u);
            _gl.StencilOp(StencilOp.Keep, StencilOp.Keep,
                descriptor.WritesStencil ? StencilOp.Replace : StencilOp.Keep);
            _gl.StencilFunc(descriptor.Operation switch
            {
                VfxStencilOperationKind.WriteReference => StencilFunction.Always,
                VfxStencilOperationKind.TestEqual => StencilFunction.Equal,
                VfxStencilOperationKind.TestNotEqual => StencilFunction.Notequal,
                _ => StencilFunction.Always
            }, reference, 0xFFu);

            if (!descriptor.WritesColor)
                _gl.ColorMask(false, false, false, false);
        }

        private IReadOnlyDictionary<uint, byte> BuildStencilReferenceMap(
            IReadOnlyList<VfxRenderQueueEntry> renderQueue)
        {
            _stencilReferences.Clear();
            Array.Clear(_reservedStencilReferences, 0, _reservedStencilReferences.Length);
            _reservedStencilReferences[0] = true;
            foreach (VfxRenderQueueEntry entry in renderQueue)
            {
                byte authoredReference = (entry.Emitter.Def.RenderState ?? VfxEmitterRenderState.Default).StencilReference;
                _reservedStencilReferences[authoredReference] = true;
            }

            byte candidate = 1;
            foreach (VfxRenderQueueEntry entry in renderQueue)
            {
                uint id = (entry.Emitter.Def.RenderState ?? VfxEmitterRenderState.Default).StencilReferenceId;
                if (id == 0 || _stencilReferences.ContainsKey(id)) continue;
                while (candidate != 0 && _reservedStencilReferences[candidate]) candidate++;
                if (candidate == 0)
                    throw new InvalidOperationException("The VFX render queue exhausts the 8-bit stencil reference space.");
                _stencilReferences.Add(id, candidate);
                _reservedStencilReferences[candidate] = true;
                candidate++;
            }
            return _stencilReferences;
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

        private static Vector2 EffectiveCenter(Vector2 center) => center;

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
            _gl.DeleteBuffer(_trailVbo);
            _gl.DeleteVertexArray(_trailVao);
            _gl.DeleteBuffer(_quadVbo);
            _gl.DeleteBuffer(_instVbo);
            _gl.DeleteVertexArray(_vao);
            _gl.DeleteProgram(_program);
            if (_meshBoneBuffer != 0) _gl.DeleteBuffer(_meshBoneBuffer);
            _meshBoneBuffer = 0;
            _ownerSkinningCount = 0;
            if (_meshProgram != 0) _gl.DeleteProgram(_meshProgram);
            _meshProgram = 0;
            _meshResources.Dispose();
            _capture.Dispose();
            _ready = false;
        }

        private const uint OwnerBoneBinding = 1;
        private uint _meshProgram;
        private uint _meshBoneBuffer;
        private int _ownerSkinningCount;
        private int _muViewProj, _muWorldPos, _muScale, _muRotation, _muCamPos, _muCamUp, _muAlignPitchToCamera, _muAlignYawToCamera, _muMeshSkinned, _muUseOwnerSkinning, _muIsGroundLayer, _muColor, _muTex, _muHasTex, _muEmitterUvOffset;
        private int _muIsDistortion, _muDistortionTex, _muSceneTex, _muDistortionStrength;
        private int _muTexDiv, _muTexSize, _muFrame, _muAddressMode, _muClampUv, _muUvTransformCenter;
        private int _muTexMult, _muHasTexMult, _muTexDivMult, _muTexSizeMult, _muUvOffsetMult, _muUvScaleMult, _muUvRotationMult;
        private int _muEmitterUvOffsetMult, _muFlipUMult, _muFlipVMult;
        private int _muAddressModeMult, _muClampUvMult, _muUvTransformCenterMult;
        private int _muPlacementRight, _muPlacementUp, _muPlacementForward;
        private int _muAlphaCutoff, _muAlphaTest, _muEmissiveStrength, _muColorMap, _muHasColor, _muRampAtMult, _muUvMode, _muColorRenderFlags, _muColorLookUpTypeX, _muColorLookUpTypeY, _muColorLookUpScales, _muColorLookUpOffsets, _muFlipU, _muFlipV;
        private int _muPaletteMap, _muHasPalette, _muPaletteCount, _muPaletteAddressMode, _muPaletteSelector, _muPaletteMixMask, _muPaletteScroll;
        private int _muBirthUvOffset, _muUvScale, _muUvRotation;
        private int _muErosionTex, _muHasErosion, _muHasErosionMap, _muErosionAddressMode, _muErosionDefault;
        private int _muErosionDrive, _muErosionFeatherIn, _muErosionFeatherOut, _muErosionSliceWidth, _muErosionMixer;
        private int _muReflectionTex, _muHasReflection, _muFresnel, _muReflection, _muReflectionColor, _muAttachedMesh;
        private int _muSceneDepthTex, _muHasSoftParticle, _muSoftParticleParams, _muSoftParticleControl, _muDepthProjection, _muViewportSize;

        private void EnsureMeshProgram()
        {
            if (_meshProgram == 0)
            {
                _meshProgram = GlShaderCompiler.CreateProgram(_gl, _gles, VfxShaderSource.MeshVertex, VfxShaderSource.MeshFragment);
                _muViewProj = _gl.GetUniformLocation(_meshProgram, "uViewProj");
                _muWorldPos = _gl.GetUniformLocation(_meshProgram, "uWorldPos");
                _muScale = _gl.GetUniformLocation(_meshProgram, "uScale");
                _muRotation = _gl.GetUniformLocation(_meshProgram, "uRotation");
                _muCamPos = _gl.GetUniformLocation(_meshProgram, "uCamPos");
                _muCamUp = _gl.GetUniformLocation(_meshProgram, "uCamUp");
                _muAlignPitchToCamera = _gl.GetUniformLocation(_meshProgram, "uAlignPitchToCamera");
                _muAlignYawToCamera = _gl.GetUniformLocation(_meshProgram, "uAlignYawToCamera");
                _muMeshSkinned = _gl.GetUniformLocation(_meshProgram, "uMeshSkinned");
                _muUseOwnerSkinning = _gl.GetUniformLocation(_meshProgram, "uUseOwnerSkinning");
                _muIsGroundLayer = _gl.GetUniformLocation(_meshProgram, "uIsGroundLayer");
                _muColor = _gl.GetUniformLocation(_meshProgram, "uColor");
                _muTex = _gl.GetUniformLocation(_meshProgram, "uTex");
                _muHasTex = _gl.GetUniformLocation(_meshProgram, "uHasTex");
                _muEmitterUvOffset = _gl.GetUniformLocation(_meshProgram, "uEmitterUvOffset");
                _muIsDistortion = _gl.GetUniformLocation(_meshProgram, "uIsDistortion");
                _muDistortionTex = _gl.GetUniformLocation(_meshProgram, "uDistortionTex");
                _muSceneTex = _gl.GetUniformLocation(_meshProgram, "uSceneTex");
                _muDistortionStrength = _gl.GetUniformLocation(_meshProgram, "uDistortionStrength");
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
                _muEmissiveStrength = _gl.GetUniformLocation(_meshProgram, "uEmissiveStrength");
                _muColorMap = _gl.GetUniformLocation(_meshProgram, "uColorMap");
                _muHasColor = _gl.GetUniformLocation(_meshProgram, "uHasColor");
                _muRampAtMult = _gl.GetUniformLocation(_meshProgram, "uRampAtMult");
                _muUvMode = _gl.GetUniformLocation(_meshProgram, "uUvMode");
                _muColorRenderFlags = _gl.GetUniformLocation(_meshProgram, "uColorRenderFlags");
                _muPaletteMap = _gl.GetUniformLocation(_meshProgram, "uPaletteMap");
                _muHasPalette = _gl.GetUniformLocation(_meshProgram, "uHasPalette");
                _muPaletteCount = _gl.GetUniformLocation(_meshProgram, "uPaletteCount");
                _muPaletteAddressMode = _gl.GetUniformLocation(_meshProgram, "uPaletteAddressMode");
                _muPaletteSelector = _gl.GetUniformLocation(_meshProgram, "uPaletteSelector");
                _muPaletteMixMask = _gl.GetUniformLocation(_meshProgram, "uPaletteMixMask");
                _muPaletteScroll = _gl.GetUniformLocation(_meshProgram, "uPaletteScroll");
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
                _muHasErosionMap = _gl.GetUniformLocation(_meshProgram, "uHasErosionMap");
                _muErosionAddressMode = _gl.GetUniformLocation(_meshProgram, "uErosionAddressMode");
                _muErosionDefault = _gl.GetUniformLocation(_meshProgram, "uErosionDefault");
                _muErosionDrive = _gl.GetUniformLocation(_meshProgram, "uErosionDrive");
                _muErosionFeatherIn = _gl.GetUniformLocation(_meshProgram, "uErosionFeatherIn");
                _muErosionFeatherOut = _gl.GetUniformLocation(_meshProgram, "uErosionFeatherOut");
                _muErosionSliceWidth = _gl.GetUniformLocation(_meshProgram, "uErosionSliceWidth");
                _muErosionMixer = _gl.GetUniformLocation(_meshProgram, "uErosionMixer");
                _muReflectionTex = _gl.GetUniformLocation(_meshProgram, "uReflectionTex");
                _muHasReflection = _gl.GetUniformLocation(_meshProgram, "uHasReflection");
                _muFresnel = _gl.GetUniformLocation(_meshProgram, "uFresnel");
                _muReflection = _gl.GetUniformLocation(_meshProgram, "uReflection");
                _muReflectionColor = _gl.GetUniformLocation(_meshProgram, "uReflectionColor");
                _muAttachedMesh = _gl.GetUniformLocation(_meshProgram, "uAttachedMesh");
                _muSceneDepthTex = _gl.GetUniformLocation(_meshProgram, "uSceneDepthTex");
                _muHasSoftParticle = _gl.GetUniformLocation(_meshProgram, "uHasSoftParticle");
                _muSoftParticleParams = _gl.GetUniformLocation(_meshProgram, "uSoftParticleParams");
                _muSoftParticleControl = _gl.GetUniformLocation(_meshProgram, "uSoftParticleControl");
                _muDepthProjection = _gl.GetUniformLocation(_meshProgram, "uDepthProjection");
                _muViewportSize = _gl.GetUniformLocation(_meshProgram, "uViewportSize");

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
            _ownerSkinningCount = Math.Min(matrices?.Length ?? 0, GpuSkinningData.MaxBones);
            if (!_ready || _ownerSkinningCount == 0)
                return;

            EnsureMeshProgram();
            _gl.BindBuffer(BufferTargetARB.UniformBuffer, _meshBoneBuffer);
            _gl.BufferSubData(
                BufferTargetARB.UniformBuffer,
                0,
                new ReadOnlySpan<Matrix4x4>(matrices, 0, _ownerSkinningCount));
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

        private void UpdateEmitterMeshPositions(VfxPlaybackRuntime.EmitterState es, float[] positions)
        {
            if (_ready)
                _meshResources.UpdatePositions(es, positions);
        }

        private void RenderMeshEmitter(
            VfxPlaybackRuntime.EmitterState es,
            Matrix4x4 viewProj,
            Vector3 camPos,
            Vector3 camUp,
            ReadOnlySpan<float> instances,
            int instanceCount,
            float sharedPalettePhase)
        {
            if (es.MeshVao == 0 || es.MeshVertexCount == 0) return;
            bool isDistortion = es.Def.Distortion != null;
            bool warpsFrame = isDistortion && es.Def.Distortion.Strength != 0f;
            if (warpsFrame && _capture.ColorTexture == 0) return;
            if (es.MeshAnimation != null)
                UpdateEmitterMeshPositions(es, es.MeshAnimation.Evaluate(es.EmitterAge));
            bool cullFace = _gl.IsEnabled(EnableCap.CullFace);
            EnsureMeshProgram();
            _gl.UseProgram(_meshProgram);
            _gl.BindVertexArray(es.MeshVao);
            _gl.UniformMatrix4(_muViewProj, 1, false, in viewProj.M11);
            _gl.Uniform3(_muCamPos, camPos.X, camPos.Y, camPos.Z);
            _gl.Uniform3(_muCamUp, camUp.X, camUp.Y, camUp.Z);
            bool attachedMesh = es.Def.PrimitiveKind == VfxPrimitiveKind.AttachedMesh;
            bool useOwnerSkinning = attachedMesh && es.MeshHasSkinning && _ownerSkinningCount > 0;
            _gl.Uniform1(_muUseOwnerSkinning, useOwnerSkinning ? 1 : 0);
            if (useOwnerSkinning)
                _gl.BindBufferBase(BufferTargetARB.UniformBuffer, OwnerBoneBinding, _meshBoneBuffer);

            // Direction-oriented mesh particles take precedence over camera alignment in LTK.
            bool cameraAlignedMesh = !attachedMesh && !es.Def.IsDirectionOriented &&
                (es.Def.MeshAlignPitchToCamera || es.Def.MeshAlignYawToCamera);
            _gl.Uniform1(_muAlignPitchToCamera, cameraAlignedMesh && es.Def.MeshAlignPitchToCamera ? 1 : 0);
            _gl.Uniform1(_muAlignYawToCamera, cameraAlignedMesh && es.Def.MeshAlignYawToCamera ? 1 : 0);
            _gl.Uniform1(_muMeshSkinned, es.Def.MeshIsSkinned ? 1 : 0);
            _gl.Uniform1(_muIsGroundLayer, es.Def.IsGroundLayer ? 1 : 0);
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
            Vector2 emitterUvOffsetMult = VfxUvSemantics.Periodic(
                    es.Def.TextureMultEmitterUvScrollRate * es.RenderTime,
                    es.Def.TextureMultAddressMode);
            _gl.Uniform2(_muEmitterUvOffsetMult, emitterUvOffsetMult.X, emitterUvOffsetMult.Y);
            _gl.Uniform1(_muFlipUMult, es.Def.TextureMultFlipU ? 1 : 0);
            _gl.Uniform1(_muFlipVMult, es.Def.TextureMultFlipV ? 1 : 0);
            _gl.Uniform1(_muAddressModeMult, es.Def.TextureMultAddressMode);
            _gl.Uniform1(_muClampUvMult, es.Def.TextureMultClampUvScroll ? 1 : 0);
            VfxAlphaErosionDefinition meshErosion = es.Def.AlphaErosion;
            bool meshErosionEnabled = meshErosion is not null;
            bool meshHasErosionMap = meshErosionEnabled && es.ErosionTexture != 0;
            Vector4 meshErosionDefault = meshErosion is not null && string.IsNullOrWhiteSpace(meshErosion.TexturePath)
                ? Vector4.One
                : Vector4.Zero;
            _gl.Uniform1(_muHasErosion, meshErosionEnabled ? 1 : 0);
            _gl.Uniform1(_muHasErosionMap, meshHasErosionMap ? 1 : 0);
            _gl.Uniform1(_muErosionAddressMode, meshErosion?.AddressMode ?? 0);
            _gl.Uniform4(_muErosionDefault, meshErosionDefault.X, meshErosionDefault.Y, meshErosionDefault.Z, meshErosionDefault.W);
            _gl.Uniform1(_muErosionFeatherIn, meshErosion?.FeatherIn ?? 0f);
            _gl.Uniform1(_muErosionFeatherOut, meshErosion?.FeatherOut ?? 0f);
            _gl.Uniform1(_muErosionSliceWidth, meshErosion?.SliceWidth ?? 1.5f);
            _gl.Uniform3(_muPlacementRight, es.PlacementRight.X, es.PlacementRight.Y, es.PlacementRight.Z);
            _gl.Uniform3(_muPlacementUp, es.PlacementUp.X, es.PlacementUp.Y, es.PlacementUp.Z);
            _gl.Uniform3(_muPlacementForward, es.PlacementForward.X, es.PlacementForward.Y, es.PlacementForward.Z);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, es.Texture != 0 ? es.Texture : _textures.FallbackTransparentTexture);
            _gl.Uniform1(_muHasTex, ShouldSampleBaseTexture(es.Def, es.Texture) ? 1 : 0);
            var renderState = es.Def.RenderState ?? VfxEmitterRenderState.Default;
            ApplyAddressMode(2);
            _gl.Uniform1(_muAlphaCutoff, renderState.AlphaCutoff);
            _gl.Uniform1(
                _muAlphaTest,
                VfxBlendModes.ShouldAlphaTest(es.Def.BlendMode, renderState.AlphaReference) ? 1 : 0);
            _gl.Uniform1(_muEmissiveStrength, VfxBlendModes.ResolveEmissiveStrength(es.Def.BlendMode));
            _gl.Uniform1(_muIsDistortion, isDistortion ? 1 : 0);
            _gl.Uniform1(_muDistortionStrength, es.Def.Distortion?.Strength ?? 0f);
            _gl.Uniform1(_muDistortionTex, 2);
            _gl.Uniform1(_muSceneTex, 3);
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
            _gl.Uniform1(_muHasColor, 0);
            _gl.Uniform1(_muRampAtMult, 0);
            _gl.Uniform1(_muUvMode, es.Def.UvMode);
            _gl.Uniform1(
                _muColorRenderFlags,
                VfxBlendModes.ResolveColorRenderFlags(
                    es.Def.ColorRenderFlags,
                    !string.IsNullOrWhiteSpace(es.Def.ParticleColorTexturePath)));
            VfxPaletteDefinition meshPalette = es.Def.PaletteDefinition;
            bool meshHasPalette = es.PaletteTexture != 0 && meshPalette is { PaletteCount: > 0 };
            _gl.Uniform1(_muHasPalette, meshHasPalette ? 1 : 0);
            _gl.Uniform1(_muPaletteCount, Math.Max(1, meshPalette?.PaletteCount ?? 1));
            _gl.Uniform1(_muPaletteAddressMode, meshPalette?.AddressMode ?? 0);
            Vector4 meshPaletteMask = meshPalette?.PaletteSourceMixColor ?? Vector4.Zero;
            _gl.Uniform4(_muPaletteMixMask, meshPaletteMask.X, meshPaletteMask.Y, meshPaletteMask.Z, meshPaletteMask.W);
            Vector2 meshPaletteScroll = new(
                meshPalette?.ScrollU?.Sample(sharedPalettePhase) ?? 0f,
                meshPalette?.ScrollV?.Sample(sharedPalettePhase) ?? 0f);
            _gl.Uniform2(_muPaletteScroll, meshPaletteScroll.X, meshPaletteScroll.Y);
            _gl.Uniform1(_muColorLookUpTypeX, es.Def.ColorLookUpTypeX ?? 0);
            _gl.Uniform1(_muColorLookUpTypeY, es.Def.ColorLookUpTypeY ?? 0);
            Vector2 meshColorLookUpScales = es.Def.ColorLookUpScales;
            _gl.Uniform2(_muColorLookUpScales, meshColorLookUpScales.X, meshColorLookUpScales.Y);
            _gl.Uniform2(_muColorLookUpOffsets, es.Def.ColorLookUpOffsets.X, es.Def.ColorLookUpOffsets.Y);
            _gl.Uniform1(_muFlipU, renderState.FlipU ? 1 : 0);
            _gl.Uniform1(_muFlipV, renderState.FlipV ? 1 : 0);
            _gl.Uniform1(_muAddressMode, renderState.TextureAddressMode);
            _gl.Uniform1(_muClampUv, renderState.ClampUvScroll ? 1 : 0);
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
            _gl.Uniform1(_muHasReflection, hasReflectionCube ? 1 : 0);
            _gl.Uniform1(_muAttachedMesh, attachedMesh ? 1 : 0);

            Vector4 fresnelColor = reflection?.FresnelColor ?? Vector4.Zero;
            _gl.Uniform4(
                _muFresnel,
                fresnelColor.X,
                fresnelColor.Y,
                fresnelColor.Z,
                reflection?.Fresnel ?? 1f);
            _gl.Uniform4(
                _muReflection,
                reflection?.ReflectionFresnel ?? 1f,
                reflection?.DirectOpacity ?? 0f,
                reflection?.GlancingOpacity ?? 1f,
                0f);
            Vector4 reflectionColor = reflection?.ReflectionFresnelColor ?? Vector4.One;
            _gl.Uniform4(
                _muReflectionColor,
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
            _gl.Uniform1(_muHasSoftParticle, meshUsesSoftParticles ? 1 : 0);
            Vector4 meshSoftParams = ResolveSoftParticleParams(es.Def.SoftParticle);
            Vector4 meshSoftControl = ResolveSoftParticleControl(es.Def.BlendMode);
            _gl.Uniform4(_muSoftParticleParams, meshSoftParams.X, meshSoftParams.Y, meshSoftParams.Z, meshSoftParams.W);
            _gl.Uniform4(_muSoftParticleControl, meshSoftControl.X, meshSoftControl.Y, meshSoftControl.Z, meshSoftControl.W);
            _gl.Uniform2(_muDepthProjection, _depthProjectionValue.X, _depthProjectionValue.Y);
            _gl.Uniform2(_muViewportSize, (float)_capture.Width, (float)_capture.Height);
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
            // LTK/Riot cull mesh backfaces by default. disableBackfaceCull explicitly asks
            // for a double-sided draw; do not make every particle mesh double-sided.
            if (es.Def.RenderState?.DisableBackfaceCull == true)
                _gl.Disable(EnableCap.CullFace);
            else
                _gl.Enable(EnableCap.CullFace);
            ApplyBlendMode(es.Def.BlendMode, isDistortion);

            Vector2 emitterUvOffset = VfxUvSemantics.Periodic(
                    es.Def.EmitterUvScrollRate * es.RenderTime,
                    renderState.TextureAddressMode);
            _gl.Uniform2(_muEmitterUvOffset, emitterUvOffset.X, emitterUvOffset.Y);
            for (int i = 0; i < instanceCount; i++)
            {
                int o = i * Stride;
                _gl.Uniform3(_muWorldPos, instances[o], instances[o + 1], instances[o + 2]);
                float ownerScale = attachedMesh && float.IsFinite(es.MeshOwnerScale) && es.MeshOwnerScale > 0f
                    ? es.MeshOwnerScale
                    : 1f;
                float scaleX = ClampScale(instances[o + 3]) * ownerScale;
                float scaleY = ClampScale(instances[o + 4]) * ownerScale;
                float scaleZ = ClampScale(instances[o + 18]) * ownerScale;
                _gl.Uniform3(_muScale, scaleX, scaleY, scaleZ);
                Vector3 meshRotation = new(
                    instances[o + 15],
                    instances[o + 16],
                    instances[o + 17]);
                _gl.Uniform3(
                    _muRotation,
                    meshRotation.X,
                    meshRotation.Y,
                    meshRotation.Z);
                _gl.Uniform4(_muColor, instances[o + 5], instances[o + 6], instances[o + 7], instances[o + 8]);
                _gl.Uniform2(_muBirthUvOffset, instances[o + 19], instances[o + 20]);
                _gl.Uniform2(_muUvScale, instances[o + 21], instances[o + 22]);
                _gl.Uniform1(_muUvRotation, instances[o + 23]);
                _gl.Uniform1(_muErosionDrive, instances[o + 24]);
                _gl.Uniform4(_muErosionMixer, instances[o + 25], instances[o + 26], instances[o + 27], instances[o + 28]);
                _gl.Uniform2(_muUvOffsetMult, instances[o + 29], instances[o + 30]);
                _gl.Uniform2(_muUvScaleMult, instances[o + 31], instances[o + 32]);
                _gl.Uniform1(_muUvRotationMult, instances[o + 33]);
                _gl.Uniform1(_muFrame, instances[o + 10]);
                _gl.Uniform1(_muPaletteSelector, instances[o + 35]);

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

                                _drawElements(
                                    (uint)PrimitiveType.Triangles,
                                    range.IndexCount,
                                    (uint)DrawElementsType.UnsignedInt,
                                    new IntPtr(range.StartIndex * sizeof(uint)));
                            }
                        }
                        else
                        {
                            _drawElements((uint)PrimitiveType.Triangles, es.MeshIndexCount, (uint)DrawElementsType.UnsignedInt, IntPtr.Zero);
                        }
                    }
                }
                else _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)es.MeshVertexCount);
            }
            if (cullFace) _gl.Enable(EnableCap.CullFace);
            else _gl.Disable(EnableCap.CullFace);
            _gl.UseProgram(_program);
            _gl.BindVertexArray(_vao);
        }

        internal static int ResolveEmitterDrawCount(
            VfxEmitterDefinition definition,
            int alreadyUsed,
            int instanceCount)
        {
            if (definition is null || instanceCount <= 0) return 0;

            int limit = definition.PrimitiveKind == VfxPrimitiveKind.AttachedMesh
                ? AttachedMeshesPerEmitter
                : definition.IsMeshPrimitive
                    ? MeshesPerEmitter
                    : definition.DrawsAsBeam
                        ? BeamsPerEmitter
                        : definition.DrawsAsQuad
                            ? QuadsPerEmitter
                            : int.MaxValue;

            if (limit == int.MaxValue) return instanceCount;
            int used = Math.Max(0, alreadyUsed);
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
               VfxBlendModes.ShouldSortBackToFront(definition.BlendMode);

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
            => textureHandle != 0 || string.IsNullOrWhiteSpace(definition?.TexturePath);

        internal static float ResolveEmitterPhase(VfxEmitterDefinition definition, float age)
        {
            float lifetime = definition?.EmitterLifetime ?? 0f;
            if (lifetime <= 0f || !float.IsFinite(lifetime) || !float.IsFinite(age)) return 0f;
            return Math.Clamp(age / lifetime, 0f, 1f);
        }

        internal static Vector2? ResolvePolygonOffset(VfxEmitterDefinition definition)
        {
            if (definition is null) return null;

            if (definition.DepthBiasFactors is { } authored &&
                (authored.X != 0f || authored.Y != 0f))
            {
                return authored;
            }

            // skinnedmesh/particle_ps gives AttachedMesh a small overlay offset whenever
            // the emitter authors no effective bias. Riot treats an explicit [0,0] the same
            // as an omitted pair here.
            return definition.PrimitiveKind == VfxPrimitiveKind.AttachedMesh
                ? new Vector2(-1f, -1f)
                : null;
        }

        internal static bool ShouldUseColorRamp(VfxEmitterDefinition definition, bool hasColorRampTexture)
        {
            if (!hasColorRampTexture || definition is null) return false;

            // LTK's fixed-alpha quad/ribbon bundle drops ALPHA_EROSION. Once that pass is gone,
            // the authored ramp is valid again unless LOCK_ALPHA is also occupying the mult pass.
            bool fixedAlphaUv = definition.UvMode == 2 &&
                definition.PrimitiveKind != VfxPrimitiveKind.Mesh &&
                definition.PrimitiveKind != VfxPrimitiveKind.AttachedMesh;
            bool erosionEnabled = definition.AlphaErosion is not null && !fixedAlphaUv;
            bool hasMultLayer = !string.IsNullOrWhiteSpace(definition.TextureMultPath);
            return !erosionEnabled && !(hasMultLayer && definition.UvMode == 2);
        }

        internal static bool ShouldProjectToGround(VfxEmitterDefinition definition)
        {
            if (definition is null) return false;

            // LTK enables GROUND_LAYER only from the authored isGroundLayer flag. Neither
            // isFollowingTerrain nor a ground-looking birth rotation selects this draw path.
            return definition.IsGroundLayer;
        }

        internal static bool ShouldUseSoftParticles(VfxEmitterDefinition definition, bool hasSceneDepth)
        {
            if (!hasSceneDepth || definition?.SoftParticle is null) return false;
            if (definition.PrimitiveKind is VfxPrimitiveKind.AttachedMesh or VfxPrimitiveKind.PlanarProjection)
                return false;

            // LTK's fixed-alpha quad/ribbon shader compiles no SOFT_PARTICLES. Meshes keep
            // their soft pass even when the emitter authors LOCK_ALPHA.
            bool fixedAlphaUv = definition.UvMode == 2 && definition.PrimitiveKind != VfxPrimitiveKind.Mesh;
            return !fixedAlphaUv;
        }

        internal static Vector4 ResolveSoftParticleParams(VfxSoftParticleDefinition soft)
        {
            if (soft is null) return Vector4.Zero;
            bool fadesIn = soft.DeltaIn != 0f;
            return new Vector4(
                fadesIn ? soft.BeginIn : -1e9f,
                soft.BeginIn + soft.DeltaIn + soft.BeginOut,
                fadesIn ? 1f / soft.DeltaIn : 1f,
                soft.DeltaOut == 0f ? 0f : 1f / soft.DeltaOut);
        }

        internal static Vector4 ResolveSoftParticleControl(int blendMode)
            => blendMode switch
            {
                1 or 4 => new Vector4(1f, 0f, 0f, 1f),
                5 => new Vector4(0f, 1f, 0f, 1f),
                _ => new Vector4(0f, 1f, 1f, 0f)
            };
    }
}
