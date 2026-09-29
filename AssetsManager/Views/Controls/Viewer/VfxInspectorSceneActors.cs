using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    /// <summary>
    /// Multi-Character Skin scenes. The focused actor runs through the Studio inspection pipeline;
    /// every other actor keeps its own mesh, pose and VFX session in a VfxSceneActorRuntime. Focus
    /// changes hand those objects over in both directions so no Character is decoded twice.
    /// </summary>
    public partial class VfxInspectorControl
    {
        internal const int MaxSceneActors = 8;
        private const double SceneActorSpacing = 180d;

        private readonly Dictionary<VfxSceneActor, VfxSceneActorRuntime> _sceneActorRuntimes = new();
        private readonly Dictionary<VfxSceneActor, CancellationTokenSource> _sceneActorLoads = new();
        private readonly List<VfxRenderSession> _retiredSceneActorSessions = new();
        private readonly List<IPreparedParticlePass> _preparedParticlePasses = new();
        private readonly List<IDisposable> _preparedParticleBatches = new();
        private VfxSceneActorRuntime _pendingActorAdoption;
        private bool _isSceneFocusHandover;
        private bool _openSkinInOwnTab;
        private bool _startNextPreviewPaused;
        private double _startNextPreviewTime;

        /// <summary>The Character driven by the Inspector, browser and timeline in a Skin workspace.</summary>
        private VfxSceneActor FocusedActor =>
            _model.SelectedWorkspaceTab?.Kind == VfxWorkspaceTabKind.Skin
                ? _model.SelectedWorkspaceTab.FocusedActor
                : null;

        private bool IsFocusedActorVisible => FocusedActor?.IsVisible != false;

        /// <summary>
        /// Consumed by the next System/Clip/Spell start: the preview restored for a newly focused actor
        /// keeps that actor's own paused state. Explicit browser selections always play.
        /// </summary>
        private bool TakeStartPreviewPaused()
        {
            bool paused = _startNextPreviewPaused;
            _startNextPreviewPaused = false;
            return paused;
        }

        /// <summary>The clip time a newly focused actor was at, so its restored clip resumes there.</summary>
        private double TakeStartPreviewTime()
        {
            double time = _startNextPreviewTime;
            _startNextPreviewTime = 0d;
            return time;
        }

        private VfxSceneActor CreateSceneActor(VfxSkinItem skin)
        {
            var actor = new VfxSceneActor(skin);
            actor.PropertyChanged += SceneActor_PropertyChanged;
            return actor;
        }

        private void SceneActor_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(VfxSceneActor.IsVisible)) return;
            RefreshCharacterInteractionTarget();
            OpenTkControl?.InvalidateVisual();
        }

        #region Scene composition

        /// <summary>
        /// Adds a Skin to the active Skin scene next to the existing Characters. Without an active
        /// Skin scene the Skin simply opens in its own workspace tab.
        /// </summary>
        private void AddSkinToScene(VfxSkinItem skin)
        {
            if (skin == null || _isCleanedUp) return;
            VfxWorkspaceTab tab = _model.SelectedWorkspaceTab;
            if (tab?.Kind != VfxWorkspaceTabKind.Skin || tab.FocusedActor == null)
            {
                OpenSkin(skin, ownTab: false);
                return;
            }

            if (tab.Actors.Count >= MaxSceneActors)
            {
                _model.StatusText = $"A scene holds up to {MaxSceneActors} Characters.";
                return;
            }

            CaptureCharacterWorkspaceState(tab);
            VfxSceneActor anchor = tab.FocusedActor;
            VfxSceneActor actor = CreateSceneActor(skin);
            actor.PositionX = tab.Actors.Max(existing => existing.PositionX) + SceneActorSpacing;
            actor.PositionY = anchor.PositionY;
            actor.PositionZ = anchor.PositionZ;
            // On a MAP backdrop a Character the map spawns (a jungle camp, a drake) stands where the game puts it.
            if (_model.HasActiveCharacterBackdrop &&
                TryGetBackdropSpawn(_mapSceneRuntime?.Scene, skin, out Vector3 spawn, out double yaw))
            {
                actor.PositionX = spawn.X;
                actor.PositionY = spawn.Y;
                actor.PositionZ = spawn.Z;
                actor.RotationY = yaw;
            }
            // Added actors keep their offset: a MAP origin never snaps them onto the anchor actor.
            actor.PlacementCustomized = true;
            actor.PlacedOnKey = anchor.PlacedOnKey;
            tab.Actors.Add(actor);
            StartSceneActorLoad(actor);
            _model.StatusText = $"Added {actor.Title} to the scene.";
        }

        /// <summary>Opens a Skin through the regular browser flow, optionally in its own tab.</summary>
        private void OpenSkin(VfxSkinItem skin, bool ownTab)
        {
            if (skin == null) return;
            _openSkinInOwnTab = ownTab;
            try
            {
                _model.SelectedSkin = skin;
            }
            finally
            {
                _openSkinInOwnTab = false;
            }
        }

        /// <summary>
        /// Moves Inspector/timeline ownership to another actor of the active scene. Loaded runtimes are
        /// swapped with the focused pipeline instead of being disposed and decoded again.
        /// </summary>
        private void FocusSceneActor(VfxSceneActor actor)
        {
            VfxWorkspaceTab tab = _model.SelectedWorkspaceTab;
            if (actor == null || tab?.Kind != VfxWorkspaceTabKind.Skin || !tab.Actors.Contains(actor))
                return;
            VfxSceneActor previous = tab.FocusedActor;
            if (ReferenceEquals(previous, actor))
                return;

            CaptureWorkspaceSelection(tab);
            if (previous != null)
                previous.IsPlaybackPaused = !_model.IsPlaying;
            VfxSceneActorRuntime promoted = TakeSceneActorRuntime(actor);
            VfxSceneActorRuntime demoted = DetachFocusedRuntime();
            tab.FocusedActor = actor;
            // The Inspector must hold the new actor's placement before its model is installed as
            // focused; otherwise the previous actor's position would be applied to (and saved into) it.
            LoadFocusedPlacement(actor);

            if (previous != null)
            {
                if (demoted != null)
                    RegisterSceneActorRuntime(previous, demoted);
                else
                    StartSceneActorLoad(previous);
            }

            _pendingActorAdoption = promoted;
            _pendingWorkspaceRestoreTab = tab;
            _startNextPreviewPaused = actor.IsPlaybackPaused;
            _startNextPreviewTime = promoted?.Clip != null ? promoted.Session.PlaybackTime : 0d;
            _isSceneFocusHandover = true;
            _isSwitchingWorkspaceTab = true;
            try
            {
                // Rebinds the browser/Inspector to the actor; LoadBinFile adopts the promoted runtime.
                _model.SelectedSkin = actor.Skin;
            }
            finally
            {
                _isSwitchingWorkspaceTab = false;
                _isSceneFocusHandover = false;
            }

            // An adoption that did not match the Skin (for example a failed load) must not leak.
            if (_pendingActorAdoption != null)
            {
                ReleaseSceneActorRuntime(_pendingActorAdoption);
                _pendingActorAdoption = null;
            }
            RefreshCharacterInteractionTarget();
        }

        private void RemoveSceneActor(VfxSceneActor actor)
        {
            VfxWorkspaceTab tab = _model.SelectedWorkspaceTab;
            if (actor == null || tab?.Kind != VfxWorkspaceTabKind.Skin ||
                tab.Actors.Count <= 1 || !tab.Actors.Contains(actor))
            {
                return;
            }

            if (ReferenceEquals(tab.FocusedActor, actor))
            {
                int index = tab.Actors.IndexOf(actor);
                FocusSceneActor(tab.Actors[index == 0 ? 1 : index - 1]);
            }

            tab.Actors.Remove(actor);
            actor.PropertyChanged -= SceneActor_PropertyChanged;
            CancelSceneActorLoad(actor);
            if (_sceneActorRuntimes.Remove(actor, out VfxSceneActorRuntime runtime))
                ReleaseSceneActorRuntime(runtime);
            RefreshCharacterInteractionTarget();
            OpenTkControl?.InvalidateVisual();
        }

        #endregion

        #region Runtime ownership

        /// <summary>
        /// Keeps runtimes only for the non-focused actors of the given Skin scene and starts loading
        /// the missing ones. Any other scene's runtimes are released.
        /// </summary>
        private void SyncSceneActorRuntimes(VfxWorkspaceTab tab)
        {
            var wanted = new HashSet<VfxSceneActor>();
            if (tab?.Kind == VfxWorkspaceTabKind.Skin)
            {
                foreach (VfxSceneActor actor in tab.Actors)
                {
                    if (!ReferenceEquals(actor, tab.FocusedActor))
                        wanted.Add(actor);
                }
            }

            foreach (VfxSceneActor actor in _sceneActorLoads.Keys.Where(actor => !wanted.Contains(actor)).ToArray())
                CancelSceneActorLoad(actor);
            foreach (VfxSceneActor actor in _sceneActorRuntimes.Keys.Where(actor => !wanted.Contains(actor)).ToArray())
            {
                _sceneActorRuntimes.Remove(actor, out VfxSceneActorRuntime runtime);
                ReleaseSceneActorRuntime(runtime);
            }
            foreach (VfxSceneActor actor in wanted)
            {
                if (!_sceneActorRuntimes.ContainsKey(actor) && !_sceneActorLoads.ContainsKey(actor))
                    StartSceneActorLoad(actor);
            }
            RefreshCharacterInteractionTarget();
        }

        private void ReleaseSceneActorRuntimes() => SyncSceneActorRuntimes(null);

        private async void StartSceneActorLoad(VfxSceneActor actor)
        {
            if (actor == null || _isCleanedUp || VfxLoadingService == null || SknLoadingService == null)
                return;

            CancelSceneActorLoad(actor);
            var operation = new CancellationTokenSource();
            _sceneActorLoads[actor] = operation;
            actor.IsLoading = true;
            actor.StatusText = "Loading…";
            VfxSceneActorRuntime runtime = null;
            try
            {
                runtime = await VfxSceneActorRuntime.LoadAsync(
                    actor,
                    VfxLoadingService,
                    SknLoadingService,
                    LogService,
                    _model.RootPath,
                    operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (_isCleanedUp || !IsBackgroundSceneActor(actor))
                    return;
                if (runtime == null)
                {
                    actor.StatusText = "Character mesh unavailable";
                    return;
                }

                // Enter the scene already posed on its clip, never flashing the bind pose first.
                await runtime.PlayRememberedClipAsync(actor, LogService, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (_isCleanedUp || !IsBackgroundSceneActor(actor))
                    return;
                RegisterSceneActorRuntime(actor, runtime);
                runtime = null;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogService?.LogError(ex, $"Failed to load scene actor {actor.Title}.");
                actor.StatusText = "Load failed";
            }
            finally
            {
                if (runtime != null)
                    ReleaseSceneActorRuntime(runtime);
                if (_sceneActorLoads.TryGetValue(actor, out CancellationTokenSource active) &&
                    ReferenceEquals(active, operation))
                {
                    _sceneActorLoads.Remove(actor);
                    actor.IsLoading = false;
                }
                operation.Dispose();
            }
        }

        private bool IsBackgroundSceneActor(VfxSceneActor actor)
        {
            VfxWorkspaceTab tab = _model.SelectedWorkspaceTab;
            return tab?.Kind == VfxWorkspaceTabKind.Skin &&
                   tab.Actors.Contains(actor) &&
                   !ReferenceEquals(tab.FocusedActor, actor);
        }

        private void CancelSceneActorLoad(VfxSceneActor actor)
        {
            if (actor == null || !_sceneActorLoads.Remove(actor, out CancellationTokenSource operation))
                return;
            operation.Cancel();
            actor.IsLoading = false;
        }

        private void RegisterSceneActorRuntime(VfxSceneActor actor, VfxSceneActorRuntime runtime)
        {
            _sceneActorRuntimes[actor] = runtime;
            runtime.SetGameStates(actor.EnabledGameStates);
            runtime.ApplyPlacement(actor, _model.CharacterAutoRotate ? _characterAutoRotateDegrees : 0d);
            actor.StatusText = runtime.DescribePlayback();
            RefreshCharacterInteractionTarget();
            OpenTkControl?.InvalidateVisual();
        }

        private VfxSceneActorRuntime TakeSceneActorRuntime(VfxSceneActor actor)
        {
            CancelSceneActorLoad(actor);
            return _sceneActorRuntimes.Remove(actor, out VfxSceneActorRuntime runtime) ? runtime : null;
        }

        /// <summary>
        /// Moves the focused Character's loaded objects into a background runtime. The clip that is
        /// already playing keeps running; System/Spell inspection falls back to the actor's own clip.
        /// </summary>
        private VfxSceneActorRuntime DetachFocusedRuntime()
        {
            if (_championModel == null || _championBundle == null ||
                !ReferenceEquals(_championBundle, _activeBundle) ||
                _championAnimationService == null || VfxLoadingService == null)
            {
                return null;
            }

            if (_inspectedSystem != null)
            {
                RememberStandaloneRun(_inspectedSystem);
                _inspectedSystem = null;
            }
            _animationClipCancellation?.Cancel();
            _animationClipCancellation = null;

            AnimationClipCatalogItem playingClip =
                _activeSpellPlan == null && _model.SelectedSystem == null &&
                _activeAnimationClip?.AnimationAsset != null && _vfxRenderer?.ActiveSystem != null
                    ? _activeAnimationClip
                    : null;
            VfxRenderSession session = _vfxRenderer ?? new VfxRenderSession(LogService, VfxLoadingService);
            var runtime = new VfxSceneActorRuntime(
                VfxLoadingService,
                _championBundle,
                _model.SelectedCharacterForm?.Definition,
                _championModel,
                _championSknPath,
                _championAnimationService,
                session,
                _clipCatalog,
                ResolvePreviewSearchDirectory());

            _championModel = null;
            _championBundle = null;
            _championSknPath = null;
            _clipCatalog = null;
            _championAnimationService = null;
            _vfxRenderer = null;
            _activeAnimationClip = null;
            _animationBasePartVisibility.Clear();
            _animationBaseHiddenSubmeshes.Clear();

            if (playingClip != null)
            {
                runtime.AdoptPlayingClip(playingClip);
            }
            else
            {
                VfxSceneActor owner = FocusedActor;
                _ = PlayDemotedClipAsync(owner, runtime);
            }
            return runtime;
        }

        private async Task PlayDemotedClipAsync(VfxSceneActor actor, VfxSceneActorRuntime runtime)
        {
            try
            {
                await runtime.PlayRememberedClipAsync(actor, LogService, CancellationToken.None);
                if (actor != null &&
                    _sceneActorRuntimes.TryGetValue(actor, out VfxSceneActorRuntime current) &&
                    ReferenceEquals(current, runtime))
                {
                    actor.StatusText = runtime.DescribePlayback();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogService?.LogError(ex, $"Failed to resume scene actor {actor?.Title}.");
            }
        }

        /// <summary>
        /// Returns the runtime handed over by FocusSceneActor when it belongs to the BIN being opened.
        /// </summary>
        private VfxSceneActorRuntime TakePendingActorAdoption(string binFilePath)
        {
            VfxSceneActorRuntime adoption = _pendingActorAdoption;
            if (adoption?.Bundle?.PrimaryBinPath == null ||
                !string.Equals(
                    Path.GetFullPath(adoption.Bundle.PrimaryBinPath),
                    Path.GetFullPath(binFilePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            _pendingActorAdoption = null;
            return adoption;
        }

        /// <summary>Installs a background runtime's pose evaluator, session and clip cache as focused.</summary>
        private void AdoptFocusedServices(VfxSceneActorRuntime runtime)
        {
            if (!ReferenceEquals(_championAnimationService, runtime.Animation))
            {
                _championAnimationService?.Dispose();
                _championAnimationService = runtime.Animation;
            }
            if (!ReferenceEquals(_vfxRenderer, runtime.Session))
            {
                RetireSceneActorSession(_vfxRenderer);
                _vfxRenderer = runtime.Session;
                _vfxRenderer.SetBoneTransformSampler(null);
            }
            _clipCatalog?.Dispose();
            _clipCatalog = runtime.ClipCatalog;
        }

        private void ReleaseSceneActorRuntime(VfxSceneActorRuntime runtime)
        {
            if (runtime == null) return;
            _championMeshRenderer?.QueueRelease(runtime.Model);
            RunReleaseStep("Scene actor", runtime.ReleaseCpuState);
            RetireSceneActorSession(runtime.Session);
        }

        /// <summary>
        /// An initialized session owns GL objects and is disposed on the next render callback while the
        /// context is current; a session that never reached the GPU is released immediately.
        /// </summary>
        private void RetireSceneActorSession(VfxRenderSession session)
        {
            if (session == null) return;
            if (!session.IsInitialized || _gl == null)
                RunReleaseStep(nameof(VfxRenderSession), session.Dispose, gpuBound: true);
            else
                _retiredSceneActorSessions.Add(session);
        }

        /// <summary>Render-callback GPU work for scene actors: teardown, lazy init and queued purges.</summary>
        private void ProcessSceneActorGpuState()
        {
            foreach (VfxRenderSession session in _retiredSceneActorSessions)
                RunReleaseStep(nameof(VfxRenderSession), session.Dispose, gpuBound: true);
            _retiredSceneActorSessions.Clear();

            if (_vfxRenderer != null && !_vfxRenderer.IsInitialized && _gl != null)
                InitializeSession(_vfxRenderer);
            foreach (VfxSceneActorRuntime runtime in _sceneActorRuntimes.Values)
            {
                if (!runtime.Session.IsInitialized && _gl != null)
                    InitializeSession(runtime.Session);
                runtime.Session.ProcessPendingGpuState();
            }
        }

        private void InitializeSession(VfxRenderSession session)
        {
            try
            {
                session.Initialize(_gl, AppSettings);
            }
            catch (Exception ex)
            {
                LogService?.LogError(ex, "Failed to initialize a scene actor VFX session.");
            }
        }

        private void DisposeSceneActorResources()
        {
            foreach (CancellationTokenSource operation in _sceneActorLoads.Values)
                operation.Cancel();
            _sceneActorLoads.Clear();
            foreach (VfxSceneActorRuntime runtime in _sceneActorRuntimes.Values)
            {
                RunReleaseStep("Scene actor", runtime.ReleaseCpuState);
                RunReleaseStep(nameof(VfxRenderSession), runtime.Session.Dispose, gpuBound: true);
            }
            _sceneActorRuntimes.Clear();
            foreach (VfxRenderSession session in _retiredSceneActorSessions)
                RunReleaseStep(nameof(VfxRenderSession), session.Dispose, gpuBound: true);
            _retiredSceneActorSessions.Clear();
            _pendingActorAdoption = null;
        }

        #endregion

        #region Frame loop

        private void AdvanceSceneActors(float deltaSeconds)
        {
            if (_sceneActorRuntimes.Count == 0 || !_model.IsSkinWorkspace) return;
            double autoYaw = _model.CharacterAutoRotate ? _characterAutoRotateDegrees : 0d;
            foreach ((VfxSceneActor actor, VfxSceneActorRuntime runtime) in _sceneActorRuntimes)
            {
                if (_model.CharacterAutoRotate)
                    runtime.ApplyPlacement(actor, autoYaw);
                runtime.Advance(deltaSeconds, !actor.IsPlaybackPaused, _model.Speed, actor);
            }
        }

        private void ApplySceneActorPlacements()
        {
            double autoYaw = _model.CharacterAutoRotate ? _characterAutoRotateDegrees : 0d;
            foreach ((VfxSceneActor actor, VfxSceneActorRuntime runtime) in _sceneActorRuntimes)
                runtime.ApplyPlacement(actor, autoYaw);
            OpenTkControl?.InvalidateVisual();
        }

        private void RenderSceneActorMeshes(Matrix4x4 viewProjection, Matrix4x4 view, Matrix4x4 projection, Vector3 eye)
        {
            if (!_model.IsSkinWorkspace || !_model.ShowChampionMesh) return;
            foreach ((VfxSceneActor actor, VfxSceneActorRuntime runtime) in _sceneActorRuntimes)
            {
                if (actor.IsVisible)
                    RenderCharacterMesh(runtime.Model, viewProjection, view, projection, eye);
            }
        }

        private void PrepareParticleSession(
            VfxRenderSession session,
            MapSunData sun,
            Matrix4x4 viewProjection,
            Matrix4x4 view)
        {
            session.SetSun(sun);
            session.SetViewportSize(OpenTkControl.ActualWidth, OpenTkControl.ActualHeight);
            if (session.PrepareRenderFrame(viewProjection, view, _model.PreviewViewMode, _model.EffectivePreviewWireOverlay))
                _preparedParticlePasses.Add(session);
        }

        private void PrepareSceneActorParticles(MapSunData sun, Matrix4x4 viewProjection, Matrix4x4 view)
        {
            if (!_model.IsSkinWorkspace || !_model.CharacterEffectsEnabled) return;
            foreach ((VfxSceneActor actor, VfxSceneActorRuntime runtime) in _sceneActorRuntimes)
            {
                if (actor.IsVisible && runtime.Session.ActiveSystem != null)
                    PrepareParticleSession(runtime.Session, sun, viewProjection, view);
            }
        }

        /// <summary>
        /// Draws every prepared particle owner as one global pass: all colour draws land before any
        /// owner captures the frame used by distortion.
        /// </summary>
        private void RenderPreparedParticlePasses()
        {
            try
            {
                PreparedParticlePasses.Render(_preparedParticlePasses, _preparedParticleBatches);
            }
            finally
            {
                _preparedParticlePasses.Clear();
            }
        }

        private int SceneActorParticleCount()
        {
            int count = 0;
            foreach ((VfxSceneActor actor, VfxSceneActorRuntime runtime) in _sceneActorRuntimes)
            {
                if (actor.IsVisible) count += runtime.LiveParticleCount;
            }
            return count;
        }

        #endregion

        #region Placement

        /// <summary>
        /// When the focused actor is re-anchored onto a MAP origin, actors placed for another stage move
        /// by the same offset so the scene layout is preserved on the new backdrop.
        /// </summary>
        private void ShiftSceneActorsWithAnchor(Vector3 delta, string sourceKey)
        {
            VfxWorkspaceTab tab = _model.SelectedWorkspaceTab;
            if (tab?.Kind != VfxWorkspaceTabKind.Skin || delta == Vector3.Zero) return;
            double autoYaw = _model.CharacterAutoRotate ? _characterAutoRotateDegrees : 0d;
            foreach (VfxSceneActor actor in tab.Actors)
            {
                if (ReferenceEquals(actor, tab.FocusedActor) ||
                    string.Equals(actor.PlacedOnKey, sourceKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                actor.PositionX += delta.X;
                actor.PositionY += delta.Y;
                actor.PositionZ += delta.Z;
                actor.PlacedOnKey = sourceKey;
                if (_sceneActorRuntimes.TryGetValue(actor, out VfxSceneActorRuntime runtime))
                    runtime.ApplyPlacement(actor, autoYaw);
            }
        }

        private VfxSceneActor SceneActorForModel(SceneModel model)
        {
            if (model == null) return null;
            if (ReferenceEquals(model, _championModel)) return FocusedActor;
            foreach ((VfxSceneActor actor, VfxSceneActorRuntime runtime) in _sceneActorRuntimes)
            {
                if (ReferenceEquals(runtime.Model, model)) return actor;
            }
            return null;
        }

        private void CharacterInteraction_SelectionRequested(SceneModel model, ModifierKeys modifiers)
        {
            VfxSceneActor actor = SceneActorForModel(model);
            if (actor != null && !ReferenceEquals(actor, FocusedActor))
                FocusSceneActor(actor);
        }

        /// <summary>World bounds of every visible Character in the Skin scene for camera framing.</summary>
        private VfxDefinitionBounds SceneCharacterBounds()
        {
            var min = new Vector3(float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity);
            void Include(SceneModel model)
            {
                VfxDefinitionBounds bounds = CharacterPreviewBounds(model);
                if (!IsFiniteBounds(bounds)) return;
                min = Vector3.Min(min, bounds.Min);
                max = Vector3.Max(max, bounds.Max);
            }

            if (_championModel != null && IsFocusedActorVisible)
                Include(_championModel);
            foreach ((VfxSceneActor actor, VfxSceneActorRuntime runtime) in _sceneActorRuntimes)
            {
                if (actor.IsVisible) Include(runtime.Model);
            }
            return new VfxDefinitionBounds(min, max);
        }

        #endregion

        #region UI handlers

        private void AddSkinToScene_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.DataContext is VfxSkinItem skin)
                AddSkinToScene(skin);
        }

        private void OpenSkinInNewTab_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.DataContext is VfxSkinItem skin)
                OpenSkin(skin, ownTab: true);
        }

        private void SceneActorsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if ((sender as ListBox)?.SelectedItem is VfxSceneActor actor &&
                !ReferenceEquals(actor, FocusedActor))
            {
                FocusSceneActor(actor);
            }
        }

        private void RemoveSceneActor_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.DataContext is VfxSceneActor actor)
                RemoveSceneActor(actor);
        }

        #endregion
    }
}
