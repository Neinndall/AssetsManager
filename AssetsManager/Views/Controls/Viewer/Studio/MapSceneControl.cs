using AssetsManager.Services.Viewer.Resources;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Utils.Viewport;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioControl
    {
        private async Task LoadDetectedMapAsync(
            MapSceneSource source,
            bool asCharacterBackdrop = false,
            MapVisibilityState initialVisibility = null)
        {
            if (source == null || MapViewerSceneService == null || _isCleanedUp)
                return;

            _mapCancellation?.Cancel();
            _mapCancellation?.Dispose();
            ClearPendingMapTextureUpdates();
            var operation = new System.Threading.CancellationTokenSource();
            _mapCancellation = operation;
            MapSceneRuntime staged = null;
            Task<IReadOnlyList<MapCharacterRuntimeGroup>> characterTask = null;
            Task<MapParticleSceneRuntime> particleTask = null;
            bool characterAssetsAdopted = false;
            bool particleAssetsAdopted = false;

            try
            {
                // Match current LTK MAIN's backdrop flow: publish decoded geometry/material state first.
                // Structure skins, placed VFX and texture waves must not hold the first visible frame.
                staged = await MapViewerSceneService.LoadBackdropAsync(source, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (staged == null)
                {
                    _model.StatusText = $"Unable to load {MapSourceDisplayName(source)}.";
                    return;
                }
                if (_isCleanedUp || !ReferenceEquals(_mapCancellation, operation))
                    return;

                if (asCharacterBackdrop && initialVisibility != null)
                    staged.SetVisibility(initialVisibility);

                _mapClipCancellation?.Cancel();
                ClearMapCharacterClipPreview();
                MapSceneRuntime previous = _mapSceneRuntime;
                _mapSceneRuntime = staged;
                _mapSceneIsCharacterBackdrop = asCharacterBackdrop;
                SyncSelectedMapVariant(source);
                _mapSkyCube = null;
                _skyCubeDirty = true;
                staged = null;
                MapSceneRuntime backdrop = _mapSceneRuntime;
                MapSceneData scene = backdrop.Scene;
                backdrop.ShowStructures = _model.MapStructuresVisible;
                backdrop.ShowParticles = _model.MapParticlesVisible;
                _model.HasMapPreview = true;
                PublishMapVisibilityControls(backdrop);
                _mapGpuSceneDirty = true;
                _mapTexturesDirty = false;
                previous?.Dispose();
                if (!asCharacterBackdrop)
                {
                    ReplaceMapBrowserRoot(MapBrowserSemantics.Build(backdrop));
                    if (_model.SelectedWorkspaceTab?.CameraState != null)
                        RestoreWorkspaceCamera(_model.SelectedWorkspaceTab);
                    else
                        SnapMapCamera(scene);
                }
                if (asCharacterBackdrop)
                    ApplyCharacterBackdropOrigin(scene, source);
                _model.StatusText = asCharacterBackdrop
                    ? $"Loaded {MapSourceDisplayName(source)} behind the active Character. Loading backdrop resources..."
                    : $"Loaded {MapSourceDisplayName(source)} backdrop. Loading scene resources...";
                StudioViewportView.OpenTkControl?.InvalidateVisual();
                SyncMapPreviewControls();

                // Every secondary MAP resource is independent. One malformed texture, optional sky,
                // Character skin or VFX asset must never cancel the geometry/material scene nor the
                // other resource waves. Cancellation still propagates when this MAP selection is replaced.
                Task<CubeMapData> skyTask = LoadMapResourceSafelyAsync(
                    MapViewerSceneService.LoadBackdropSkyAsync(source.ProjectRoot, operation.Token),
                    fallback: null,
                    "sky",
                    operation.Token);
                Task<IReadOnlyDictionary<string, MapTextureImage>> previewTask = LoadMapResourceSafelyAsync(
                    MapViewerSceneService.LoadPreviewTexturesAsync(
                        backdrop,
                        operation.Token,
                        (key, image) => QueueMapTextureUpdate(backdrop, MapTextureUpdateKind.Base, key, image)),
                    new Dictionary<string, MapTextureImage>(StringComparer.Ordinal),
                    "base preview textures",
                    operation.Token);
                Task<IReadOnlyDictionary<string, MapTextureImage>> previewProgramTask = LoadMapResourceSafelyAsync(
                    MapViewerSceneService.LoadPreviewProgramTexturesAsync(
                        backdrop,
                        operation.Token,
                        (key, image) => QueueMapTextureUpdate(backdrop, MapTextureUpdateKind.Program, key, image)),
                    new Dictionary<string, MapTextureImage>(StringComparer.Ordinal),
                    "program preview textures",
                    operation.Token);
                Task<IReadOnlyDictionary<string, MapTextureImage>> previewLightmapTask = LoadMapResourceSafelyAsync(
                    MapViewerSceneService.LoadPreviewLightmapsAsync(
                        backdrop,
                        operation.Token,
                        (key, image) => QueueMapTextureUpdate(backdrop, MapTextureUpdateKind.Lightmap, key, image)),
                    new Dictionary<string, MapTextureImage>(StringComparer.OrdinalIgnoreCase),
                    "preview lightmaps",
                    operation.Token);
                // Placeables prepared here belong to this state; a map-state switch that lands first owns the runtime.
                MapVisibilityState placeableVisibility = backdrop.Visibility;
                characterTask = LoadMapResourceSafelyAsync(
                    MapViewerSceneService.LoadCharacterAssetsAsync(backdrop, placeableVisibility, operation.Token),
                    (IReadOnlyList<MapCharacterRuntimeGroup>)Array.Empty<MapCharacterRuntimeGroup>(),
                    "structures",
                    operation.Token);
                particleTask = LoadMapResourceSafelyAsync(
                    MapViewerSceneService.LoadParticleAssetsAsync(backdrop, placeableVisibility, operation.Token),
                    new MapParticleSceneRuntime(Array.Empty<MapParticleRuntime>()),
                    "VFX placements",
                    operation.Token);

                Task previewWaveTask = Task.WhenAll(previewTask, previewProgramTask, previewLightmapTask);
                bool previewFinalized = false;
                bool skyFinalized = false;
                Task<IReadOnlyDictionary<string, MapTextureImage>> fullTextureTask = null;
                Task<IReadOnlyDictionary<string, MapTextureImage>> fullProgramTask = null;
                Task<IReadOnlyDictionary<string, MapTextureImage>> fullLightmapTask = null;

                // LTK lets independent scene resources join as they land. Do not hold Characters or
                // placed VFX behind the complete preview-texture wave; texture callbacks already make
                // the backdrop progressively visible while these tasks finish in parallel.
                while (!previewFinalized || !characterAssetsAdopted || !particleAssetsAdopted || !skyFinalized)
                {
                    var pending = new List<Task>(4);
                    if (!previewFinalized) pending.Add(previewWaveTask);
                    if (!characterAssetsAdopted) pending.Add(characterTask);
                    if (!particleAssetsAdopted) pending.Add(particleTask);
                    if (!skyFinalized) pending.Add(skyTask);
                    Task completed = await Task.WhenAny(pending);

                    if (!skyFinalized && ReferenceEquals(completed, skyTask))
                    {
                        CubeMapData sky = await skyTask;
                        operation.Token.ThrowIfCancellationRequested();
                        if (_isCleanedUp || !ReferenceEquals(_mapCancellation, operation) ||
                            !ReferenceEquals(_mapSceneRuntime, backdrop))
                        {
                            return;
                        }

                        _mapSkyCube = sky;
                        _skyCubeDirty = true;
                        skyFinalized = true;
                        StudioViewportView.OpenTkControl?.InvalidateVisual();
                    }

                    if (!previewFinalized && ReferenceEquals(completed, previewWaveTask))
                    {
                        await previewWaveTask;
                        operation.Token.ThrowIfCancellationRequested();
                        if (_isCleanedUp || !ReferenceEquals(_mapCancellation, operation) ||
                            !ReferenceEquals(_mapSceneRuntime, backdrop))
                        {
                            return;
                        }

                        backdrop.SetBackdropTextures(await previewTask);
                        backdrop.SetBackdropProgramTextures(await previewProgramTask);
                        backdrop.SetBackdropLightmaps(await previewLightmapTask);
                        _mapTexturesDirty = true;
                        StudioViewportView.OpenTkControl?.InvalidateVisual();
                        previewFinalized = true;

                        // Like LTK, only start the sharpening wave once every preview request has
                        // settled. Each full texture still publishes independently as it arrives.
                        fullTextureTask = LoadMapResourceSafelyAsync(
                            MapViewerSceneService.LoadFullTexturesAsync(
                                backdrop,
                                operation.Token,
                                (key, image) => QueueMapTextureUpdate(backdrop, MapTextureUpdateKind.Base, key, image)),
                            new Dictionary<string, MapTextureImage>(StringComparer.Ordinal),
                            "full base textures",
                            operation.Token);
                        fullProgramTask = LoadMapResourceSafelyAsync(
                            MapViewerSceneService.LoadFullProgramTexturesAsync(
                                backdrop,
                                operation.Token,
                                (key, image) => QueueMapTextureUpdate(backdrop, MapTextureUpdateKind.Program, key, image)),
                            new Dictionary<string, MapTextureImage>(StringComparer.Ordinal),
                            "full program textures",
                            operation.Token);
                        fullLightmapTask = LoadMapResourceSafelyAsync(
                            MapViewerSceneService.LoadFullLightmapsAsync(
                                backdrop,
                                operation.Token,
                                (key, image) => QueueMapTextureUpdate(backdrop, MapTextureUpdateKind.Lightmap, key, image)),
                            new Dictionary<string, MapTextureImage>(StringComparer.OrdinalIgnoreCase),
                            "full lightmaps",
                            operation.Token);
                    }

                    if (!characterAssetsAdopted && ReferenceEquals(completed, characterTask))
                    {
                        IReadOnlyList<MapCharacterRuntimeGroup> characters = await characterTask;
                        operation.Token.ThrowIfCancellationRequested();
                        if (_isCleanedUp || !ReferenceEquals(_mapCancellation, operation) ||
                            !ReferenceEquals(_mapSceneRuntime, backdrop))
                        {
                            DisposeCharacterGroups(characters);
                            characterAssetsAdopted = true;
                            return;
                        }
                        if (!placeableVisibility.Equals(backdrop.Visibility))
                        {
                            DisposeCharacterGroups(characters);
                            characterAssetsAdopted = true;
                            continue;
                        }

                        backdrop.SetCharacterGroups(characters);
                        characterAssetsAdopted = true;
                        bool characterBackdropNow = _mapSceneIsCharacterBackdrop;
                        if (!characterBackdropNow)
                            ReplaceMapBrowserRoot(MapBrowserSemantics.Build(backdrop));
                        _model.StatusText = characterBackdropNow
                            ? $"Character backdrop loaded {backdrop.CharacterGroups.Count} authored MAP structures. Loading remaining resources..."
                            : $"Loaded {backdrop.CharacterGroups.Count} MAP character skins. Loading remaining scene resources...";
                        StudioViewportView.OpenTkControl?.InvalidateVisual();
                    }

                    if (!particleAssetsAdopted && ReferenceEquals(completed, particleTask))
                    {
                        MapParticleSceneRuntime particles = await particleTask;
                        operation.Token.ThrowIfCancellationRequested();
                        if (_isCleanedUp || !ReferenceEquals(_mapCancellation, operation) ||
                            !ReferenceEquals(_mapSceneRuntime, backdrop))
                        {
                            particles?.Dispose();
                            particleAssetsAdopted = true;
                            return;
                        }
                        if (!placeableVisibility.Equals(backdrop.Visibility))
                        {
                            particles?.Dispose();
                            particleAssetsAdopted = true;
                            continue;
                        }

                        backdrop.SetParticles(particles);
                        particleAssetsAdopted = true;
                        bool characterBackdropNow = _mapSceneIsCharacterBackdrop;
                        if (!characterBackdropNow)
                            ReplaceMapBrowserRoot(MapBrowserSemantics.Build(backdrop));
                        _model.StatusText = characterBackdropNow
                            ? $"Character backdrop loaded {backdrop.Particles.Runtimes.Count} authored MAP VFX placements. Loading remaining resources..."
                            : $"Loaded {backdrop.Particles.Runtimes.Count} MAP VFX placements. Loading remaining scene resources...";
                        StudioViewportView.OpenTkControl?.InvalidateVisual();
                    }
                }

                if (_isCleanedUp || !ReferenceEquals(_mapCancellation, operation) ||
                    !ReferenceEquals(_mapSceneRuntime, backdrop))
                {
                    return;
                }

                _model.StatusText = _mapSceneIsCharacterBackdrop
                    ? $"Character backdrop {scene.Source.Map.Value} ready."
                    : $"Loaded map {scene.Source.Map.Value}.";
                _model.LogMessages.Add(
                    $"[MAP] Loaded {scene.Geometry.Meshes.Count} backdrop meshes, " +
                    $"{scene.Characters.Count} characters and {scene.Particles.Count} particle placeables.");
                StudioViewportView.OpenTkControl?.InvalidateVisual();

                if (fullTextureTask != null && fullProgramTask != null && fullLightmapTask != null)
                {
                    await Task.WhenAll(fullTextureTask, fullProgramTask, fullLightmapTask);
                    operation.Token.ThrowIfCancellationRequested();
                    if (!_isCleanedUp && ReferenceEquals(_mapCancellation, operation) &&
                        ReferenceEquals(_mapSceneRuntime, backdrop))
                    {
                        backdrop.SetBackdropTextures(await fullTextureTask);
                        backdrop.SetBackdropProgramTextures(await fullProgramTask);
                        backdrop.SetBackdropLightmaps(await fullLightmapTask);
                        _mapTexturesDirty = true;
                        StudioViewportView.OpenTkControl?.InvalidateVisual();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                operation.Cancel();
                LogService?.LogError(ex, "Failed to load selected MAP geometry in 3D Studio.");
                _model.StatusText = "Unable to load the selected map scene.";
                _model.LogMessages.Add($"[MAP ERROR] {ex.Message}");
            }
            finally
            {
                staged?.Dispose();
                if (!characterAssetsAdopted && characterTask != null)
                {
                    try
                    {
                        IReadOnlyList<MapCharacterRuntimeGroup> characters = await characterTask;
                        DisposeCharacterGroups(characters);
                    }
                    catch (OperationCanceledException) { }
                    catch (ObjectDisposedException) { }
                }
                if (!particleAssetsAdopted && particleTask != null)
                {
                    try
                    {
                        MapParticleSceneRuntime particles = await particleTask;
                        particles?.Dispose();
                    }
                    catch (OperationCanceledException) { }
                    catch (ObjectDisposedException) { }
                }
                if (ReferenceEquals(_mapCancellation, operation))
                    _mapCancellation = null;
                operation.Dispose();
            }
        }

        private async Task<T> LoadMapResourceSafelyAsync<T>(
            Task<T> task,
            T fallback,
            string label,
            System.Threading.CancellationToken cancellationToken)
        {
            try
            {
                return await task;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                string resource = string.IsNullOrWhiteSpace(label) ? "resource" : label;
                LogService?.LogError(ex, $"MAP {resource} load failed without cancelling the scene.");
                _model.LogMessages.Add($"[MAP WARNING] {resource}: {ex.Message}");
                return fallback;
            }
        }

        private static void DisposeCharacterGroups(IReadOnlyList<MapCharacterRuntimeGroup> groups)
        {
            if (groups == null) return;
            foreach (MapCharacterRuntimeGroup group in groups)
                group?.Dispose();
        }

        private void QueueMapTextureUpdate(
            MapSceneRuntime runtime,
            MapTextureUpdateKind kind,
            string key,
            MapTextureImage image)
        {
            if (runtime == null || string.IsNullOrWhiteSpace(key) || image == null || _isCleanedUp)
                return;

            bool schedule = false;
            lock (_mapTextureUpdateGate)
            {
                if (!ReferenceEquals(_pendingMapTextureRuntime, runtime))
                {
                    _pendingMapTextureRuntime = runtime;
                    _pendingMapTextureUpdates.Clear();
                    _pendingMapProgramTextureUpdates.Clear();
                    _pendingMapLightmapUpdates.Clear();
                }

                switch (kind)
                {
                    case MapTextureUpdateKind.Base:
                        _pendingMapTextureUpdates[key] = image;
                        break;
                    case MapTextureUpdateKind.Program:
                        _pendingMapProgramTextureUpdates[key] = image;
                        break;
                    case MapTextureUpdateKind.Lightmap:
                        _pendingMapLightmapUpdates[key] = image;
                        break;
                }

                if (!_mapTexturePublishQueued)
                {
                    _mapTexturePublishQueued = true;
                    schedule = true;
                }
            }

            if (schedule && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Render,
                    new Action(FlushPendingMapTextureUpdates));
            }
        }

        private void FlushPendingMapTextureUpdates()
        {
            MapSceneRuntime runtime;
            Dictionary<string, MapTextureImage> baseTextures;
            Dictionary<string, MapTextureImage> programTextures;
            Dictionary<string, MapTextureImage> lightmaps;

            lock (_mapTextureUpdateGate)
            {
                runtime = _pendingMapTextureRuntime;
                baseTextures = new Dictionary<string, MapTextureImage>(_pendingMapTextureUpdates, StringComparer.Ordinal);
                programTextures = new Dictionary<string, MapTextureImage>(_pendingMapProgramTextureUpdates, StringComparer.Ordinal);
                lightmaps = new Dictionary<string, MapTextureImage>(_pendingMapLightmapUpdates, StringComparer.OrdinalIgnoreCase);
                _pendingMapTextureUpdates.Clear();
                _pendingMapProgramTextureUpdates.Clear();
                _pendingMapLightmapUpdates.Clear();
                _mapTexturePublishQueued = false;
            }

            if (_isCleanedUp || runtime == null || !ReferenceEquals(runtime, _mapSceneRuntime))
                return;

            runtime.MergeBackdropTextures(baseTextures);
            runtime.MergeBackdropProgramTextures(programTextures);
            runtime.MergeBackdropLightmaps(lightmaps);
            if (baseTextures.Count == 0 && programTextures.Count == 0 && lightmaps.Count == 0)
                return;

            _mapTexturesDirty = true;
            StudioViewportView.OpenTkControl?.InvalidateVisual();
        }

        private void ClearPendingMapTextureUpdates()
        {
            lock (_mapTextureUpdateGate)
            {
                _pendingMapTextureRuntime = null;
                _pendingMapTextureUpdates.Clear();
                _pendingMapProgramTextureUpdates.Clear();
                _pendingMapLightmapUpdates.Clear();
                _mapTexturePublishQueued = false;
            }
        }

        private void CancelMapLoadAndClearScene()
        {
            _mapCancellation?.Cancel();
            _mapCancellation = null;
            _mapLayerCancellation?.Cancel();
            _mapLayerCancellation = null;
            _model.HasMapPreview = false;
            _model.ClearMapVisibility();
            ClearPendingMapTextureUpdates();
            _mapClipCancellation?.Cancel();
            ClearMapCharacterClipPreview();

            MapSceneRuntime previous = _mapSceneRuntime;
            bool wasCharacterBackdrop = _mapSceneIsCharacterBackdrop;
            _mapSceneRuntime = null;
            _mapSceneIsCharacterBackdrop = false;
            _mapSkyCube = null;
            _skyCubeDirty = true;
            if (!wasCharacterBackdrop)
                ReplaceMapBrowserRoot(null);
            _mapGpuSceneDirty = true;
            _mapTexturesDirty = false;
            previous?.Dispose();
        }

        private void ReplaceMapBrowserRoot(MapBrowserNode root)
        {
            _model.SelectedMapNode = null;
            if (_mapBrowserRoot != null)
                _model.BrowserRoots.Remove(_mapBrowserRoot);

            _mapBrowserRoot = root;
            if (root != null)
            {
                _model.BrowserRoots.Insert(0, root);
                RefreshMapBrowserVisibility(root);
            }
        }

        private void ApplyPendingSkyGpuState()
        {
            if (!_skyCubeDirty || _skyRenderer == null)
                return;

            _skyRenderer.SetCube(ActiveSkyCube);
            _skyCubeDirty = false;
        }

        /// <summary>The sky the viewport shows: the MAP's authored cube, else the generic environment.</summary>
        private CubeMapData ActiveSkyCube =>
            _mapSceneRuntime != null && _mapSkyCube?.IsValid == true
                ? _mapSkyCube
                : _genericSkyCube;

        private void ApplyPendingMapGpuState()
        {
            if (_mapGeometryRenderer == null)
                return;

            if (_mapGpuSceneDirty)
            {
                _mapCharacterRenderer?.Clear();
                _mapParticleRenderer?.Clear();
                if (_mapSceneRuntime != null)
                {
                    _mapGeometryRenderer.LoadScene(_mapSceneRuntime.Scene);
                    _mapGeometryRenderer.SetVisibility(_mapSceneRuntime.Visibility);
                    _mapGeometryRenderer.SetMapSkin(
                        MapVariantData.Draws(_model.SelectedMapVariant, _mapSceneRuntime.Scene.Source?.Map)
                            ? _model.SelectedMapVariant.Skin
                            : null);
                    _mapGeometryRenderer.SetPreviewSun(EffectiveMapSun(), _mapSunPreviewOverride);
                    // Backdrop loads publish geometry before texture waves. A preview wave may finish
                    // before the first GL frame, so LoadScene's immutable scene dictionaries can still
                    // be empty here. Rebind the runtime's latest texture state immediately instead of
                    // clearing _mapTexturesDirty and losing an already-completed preview wave.
                    _mapGeometryRenderer.UpdateTextures(_mapSceneRuntime.BackdropTextures);
                    _mapGeometryRenderer.UpdateProgramTextures(_mapSceneRuntime.BackdropProgramTextures);
                    _mapGeometryRenderer.UpdateLightmaps(_mapSceneRuntime.BackdropLightmaps);
                    EnsureMapPostEffectsRenderer();
                }
                else
                {
                    _mapGeometryRenderer.ClearScene();
                    _mapPostEffectsRenderer?.Dispose();
                    _mapPostEffectsRenderer = null;
                }
                _mapGpuSceneDirty = false;
                _mapVisibilityDirty = false;
                _mapLightingDirty = false;
                _mapTexturesDirty = false;
                return;
            }

            if (_mapVisibilityDirty && _mapSceneRuntime != null)
            {
                // Keep GPU resources of structures and VFX the new state still draws; kept emitters hold
                // handles into the particle caches, so those caches live until the scene is replaced.
                _mapCharacterRenderer?.Retain(_mapSceneRuntime.CharacterGroups.Select(group => group?.Asset));
                _mapGeometryRenderer.SetVisibility(_mapSceneRuntime.Visibility);
                _mapVisibilityDirty = false;
            }

            if (_mapLightingDirty && _mapSceneRuntime != null)
            {
                _mapGeometryRenderer.SetPreviewSun(EffectiveMapSun(), _mapSunPreviewOverride);
                EnsureMapPostEffectsRenderer();
                _mapLightingDirty = false;
            }

            if (_mapTexturesDirty && _mapSceneRuntime != null)
            {
                _mapGeometryRenderer.UpdateTextures(_mapSceneRuntime.BackdropTextures);
                _mapGeometryRenderer.UpdateProgramTextures(_mapSceneRuntime.BackdropProgramTextures);
                _mapGeometryRenderer.UpdateLightmaps(_mapSceneRuntime.BackdropLightmaps);
                _mapTexturesDirty = false;
            }
        }

        private MapSunData EffectiveMapSun() =>
            _mapSceneRuntime == null
                ? null
                : MapPreviewSemantics.EffectiveSun(_mapSceneRuntime.Scene.Sun, _mapSunPreviewOverride);

        private MapPostEffectsData EffectiveMapPostEffects() =>
            _mapSceneRuntime == null
                ? null
                : MapPreviewSemantics.EffectivePostEffects(
                    _mapSceneRuntime.Scene.PostEffects,
                    _mapPostEffectsOverride,
                    _hasMapPostEffectsOverride);

        private MapSsaoData EffectiveMapSsao() =>
            _mapSceneRuntime == null
                ? null
                : MapPreviewSemantics.EffectiveSsao(
                    _mapSceneRuntime.Scene.AmbientOcclusion,
                    _mapSsaoPreviewOverride);

        private void EnsureMapPostEffectsRenderer()
        {
            if (_mapSceneRuntime == null)
                return;

            bool needsPostEffects = MapPostEffectsRenderer.DrawsAnything(
                EffectiveMapPostEffects(),
                EffectiveMapSsao());
            if (needsPostEffects && _mapPostEffectsRenderer == null)
            {
                _mapPostEffectsRenderer = new MapPostEffectsRenderer();
                _mapPostEffectsRenderer.Initialize(_gl);
            }
            else if (!needsPostEffects && _mapPostEffectsRenderer != null)
            {
                _mapPostEffectsRenderer.Dispose();
                _mapPostEffectsRenderer = null;
            }
        }

        private void EnsureFxaaRenderer()
        {
            if (_fxaaRenderer == null && _gl != null)
            {
                _fxaaRenderer = new FxaaPostEffectsRenderer();
                _fxaaRenderer.Initialize(_gl);
            }
        }

        private void MarkMapLightingDirty()
        {
            _mapLightingDirty = true;
            StudioViewportView.OpenTkControl?.InvalidateVisual();
        }

        private void SnapMapCamera(MapSceneData scene)
        {
            if (scene?.Geometry == null || _cameraController == null)
                return;

            if (StableMapOrigin(scene) is not Vector3 engineOrigin)
                return;

            if (_dummyViewport.Camera is not PerspectiveCamera)
                _dummyViewport.Camera = _previewPerspectiveCamera;
            _previewPerspectiveCamera.FieldOfView = 45d;
            _cameraController.PerspectiveMinDistance = 10d;
            _cameraController.PerspectiveMaxDistance = 50000d;

            var target = new Point3D(-engineOrigin.X, engineOrigin.Y + 300f, engineOrigin.Z);
            var direction = new Vector3D(280d, 150d, 400d);
            direction.Normalize();
            double radius = Math.Sqrt(1500d * 1500d + 300d * 300d + 1500d * 1500d);
            double aspect = Math.Max(1d, StudioViewportView.OpenTkControl.ActualWidth) /
                            Math.Max(1d, StudioViewportView.OpenTkControl.ActualHeight);
            double distance = CameraPresets.CalculatePerspectiveFrameDistance(radius, 45d, aspect);
            Point3D position = target + direction * distance;
            _cameraController.SnapTo(position, target - position, VfxCameraUpDirection);
        }

        internal void MapLayerCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as CheckBox)?.DataContext is not MapVisibilityLayerOption layer)
                return;

            layer.IsEnabled = (sender as CheckBox)?.IsChecked == true;
            int flags = 0;
            foreach (MapVisibilityLayerOption option in _model.MapLayers)
            {
                if (option.IsEnabled)
                    flags |= 1 << option.Index;
            }
            _model.RequestMapLayerFlags(flags);
        }

        private void OnMapVisibilityRequested(MapVisibilityState state)
        {
            if (state != null)
                _ = ApplyMapVisibilityAsync(state);
        }

        private void PublishMapVisibilityControls(MapSceneRuntime runtime)
        {
            if (runtime?.Scene == null)
            {
                _model.ClearMapVisibility();
                return;
            }

            _model.SetMapVisibility(
                runtime.Scene.Visibility,
                MapGeometrySemantics.Layers(runtime.Scene.Geometry, runtime.Scene.Visibility),
                runtime.Visibility,
                runtime.Scene.Geometry);
        }

        internal void ResetOtherMapControllers_Click(object sender, RoutedEventArgs e) => _model.ResetOtherMapControllers();

        private static Vector3? StableMapOrigin(MapSceneData scene) =>
            scene?.Origin ??
            (scene?.Geometry == null
                ? null
                : MapGeometrySemantics.CalculateOriginForFlags(scene.Geometry, scene.OpeningVisibilityFlags));

        /// <summary>
        /// Switches the previewed map state. The latest request always owns the runtime: it cancels
        /// any pending switch first, even when it returns to the state already applied. Structures and
        /// placed VFX are reconciled: what the new state keeps is reused, only what it adds is loaded.
        /// </summary>
        private async Task ApplyMapVisibilityAsync(MapVisibilityState state)
        {
            const int MaximumPlanAttempts = 3;
            MapSceneRuntime runtime = _mapSceneRuntime;
            if (runtime == null || MapViewerSceneService == null || state == null || _isCleanedUp)
                return;

            _mapLayerCancellation?.Cancel();
            _mapLayerCancellation = null;
            if (state.Equals(runtime.Visibility))
            {
                _model.SyncMapVisibility(runtime.Visibility);
                return;
            }

            var operation = new System.Threading.CancellationTokenSource();
            _mapLayerCancellation = operation;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Task<MapCharacterPlan> characterTask = null;
            Task<MapParticlePlan> particleTask = null;
            bool IsOwner() =>
                !_isCleanedUp &&
                ReferenceEquals(_mapLayerCancellation, operation) &&
                ReferenceEquals(_mapSceneRuntime, runtime);

            try
            {
                _model.StatusText = $"Switching MAP state ({state})...";
                for (int attempt = 1; ; attempt++)
                {
                    characterTask = MapViewerSceneService.PlanCharacterAssetsAsync(runtime, state, operation.Token);
                    particleTask = MapViewerSceneService.PlanParticleAssetsAsync(runtime, state, operation.Token);
                    await Task.WhenAll(characterTask, particleTask);
                    operation.Token.ThrowIfCancellationRequested();
                    if (!IsOwner())
                        return;

                    // An initial placeable load may land while planning; re-plan against what the runtime holds now.
                    if (characterTask.Result.Generation == runtime.CharacterGeneration &&
                        particleTask.Result.Generation == runtime.ParticleGeneration)
                    {
                        break;
                    }
                    DisposePlans(characterTask, particleTask);
                    characterTask = null;
                    particleTask = null;
                    if (attempt >= MaximumPlanAttempts)
                        throw new InvalidOperationException("MAP placeables kept changing while switching state.");
                }

                MapCharacterPlan characterPlan = characterTask.Result;
                MapParticlePlan particlePlan = particleTask.Result;
                ClearMapCharacterClipPreview();
                if (!runtime.TryApplyCharacterPlan(characterPlan) || !runtime.TryApplyParticlePlan(particlePlan))
                    throw new InvalidOperationException("MAP state plan no longer matches the scene runtime.");
                runtime.SetVisibility(state);
                _model.SyncMapVisibility(state);
                if (_mapSceneIsCharacterBackdrop && _model.SelectedWorkspaceTab?.Kind == StudioWorkspaceTabKind.Skin)
                    _model.SelectedWorkspaceTab.CharacterBackdropVisibility = state;
                else
                    ReplaceMapBrowserRoot(MapBrowserSemantics.Build(runtime));
                _mapVisibilityDirty = true;

                stopwatch.Stop();
                string summary =
                    $"structures {characterPlan.ReusedCount} reused / {characterPlan.LoadedCount} loaded · " +
                    $"VFX {particlePlan.KeptCount} kept / {particlePlan.CreatedCount} new";
                _model.StatusText = $"MAP state {DescribeMapVisibility(state)} in {stopwatch.ElapsedMilliseconds} ms · {summary}.";
                _model.LogMessages.Add($"[MAP] State {state} applied in {stopwatch.ElapsedMilliseconds} ms · {summary}.");
                StudioViewportView.OpenTkControl?.InvalidateVisual();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception) when (!IsOwner())
            {
                // A superseded switch (or a closed scene) must not log or roll back the request that replaced it.
            }
            catch (Exception ex)
            {
                LogService?.LogError(ex, $"Failed to switch MAP visibility state ({state}).");
                _model.SyncMapVisibility(runtime.Visibility);
                _model.StatusText = "Unable to switch MAP state.";
            }
            finally
            {
                // Adopted plans ignore Dispose; unadopted ones release what they loaded or created.
                DisposePlans(characterTask, particleTask);
                if (ReferenceEquals(_mapLayerCancellation, operation))
                    _mapLayerCancellation = null;
                operation.Dispose();
            }
        }

        private static void DisposePlans(Task<MapCharacterPlan> characters, Task<MapParticlePlan> particles)
        {
            if (characters?.IsCompletedSuccessfully == true)
                characters.Result?.Dispose();
            if (particles?.IsCompletedSuccessfully == true)
                particles.Result?.Dispose();
        }

        private string DescribeMapVisibility(MapVisibilityState state)
        {
            var parts = new List<string>();
            if (_model.SelectedMapTransformation?.Label is string transformation)
                parts.Add(transformation);
            if (_model.HasMapSecondaryStates && _model.SelectedMapSecondaryState?.Label is string secondary)
                parts.Add(secondary);
            if (state.Mutators.Count > 0)
                parts.Add($"+{state.Mutators.Count} mutators");
            return parts.Count == 0 ? $"0x{state.Flags:x2}" : string.Join(" · ", parts);
        }

        internal void MapBrowserEye_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ToggleMapBrowserNodeVisibility(sender as FrameworkElement);
        }

        internal void MapBrowserEye_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            ToggleMapBrowserNodeVisibility(sender as FrameworkElement);
        }

        private void ToggleMapBrowserNodeVisibility(FrameworkElement element)
        {
            if (element?.DataContext is not MapBrowserNode node ||
                !node.CanHide ||
                _mapSceneRuntime == null)
            {
                return;
            }

            string id = MapBrowserVisibilityId(node);
            if (string.IsNullOrWhiteSpace(id))
                return;

            bool hidden = IsMapBrowserNodeHidden(node, _mapSceneRuntime.Hidden);
            _mapSceneRuntime.SetHidden(id, !hidden);
            RefreshMapBrowserVisibility(_mapBrowserRoot);
            StudioViewportView.OpenTkControl?.InvalidateVisual();
        }

        private void HandleMapBrowserSelection(MapBrowserNode node)
        {
            if (node == null)
                return;

            if (node.Kind == MapBrowserNodeKind.MapFile && node.Payload is MapSceneSource source)
            {
                if (!_isSwitchingWorkspaceTab)
                {
                    EnsureMapWorkspaceTab(node);
                    if (_model.SelectedSkin != null || _championModel != null)
                    {
                        ClearLoadedSkinState();
                        _model.SelectedSkin = null;
                    }
                }

                if (_mapSceneRuntime?.Scene?.Source?.Map?.Equals(source.Map) == true &&
                    string.Equals(
                        _mapSceneRuntime.Scene.Source.SelectedMapFilePath,
                        source.SelectedMapFilePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (_mapSceneIsCharacterBackdrop)
                    {
                        // The same runtime can become the full MAP workspace without decoding the map again.
                        // Character-backdrop mode intentionally omits the MAP browser root, so restore that owner
                        // state explicitly instead of returning early with a half-switched workspace.
                        _mapSceneIsCharacterBackdrop = false;
                        _mapSceneRuntime.ShowStructures = _model.MapStructuresVisible;
                        _mapSceneRuntime.ShowParticles = _model.MapParticlesVisible;
                        _model.HasMapPreview = true;
                        PublishMapVisibilityControls(_mapSceneRuntime);
                        ReplaceMapBrowserRoot(MapBrowserSemantics.Build(_mapSceneRuntime));
                        _mapGpuSceneDirty = true;
                    }
                    SnapMapCamera(_mapSceneRuntime.Scene);
                    StudioViewportView.OpenTkControl?.InvalidateVisual();
                    return;
                }

                _model.StatusText = $"Loading {MapSourceDisplayName(source)}...";
                _ = LoadDetectedMapAsync(source);
                return;
            }

            if (_mapSceneRuntime == null)
                return;

            string selectionSummary = !string.IsNullOrWhiteSpace(node.InspectorSummary)
                ? node.InspectorSummary
                : node.Subtitle;
            _model.StatusText = string.IsNullOrWhiteSpace(selectionSummary)
                ? node.Title
                : $"{node.Title} · {selectionSummary}";

            switch (node.Kind)
            {
                case MapBrowserNodeKind.Geometry:
                    _mapGpuSceneDirty = true;
                    SnapMapCamera(_mapSceneRuntime.Scene);
                    StudioViewportView.OpenTkControl?.InvalidateVisual();
                    break;
                case MapBrowserNodeKind.Chunk when node.Payload is MapOutlineChunkData chunk:
                    MapOutlineItemData firstChunkItem = chunk.Items?.FirstOrDefault(item => item?.IsDrawable == true)
                                                        ?? chunk.Items?.FirstOrDefault();
                    if (firstChunkItem != null)
                        FocusMapBrowserPosition(firstChunkItem.Position);
                    break;
                case MapBrowserNodeKind.Placeable when node.Payload is MapOutlineItemData item:
                    FocusMapBrowserPosition(item.Position);
                    break;
                case MapBrowserNodeKind.CharacterSkin when node.Payload is MapCharacterRuntimeGroup group:
                    if (_activeMapCharacterClip != null)
                        ClearMapCharacterClipPreview();
                    MapCharacterData firstPlacement = group.Placements?.FirstOrDefault();
                    _activeMapCharacterPlacement = firstPlacement;
                    if (firstPlacement != null)
                        FocusMapBrowserPosition(firstPlacement.Placeable.Position);
                    break;
                case MapBrowserNodeKind.CharacterPlacement when node.Payload is MapCharacterData character:
                    if (_activeMapCharacterClip != null)
                        ClearMapCharacterClipPreview();
                    _activeMapCharacterPlacement = character;
                    FocusMapBrowserPosition(character.Placeable.Position);
                    break;
                case MapBrowserNodeKind.Clip when node.Payload is MapCharacterClipSelection selection:
                    ConfigureMapAnimationParameterOptions(selection.Clip);
                    _ = PlayMapCharacterClipAsync(selection);
                    break;
                case MapBrowserNodeKind.ParticleSystem when node.Payload is MapParticleSystemGroupData particleSystem:
                    MapParticleData firstParticle = particleSystem.Particles?.FirstOrDefault(particle => particle?.StartDisabled == false)
                                                    ?? particleSystem.Particles?.FirstOrDefault();
                    if (firstParticle != null)
                        FocusMapBrowserPosition(firstParticle.Position);
                    break;
                case MapBrowserNodeKind.ParticlePlacement when node.Payload is MapParticleData particle:
                    FocusMapBrowserPosition(particle.Position);
                    break;
            }
        }
    }
}
