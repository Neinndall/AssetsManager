using AssetsManager.Services.Viewer.Resources;
using System;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Interaction;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Utils.Viewport;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioControl
    {
        private readonly OpenGlSnapshotService _snapshotService = new();
        private OpenGlSnapshotService.SnapshotRequest _pendingSnapshot;

        [System.Runtime.InteropServices.DllImport("opengl32.dll", EntryPoint = "wglGetProcAddress", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern IntPtr wglGetProcAddress(string procName);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern IntPtr LoadLibrary(string lpszLib);

        private static readonly IntPtr OpenGLModule = LoadLibrary("opengl32.dll");

        private static IntPtr GetOpenGLProcAddress(string procName)
        {
            var addr = wglGetProcAddress(procName);
            if (addr == IntPtr.Zero)
                addr = GetProcAddress(OpenGLModule, procName);
            return addr;
        }

        private void EnsureVfxRenderSession()
        {
            if (_vfxRenderer != null || _gl == null || !_isActive || _isCleanedUp) return;

            var renderer = new VfxRenderSession(LogService, VfxLoadingService);
            try
            {
                renderer.Initialize(_gl, AppSettings);
                _vfxRenderer = renderer;
            }
            catch
            {
                RunReleaseStep(nameof(VfxRenderSession), renderer.Dispose, gpuBound: true);
                throw;
            }
        }

        private void RequestSystemInspection(VfxSystemDiagnosticItem systemItem)
        {
            if (_isCleanedUp) return;
            if (ReferenceEquals(_pendingSystem, systemItem)) return;
            if (ReferenceEquals(_model.SelectedSystem, systemItem) && HasSelectedSystemReady()) return;

            if (_inspectedSystem != null && !ReferenceEquals(_inspectedSystem, systemItem))
                RememberStandaloneRun(_inspectedSystem);

            _inspectedSystem = null;
            _pendingSystem = systemItem;
            if (systemItem == null) return;

            _pendingSpell = null;
            ResetPreviewContextForSelection();
        }

        private void TryInspectPendingSystem()
        {
            if (!_isActive || _isCleanedUp || _gl == null || _pendingSystem == null)
                return;

            VfxSystemDiagnosticItem systemItem = _pendingSystem;
            _pendingSystem = null;

            try
            {
                EnsureVfxRenderSession();
                if (_vfxRenderer == null)
                {
                    return;
                }

                InspectSystem(systemItem);
            }
            catch (Exception ex)
            {
                LogService?.LogError(ex, "Failed to prepare the selected VFX system.");
                _model.LogMessages.Add($"[ERROR] Failed to prepare VFX system: {ex.Message}");
            }
        }

        private void OpenTkControl_Ready()
        {
            try
            {
                if (_isCleanedUp) return;

                _gl = Silk.NET.OpenGL.GL.GetApi(GetOpenGLProcAddress);
                if (_previewSurfaceRenderer == null)
                {
                    GroundAppearance appearance = SceneElements.LoadGroundAppearance(AppSettings, LogService);
                    _previewSurfaceRenderer = new PreviewSurfaceRenderer();
                    _previewSurfaceRenderer.Initialize(_gl, appearance);
                    _groundTextureDirty = false;
                }

                if (_championMeshRenderer == null)
                {
                    _championMeshRenderer = new GlMeshRenderer(AppSettings);
                    _championMeshRenderer.Initialize(_gl);
                }

                if (_mapGeometryRenderer == null)
                {
                    _mapGeometryRenderer = new MapGeometryRenderer(AppSettings);
                    _mapGeometryRenderer.Initialize(_gl);
                }
                if (_skyRenderer == null)
                {
                    _skyRenderer = new SkyRenderer();
                    _skyRenderer.Initialize(_gl);
                    _genericSkyCube ??= SceneElements.LoadGenericSkyCube(AppSettings, LogService);
                    _skyCubeDirty = true;
                }
                if (_mapCharacterRenderer == null)
                {
                    _mapCharacterRenderer = new MapCharacterRenderer(AppSettings);
                    _mapCharacterRenderer.Initialize(_gl);
                }
                if (_mapParticleRenderer == null)
                {
                    _mapParticleRenderer = new MapParticleRenderer();
                    _mapParticleRenderer.Initialize(_gl, AppSettings);
                }
                _championAnimationService ??= new AnimationService(LogService);

                if (_characterInteractionController == null)
                {
                    _characterInteractionController = new ViewportModelInteractionController(
                        CameraInputSurface,
                        CharacterTransformGizmoCanvas,
                        () => _dummyViewport.Camera as ProjectionCamera,
                        _characterInteractionModels);
                    _characterInteractionController.WorldMatrixProvider =
                        model => GlMeshRenderer.CreateWorldMatrix(model, mirrorCharacterX: true);
                    _characterInteractionController.TransformChanged += CharacterInteraction_TransformChanged;
                    _characterInteractionController.SelectionRequested += CharacterInteraction_SelectionRequested;
                    RefreshCharacterInteractionTarget();
                }

                if (_cameraController == null)
                {
                    _cameraController = new CustomCameraController(_dummyViewport, CameraInputSurface);
                    _cameraController.RotationStarted += CameraController_RotationStarted;
                    _cameraController.RotationEnded += CameraController_RotationEnded;
                    ApplyCameraPreset(_model.PreviewCameraPreset, refit: true);
                }

                _model.LogMessages.Add("[GL] OpenGL viewport, preview surfaces and camera controller initialized successfully.");
                _model.LogMessages.Add(
                    $"[GL] Vendor={_gl.GetStringS(Silk.NET.OpenGL.StringName.Vendor)} | " +
                    $"Renderer={_gl.GetStringS(Silk.NET.OpenGL.StringName.Renderer)} | " +
                    $"OpenGL={_gl.GetStringS(Silk.NET.OpenGL.StringName.Version)} | " +
                    $"GLSL={_gl.GetStringS(Silk.NET.OpenGL.StringName.ShadingLanguageVersion)}");
                _gl.GetInteger(Silk.NET.OpenGL.GLEnum.MaxTextureImageUnits, out int textureUnits);
                _gl.GetInteger(Silk.NET.OpenGL.GLEnum.MaxVertexAttribs, out int vertexAttributes);
                _model.LogMessages.Add(
                    $"[GL] Limits: fragment texture units={textureUnits}, vertex attributes={vertexAttributes}.");

            }
            catch (Exception ex)
            {
                _model.LogMessages.Add($"[ERROR] GL Init failed: {ex.Message}");
            }
        }

        private void OpenTkControl_Render(TimeSpan delta)
        {
            if (_gl == null) return;

            _championMeshRenderer?.ProcessPendingReleases();
            ProcessSceneActorGpuState();
            if (_isExitPending)
            {
                // ReleaseCurrentProject clears the CPU/runtime ownership first. Finish the matching
                // GPU teardown here while the OpenGL context is current, then drop any grace-period
                // resources that no longer have an owner because an explicit Studio exit should
                // leave no project or MAP allocation behind.
                ApplyPendingMapGpuState();
                _mapGeometryRenderer?.PurgeReleasedResources();
                _vfxRenderer?.SetSystem(null);
                _isExitPending = false;
                ExitRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (!_isActive || !IsVisible) return;

            int width = OpenTkControl.FrameBufferWidth;
            int height = OpenTkControl.FrameBufferHeight;
            if (width <= 0 || height <= 0) return;
            float dt = ResolveSimulationFrameDelta(delta, _discardNextSimulationDelta);
            _discardNextSimulationDelta = false;
            RenderViewportScene(width, height, dt);
            _snapshotService.ProcessPendingSnapshot(ref _pendingSnapshot, _gl, width, height,
                (captureWidth, captureHeight) => RenderViewportScene(captureWidth, captureHeight, 0f, snapshot: true), LogService);
        }

        private void RenderViewportScene(int width, int height, float dt, bool snapshot = false)
        {
            _gl.Viewport(0, 0, (uint)width, (uint)height);

            if (_groundTextureDirty)
            {
                _groundTextureDirty = false;
                try
                {
                    _previewSurfaceRenderer?.SetGroundAppearance(SceneElements.LoadGroundAppearance(AppSettings, LogService));
                }
                catch (Exception ex)
                {
                    LogService?.LogError(ex, "Failed to refresh the 3D Studio ground texture.");
                }
            }

            // The first-pose wait ends once a pose landed or nothing is being prepared any more.
            if (_championAwaitingFirstPose &&
                (_animationClipCancellation == null || _championModel?.CurrentAnimation != null))
            {
                _championAwaitingFirstPose = false;
            }

            if (!snapshot) AdvanceCharacterAutoRotate(dt);

            // Update background clear color matching main viewer (Dark Studio)
            switch (_model.BgMode)
            {
                case "Light":
                    _gl.ClearColor(0.85f, 0.85f, 0.88f, 1.0f);
                    break;
                case "Alpha":
                case "Transparent":
                    _gl.ClearColor(0.0f, 0.0f, 0.0f, 0.0f);
                    break;
                default: // Dark Studio
                    _gl.ClearColor(0.08f, 0.09f, 0.12f, 1.0f);
                    break;
            }

            _gl.ClearStencil(0);
            _gl.StencilMask(0xFFu);
            _gl.Clear(
                Silk.NET.OpenGL.ClearBufferMask.ColorBufferBit |
                Silk.NET.OpenGL.ClearBufferMask.DepthBufferBit |
                Silk.NET.OpenGL.ClearBufferMask.StencilBufferBit);

            if (_model.BgMode is "Alpha" or "Transparent")
            {
                _checkerboardBackgroundRenderer ??= new CheckerboardBackgroundRenderer(_gl);
                _checkerboardBackgroundRenderer.Render(16f * (float)VisualTreeHelper.GetDpi(this).DpiScaleX);
            }

            // Build view/projection matrices from the active preview camera. Orthographic presets
            // use the same camera controller but require their own projection matrix.
            _cameraController?.ApplyPendingRotation();
            if (_dummyViewport.Camera is not ProjectionCamera camera) return;

            var eye = new Vector3((float)camera.Position.X, (float)camera.Position.Y, (float)camera.Position.Z);
            var lookDir = new Vector3((float)camera.LookDirection.X, (float)camera.LookDirection.Y, (float)camera.LookDirection.Z);
            var target = eye + lookDir;
            var up = new Vector3((float)camera.UpDirection.X, (float)camera.UpDirection.Y, (float)camera.UpDirection.Z);
            var view = Matrix4x4.CreateLookAt(eye, target, up);

            float aspect = (float)width / height;
            bool hasMapScene = _mapSceneRuntime != null;
            float projectionNear = hasMapScene
                ? CameraPresets.CalculateProjectionNearPlane(lookDir, isMapGeometry: true)
                : CameraPresets.StudioNearPlane;
            float projectionFar = hasMapScene
                ? CameraPresets.CalculateProjectionFarPlane(lookDir)
                : CameraPresets.StudioFarPlane;
            Matrix4x4 proj = camera switch
            {
                PerspectiveCamera perspective => Matrix4x4.CreatePerspectiveFieldOfView(
                    (float)(perspective.FieldOfView * (Math.PI / 180.0)),
                    aspect,
                    projectionNear,
                    projectionFar),
                OrthographicCamera orthographic => Matrix4x4.CreateOrthographic(
                    (float)Math.Max(1d, orthographic.Width),
                    (float)Math.Max(1d, orthographic.Width / Math.Max(0.001f, aspect)),
                    projectionNear,
                    projectionFar),
                _ => Matrix4x4.Identity
            };
            var viewProj = view * proj;
            if (!snapshot) _characterInteractionController?.Update(viewProj);

            // OpenTK has the current context here, so deferred session creation and resource
            // preparation are safe even when WPF selected the system before the GL control was ready.
            TryInspectPendingSystem();
            _vfxRenderer?.ProcessPendingGpuState();
            ApplyPendingSkyGpuState();
            ApplyPendingMapGpuState();
            _mapGeometryRenderer?.ProcessRetainedResources();

            bool characterBackdrop = _mapSceneIsCharacterBackdrop && _model.IsSkinWorkspace;
            if (characterBackdrop && !snapshot)
            {
                AdvanceCurrentVfxPlayback(dt);
                UpdateChampionPoseForFrame();
                AdvanceSceneActors(dt);
            }

            // Sky is one Studio display element. MAP scenes supply their authored cubemap when available;
            // otherwise the same renderer falls back to the generic AssetsManager environment.
            if (_model.ShowPreviewSky)
                _skyRenderer?.Render(view, proj);

            // PBR game shaders light characters and structures from the same environment the sky shows.
            CubeMapData imageLight = ActiveSkyCube;
            if (_championMeshRenderer != null)
                _championMeshRenderer.ImageLight = imageLight;
            if (_mapCharacterRenderer != null)
            {
                _mapCharacterRenderer.ImageLight = imageLight;
                // Map characters add their glow to the champion's bloom, composed once per frame.
                _mapCharacterRenderer.Bloom = _championMeshRenderer?.Bloom;
            }

            // Ground navigation belongs to the MAP workspace; a Character backdrop keeps orbiting its subject.
            if (_cameraController != null)
            {
                _cameraController.MapNavigationGroundHeight = _mapSceneRuntime != null && !_mapSceneIsCharacterBackdrop
                    ? _mapSceneRuntime.Scene.Origin?.Y
                    : null;
            }

            // A MAP scene owns the world backdrop. In Character-backdrop mode the selected Skin remains
            // the subject and is composited into the same depth/particle/post-processing frame.
            _frameTerrainDepth = 0;
            if (_mapSceneRuntime != null)
            {
                if (!snapshot)
                {
                    AdvanceMapCharacterClip(dt);
                    _mapSceneRuntime.Update(viewProj, dt);
                }
                // Three.js draws all scene opaque queues before any transparent queue.
                for (int phase = 0; phase < 2; phase++)
                {
                    _mapGeometryRenderer?.Render(
                        viewProj,
                        view,
                        proj,
                        eye,
                        _mapSceneRuntime.SceneTimeSeconds,
                        _model.PreviewViewMode,
                        _model.EffectivePreviewWireOverlay,
                        _model.PreviewShaders,
                        transparentPass: phase == 1);
                    if (phase == 0)
                        CaptureTerrainDepth(width, height);
                    if (_mapSceneRuntime.ShowStructures)
                    {
                        _mapCharacterRenderer?.Render(
                            _mapSceneRuntime.CharacterGroups,
                            viewProj,
                            view,
                            proj,
                            eye,
                            _mapSceneRuntime.CharacterTimeSeconds,
                            EffectiveMapSun(),
                            _mapSceneRuntime.Hidden,
                            viewMode: _model.PreviewViewMode,
                            wireOverlay: _model.EffectivePreviewWireOverlay,
                            shadersEnabled: _model.PreviewShaders,
                            lightGrid: _mapSceneRuntime.Scene.LightGrid,
                            transparentPass: phase == 1);
                    }
                }
                if (characterBackdrop)
                {
                    RenderChampionMesh(viewProj, view, proj, eye);
                    RenderSceneActorMeshes(viewProj, view, proj, eye);
                    if (!snapshot) UpdateCharacterArmatureOverlay(viewProj);
                }
                uint mapViewportWidth = (uint)width;
                uint mapViewportHeight = (uint)height;
                _mapPostEffectsRenderer?.CaptureSceneDepth(
                    EffectiveMapPostEffects(),
                    EffectiveMapSsao(),
                    mapViewportWidth,
                    mapViewportHeight);

                // LTK has one global particle pass block for the scene. MAP placements and every
                // Character session use different coordinate spaces/render owners here, so keep their
                // renderers separate but coordinate the phases: every soft-depth grab happens before
                // particle colour, then all colour/wire draws land before any renderer captures the
                // frame used by distortion.
                MapSunData sun = EffectiveMapSun();
                _mapParticleRenderer?.SetSun(sun);
                _mapParticleRenderer?.SetTerrainDepth(_frameTerrainDepth, _frameTerrainWidth, _frameTerrainHeight);
                bool mapParticlesPrepared = _mapSceneRuntime.ShowParticles &&
                    _mapParticleRenderer?.PrepareRenderFrame(
                        _mapSceneRuntime.Particles.VisibleRuntimes,
                        viewProj,
                        view,
                        mapViewportWidth,
                        mapViewportHeight,
                        _model.PreviewViewMode,
                        _model.EffectivePreviewWireOverlay) == true;
                if (mapParticlesPrepared)
                    _preparedParticlePasses.Add(_mapParticleRenderer);

                bool shouldDrawSceneVfx = HasSelectedMapClipReady() ||
                    (characterBackdrop && ShouldRenderCharacterVfx() && IsFocusedActorVisible && !_championAwaitingFirstPose);
                if (shouldDrawSceneVfx && _vfxRenderer?.ActiveSystem != null)
                    PrepareParticleSession(_vfxRenderer, sun, viewProj, view, width, height);
                if (characterBackdrop)
                    PrepareSceneActorParticles(sun, viewProj, view, width, height);
                RenderPreparedParticlePasses();
            }
            else
                _previewSurfaceRenderer?.Render(
                    viewProj,
                    _model.ShowPreviewGrid,
                    _model.ShowPreviewGround,
                    _model.ShowPreviewStage);

            if (!characterBackdrop)
            {
                if (!snapshot)
                {
                    AdvanceCurrentVfxPlayback(dt);
                    UpdateChampionPoseForFrame();
                    AdvanceSceneActors(dt);
                }
                RenderChampionMesh(viewProj, view, proj, eye);
                RenderSceneActorMeshes(viewProj, view, proj, eye);
                if (!snapshot) UpdateCharacterArmatureOverlay(viewProj);
            }

            // Without a MAP, every Character session still shares one particle pass after all meshes.
            if (_mapSceneRuntime == null)
            {
                if (_vfxRenderer != null && ShouldRenderCharacterVfx() && IsFocusedActorVisible && !_championAwaitingFirstPose)
                    PrepareParticleSession(_vfxRenderer, null, viewProj, view, width, height);
                PrepareSceneActorParticles(null, viewProj, view, width, height);
                RenderPreparedParticlePasses();
            }
            _model.LiveParticleCount = (_vfxRenderer?.LiveParticleCount ?? 0) + SceneActorParticleCount();

            if (_mapSceneRuntime != null)
            {
                _mapPostEffectsRenderer?.Render(
                    EffectiveMapPostEffects(),
                    EffectiveMapSsao(),
                    view,
                    proj,
                    (uint)width,
                    (uint)height);
            }

            // Glow the champion's game shaders wrote, once every mesh, particle and post effect is drawn.
            _championMeshRenderer?.ComposeBloom();

            if (AppSettings?.StudioParameters?.EnableFxaa ?? true)
            {
                if (AppSettings?.StudioParameters?.AntiAliasingMode == "Smaa")
                {
                    if (_smaaRenderer == null)
                    {
                        _smaaRenderer = new SmaaPostEffectsRenderer();
                        _smaaRenderer.Initialize(_gl);
                    }
                    _smaaRenderer.Render(width, height);
                }
                else
                {
                    EnsureFxaaRenderer();
                    _fxaaRenderer?.Render(width, height);
                }
            }
            else
            {
                _fxaaRenderer?.Dispose();
                _fxaaRenderer = null;
                _smaaRenderer?.Dispose();
                _smaaRenderer = null;
            }

            if (_vfxRenderer != null)
            {
                // Live active particle count per emitter lane (matches LTK Manager liveCount badge)
                foreach (var emitter in _model.Emitters)
                    emitter.ActiveParticleCount = _vfxRenderer.GetEmitterLiveCount(emitter.SourceOrder);
            }

            if (!snapshot) QueuePlayheadRefresh();
        }

        /// <summary>Copies the depth the map geometry just wrote, before structures and characters draw over it.</summary>
        private void CaptureTerrainDepth(int width, int height)
        {
            if (_gl == null)
                return;

            _terrainDepthCapture ??= new AssetsManager.Services.Viewer.Rendering.Core.GlSceneCapture(_gl);
            _frameTerrainWidth = (uint)width;
            _frameTerrainHeight = (uint)height;
            _terrainDepthCapture.Capture(_frameTerrainWidth, _frameTerrainHeight, captureColor: false, captureDepth: true);
            _frameTerrainDepth = _terrainDepthCapture.DepthTexture;
        }

        private void AdvanceCurrentVfxPlayback(float dt)
        {
            // MAP character clips own the same VFX session clock themselves so their pose, cues
            // and ParticleEventData stay on one timeline. Other previews keep the generic session clock.
            if (HasSelectedMapClipReady() ||
                !_model.IsPlaying || _isUserSeeking || _vfxRenderer?.ActiveSystem == null)
            {
                return;
            }

            _vfxRenderer.ActiveSystem.Speed = _model.Speed;
            _vfxRenderer.Update(dt);
            _model.CurrentTime = _vfxRenderer.PlaybackTime;
            if (ShouldRestartPreview(_model.IsPreviewLoopEnabled, _model.CurrentTime, _model.ActiveLoopDuration))
            {
                double loopStart = ResolvePreviewLoopRestart(
                    _model.ActiveLoopStart,
                    _model.ActiveLoopDuration,
                    _model.TotalDuration);
                _vfxRenderer.Seek(loopStart);
                _vfxRenderer.Play();
                _model.CurrentTime = loopStart;
            }
            else if (_model.CurrentTime >= _model.TotalDuration)
            {
                _model.IsPlaying = false;
            }
        }

        private void UpdateChampionPoseForFrame()
        {
            if (_activeMapCharacterClip != null ||
                _championModel == null || _championAnimationService == null)
            {
                return;
            }

            // A pending clip resets the playhead, but the last visible pose must stay frozen.
            if (_model.SelectedAnimation is { IsBindPose: false } && _activeAnimationClip == null)
                return;

            ApplyAnimationClipCues(_model.CurrentTime);
            if (_championModel.CurrentAnimation != null && _championModel.Skeleton != null)
            {
                float animationTime = _activeSpellPlan?.Animation != null
                    ? SpellAnimationTime(_model.CurrentTime, _activeSpellPlan.Animation.Duration)
                    : (float)_model.CurrentTime;
                _championAnimationService.Update(
                    animationTime,
                    _championModel.CurrentAnimation,
                    _championModel.Skeleton,
                    _championModel.SkinnedMesh,
                    _championModel.Parts,
                    _championModel.Name, _championModel);
                _championModel.SkinningMatrices = _championAnimationService.FinalBoneTransforms;
                _championModel.GpuSkinningData = _championAnimationService.SkinningData;
                _vfxRenderer?.SetOwnerSkinningMatrices(_championAnimationService.FinalBoneTransforms);
                _vfxRenderer?.UpdateBoneTransforms((boneName, boneHash) =>
                {
                    if (!string.IsNullOrEmpty(boneName) &&
                        _championAnimationService.TryGetBoneTransformExactName(boneName, out Matrix4x4 transform))
                    {
                        return transform;
                    }
                    if (boneHash != 0 && _championAnimationService.TryGetBoneTransformFnv(boneHash, out transform))
                        return transform;
                    return null;
                });
            }
            else if (_model.SelectedSystem == null)
            {
                _vfxRenderer?.SetOwnerSkinningMatrices(null);
                _vfxRenderer?.UpdateBoneTransforms(null);
            }
        }

        private void RenderChampionMesh(
            Matrix4x4 viewProjection,
            Matrix4x4 view,
            Matrix4x4 projection,
            Vector3 eye)
        {
            if (_activeMapCharacterClip != null ||
                !_model.ShowChampionMesh ||
                !IsFocusedActorVisible ||
                _championAwaitingFirstPose ||
                _championModel == null)
            {
                return;
            }

            RenderCharacterMesh(_championModel, viewProjection, view, projection, eye);
        }

        /// <summary>Draws one Studio Character with the reference lighting and the active preview modes.</summary>
        private void RenderCharacterMesh(
            SceneModel model,
            Matrix4x4 viewProjection,
            Matrix4x4 view,
            Matrix4x4 projection,
            Vector3 eye)
        {
            if (_championMeshRenderer == null || model == null) return;

            var lighting = GlMeshRenderer.ReferenceCharacterLighting();
            _championMeshRenderer.Render(
                model,
                viewProjection,
                view,
                projection,
                eye,
                lighting.LightDirection,
                lighting.LightColor,
                lighting.FillDirection,
                lighting.FillColor,
                lighting.AmbientColor,
                _model.PreviewViewMode,
                _model.EffectivePreviewWireOverlay,
                _model.PreviewShaders,
                mirrorCharacterX: true,
                mapSun: _mapSceneRuntime != null ? EffectiveMapSun() : null,
                lightGrid: _mapSceneRuntime?.Scene.LightGrid);
        }

        private bool ShouldRenderCharacterVfx()
        {
            if (!_model.IsSkinWorkspace) return true;
            // Explicit System inspection remains visible. The Character Effects switch owns the
            // Skin-driven Clip/Spell/idle effects rather than muting an explicitly opened System.
            return _model.SelectedSystem != null || _model.CharacterEffectsEnabled;
        }
    }
}
