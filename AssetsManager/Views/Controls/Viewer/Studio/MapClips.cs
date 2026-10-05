using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;
using AssetsManager.Services.Viewer.Vfx.Semantics;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioControl
    {
        private async Task PlayMapCharacterClipAsync(
            MapCharacterClipSelection selection,
            bool preservePlayhead = false)
        {
            if (selection?.Group?.Animation == null || selection.Clip == null || _mapSceneRuntime == null)
                return;

            _mapClipCancellation?.Cancel();
            _mapClipCancellation?.Dispose();
            var operation = new System.Threading.CancellationTokenSource();
            _mapClipCancellation = operation;
            MapSceneRuntime scene = _mapSceneRuntime;

            try
            {
                bool prepared = await selection.Group.Animation.PrepareClipAsync(
                    selection.Group.Asset,
                    selection.Clip,
                    _model.AnimationParameter,
                    operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (!prepared || _isCleanedUp ||
                    !ReferenceEquals(_mapClipCancellation, operation) ||
                    !ReferenceEquals(_mapSceneRuntime, scene))
                {
                    if (!prepared)
                        _model.StatusText = $"{selection.Clip.ClipName ?? "Animation Clip"} · animation asset unavailable.";
                    return;
                }

                MapCharacterData targetPlacement = ResolveMapCharacterPreviewPlacement(selection.Group);
                if (targetPlacement == null)
                    return;

                if (_activeMapCharacterGroup != null && !ReferenceEquals(_activeMapCharacterGroup, selection.Group))
                    _activeMapCharacterGroup.ClearPreviewClip();

                _activeMapCharacterGroup = selection.Group;
                _activeMapCharacterPlacement = targetPlacement;
                _activeMapCharacterClip = selection.Clip;
                selection.Group.SetPreviewClip(selection.Clip, targetPlacement);

                _pendingSystem = null;
                _pendingSpell = null;
                _activeSpellPlan = null;
                _model.SelectedSystem = null;
                _model.SelectedAnimation = null;
                _model.SelectedSpell = null;
                ClearAnimationClipCues();
                ClearCompositeDiagnostics();

                double duration = selection.Group.Animation.PreparedClipDuration(selection.Clip);
                if (!double.IsFinite(duration) || duration <= 0d)
                    duration = Math.Max(0.05d, Math.Max(0f, selection.Clip.EndFrame - selection.Clip.StartFrame) * selection.Clip.TickDuration);
                double resumeAt = preservePlayhead
                    ? Math.Clamp(_model.CurrentTime, 0d, duration)
                    : 0d;
                IEnumerable<uint> initiallyHidden =
                    (selection.Group.Asset?.Skin?.HiddenSubmeshes ?? Array.Empty<string>())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(Fnv1a.HashLower);
                _mapAnimationVisibilityTimeline = VfxClipCueEvaluator.BuildVisibilityTimeline(
                    selection.Group.Animation.PreparedClipCues(selection.Clip),
                    initiallyHidden,
                    selection.Group.Asset?.Mesh?.Ranges.Select(range => range.Name));
                _mapAnimationVisibilityDuration = duration;

                MapCharacterVfxCatalog catalog = selection.Group.Asset?.Vfx ?? MapCharacterVfxCatalog.Empty;
                IReadOnlyList<AnimationClipDefinition> playlist =
                    selection.Group.Animation.PreparedClipPlaylist(selection.Clip);
                VfxAbilityComposition composition = VfxAbilityCompositionBuilder.BuildTimedPlaylist(
                    selection.Clip,
                    playlist,
                    selection.Group.Animation.PreparedClipStepDurations(selection.Clip),
                    selection.Group.Animation.PreparedClipFrameSeconds(selection.Clip),
                    catalog.Systems,
                    catalog.ResourceMap);
                int resolvedIdleVfx = VfxAbilityCompositionBuilder.CountResolvedIdleEffects(
                    catalog.IdleEffects,
                    catalog.Systems,
                    catalog.ResourceMap);
                bool hasSceneVfx = composition.ResolvedCount > 0 || resolvedIdleVfx > 0;
                IReadOnlyDictionary<uint, VfxSystemDefinition> requiredSystems = hasSceneVfx
                    ? RequiredMapClipSystems(catalog, composition)
                    : new Dictionary<uint, VfxSystemDefinition>();

                bool vfxSessionReady = false;
                string vfxFallback = null;
                _vfxRenderer?.SetSystem(null);
                if (hasSceneVfx)
                {
                    VfxSceneResourceContext resources = null;
                    try
                    {
                        resources = await EnsureMapClipVfxResourcesAsync(
                            selection.Group,
                            scene,
                            requiredSystems,
                            operation.Token);
                        operation.Token.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        LogService?.LogWarning($"MAP character clip VFX resources unavailable: {ex.Message}");
                        vfxFallback = "VFX resources unavailable";
                    }

                    if (_isCleanedUp ||
                        !ReferenceEquals(_mapClipCancellation, operation) ||
                        !ReferenceEquals(_mapSceneRuntime, scene))
                    {
                        return;
                    }

                    if (resources != null)
                    {
                        EnsureVfxRenderSession();
                        if (_vfxRenderer != null)
                        {
                            _vfxRenderer.SetWorldTransform(
                                MapCharacterSemantics.VfxWorldTransform(targetPlacement.Transform));
                            int seed = unchecked((int)(selection.Clip.OwnerPathHash ^ targetPlacement.KeyHash));
                            vfxSessionReady = _vfxRenderer.SetAnimationSession(
                                composition,
                                catalog.IdleEffects,
                                catalog.Systems,
                                catalog.ResourceMap,
                                resources.SearchDirectory,
                                seed,
                                duration,
                                catalog.OwnerSceneContext);
                            if (vfxSessionReady)
                            {
                                _vfxRenderer.SetBoneTransformSampler((time, boneName, boneHash) =>
                                    selection.Group.Animation.TrySamplePreparedClipBoneTransform(
                                        selection.Group.Asset,
                                        selection.Clip,
                                        time,
                                        boneName,
                                        boneHash,
                                        out Matrix4x4 transform)
                                        ? transform
                                        : null);
                                _vfxRenderer.Seek(resumeAt);
                                _vfxRenderer.Play();
                            }
                            else
                            {
                                vfxFallback ??= "VFX session unavailable";
                            }
                        }
                    }
                    else
                    {
                        vfxFallback ??= "VFX resources unavailable";
                    }
                }

                if (!vfxSessionReady)
                {
                    _vfxRenderer?.SetSystem(null);
                    _vfxRenderer?.SetWorldTransform(Matrix4x4.Identity);
                    _vfxRenderer?.SetBoneTransformSampler(null);
                    _vfxRenderer?.UpdateBoneTransforms(null);
                    _vfxRenderer?.SetOwnerSkinningMatrices(null);
                }

                ResetPreviewLoopRange(duration);
                _model.CurrentTime = resumeAt;
                SyncMapCharacterClipTime(resumeAt);
                _model.IsPlaying = true;

                FocusMapBrowserPosition(targetPlacement.Placeable.Position);

                string name = string.IsNullOrWhiteSpace(selection.Clip.ClipName)
                    ? $"0x{selection.Clip.OwnerPathHash:x8}"
                    : selection.Clip.ClipName;
                string vfxSummary = vfxSessionReady
                    ? $" · {composition.ResolvedCount} VFX events"
                    : !string.IsNullOrWhiteSpace(vfxFallback)
                        ? $" · animation only ({vfxFallback})"
                        : string.Empty;
                _model.StatusText = $"{name} ({duration:F2}s) · MAP character clip{vfxSummary}.";
                _model.LogMessages.Add($"[MAP CLIP] {name} · {duration:F2}s{vfxSummary}.");
                UpdateTimelineTrackMetrics();
                UpdatePlayheadPosition();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogService?.LogError(ex, "Failed to prepare MAP character animation clip.");
                _model.StatusText = "Unable to play the selected MAP animation clip.";
            }
            finally
            {
                if (ReferenceEquals(_mapClipCancellation, operation))
                    _mapClipCancellation = null;
                operation.Dispose();
            }
        }

        private MapCharacterData ResolveMapCharacterPreviewPlacement(MapCharacterRuntimeGroup group)
        {
            if (group?.Placements == null || group.Placements.Count == 0)
                return null;

            if (_activeMapCharacterPlacement != null)
            {
                MapCharacterData held = group.Placements.FirstOrDefault(placement =>
                    ReferenceEquals(placement, _activeMapCharacterPlacement) ||
                    (placement != null &&
                     placement.ChunkHash == _activeMapCharacterPlacement.ChunkHash &&
                     placement.KeyHash == _activeMapCharacterPlacement.KeyHash));
                if (held != null)
                    return held;
            }

            return group.Placements[0];
        }

        private static IReadOnlyDictionary<uint, VfxSystemDefinition> RequiredMapClipSystems(
            MapCharacterVfxCatalog catalog,
            VfxAbilityComposition composition)
        {
            if (catalog?.Systems == null || catalog.Systems.Count == 0)
                return new Dictionary<uint, VfxSystemDefinition>();

            var roots = new List<VfxSystemDefinition>();
            foreach (VfxCompositionEvent cue in composition?.Events ?? Array.Empty<VfxCompositionEvent>())
                if (cue?.System != null)
                    roots.Add(cue.System);

            foreach (VfxIdleEffectDefinition idle in catalog.IdleEffects ?? Array.Empty<VfxIdleEffectDefinition>())
            {
                if (idle == null || idle.EffectKey == 0 ||
                    catalog.ResourceMap == null ||
                    !catalog.ResourceMap.TryGetValue(idle.EffectKey, out uint systemHash) ||
                    systemHash == 0 ||
                    !catalog.Systems.TryGetValue(systemHash, out VfxSystemDefinition system))
                {
                    continue;
                }
                roots.Add(system);
            }

            return VfxSceneResourceContext.ReachableSystems(
                catalog.Systems,
                catalog.ResourceMap,
                roots);
        }

        private Task<VfxSceneResourceContext> EnsureMapClipVfxResourcesAsync(
            MapCharacterRuntimeGroup group,
            MapSceneRuntime scene,
            IReadOnlyDictionary<uint, VfxSystemDefinition> requiredSystems,
            System.Threading.CancellationToken cancellationToken)
        {
            if (group == null || scene == null || MapViewerSceneService == null)
                return Task.FromResult<VfxSceneResourceContext>(null);

            MapCharacterVfxCatalog catalog = group.Asset?.Vfx ?? MapCharacterVfxCatalog.Empty;
            requiredSystems ??= new Dictionary<uint, VfxSystemDefinition>();
            string projectRoot = scene.Scene.Source?.ProjectRoot;
            var requiredCatalog = new MapCharacterVfxCatalog(
                requiredSystems,
                catalog.ResourceMap,
                Array.Empty<VfxIdleEffectDefinition>(),
                catalog.OwnerSceneContext);
            return group.EnsureVfxResourcesAsync(
                token => MapViewerSceneService.CreateVfxResourcesAsync(requiredCatalog, projectRoot, token),
                (resources, token) => resources.EnsureMaterializedAsync(
                    requiredSystems,
                    catalog.OwnerSceneContext,
                    projectRoot,
                    token),
                cancellationToken);
        }

        private void ClearMapCharacterClipPreview()
        {
            _mapClipCancellation?.Cancel();
            bool ownedVfxRenderer = CharacterViewportSemantics.MapCharacterClipOwnsVfxRenderer(
                _activeMapCharacterClip != null,
                _activeMapCharacterGroup?.PreviewClip != null);
            _activeMapCharacterGroup?.ClearPreviewClip();
            _activeMapCharacterGroup = null;
            _activeMapCharacterPlacement = null;
            _activeMapCharacterClip = null;
            _mapAnimationVisibilityTimeline = Array.Empty<VfxClipCueEvaluator.VisibilityEntry>();
            _mapAnimationVisibilityDuration = 0d;

            // MAP Character Clips share the scene VFX renderer with Skin/System/Spell previews. A MAP
            // backdrop load, layer change or teardown must not clear a Skin-owned session merely because
            // both flows use the same renderer instance.
            if (ownedVfxRenderer)
            {
                _vfxRenderer?.SetSystem(null);
                _vfxRenderer?.SetWorldTransform(Matrix4x4.Identity);
                _vfxRenderer?.UpdateBoneTransforms(null);
                _vfxRenderer?.SetOwnerSkinningMatrices(null);
            }
        }

        private void SyncMapCharacterClipTime(double timeSeconds)
        {
            if (_activeMapCharacterGroup == null || _activeMapCharacterClip == null)
                return;

            double safe = double.IsFinite(timeSeconds) ? Math.Max(0d, timeSeconds) : 0d;
            if (_model.TotalDuration > 0d && double.IsFinite(_model.TotalDuration))
                safe = Math.Min(safe, _model.TotalDuration);
            _activeMapCharacterGroup.PreviewTimeSeconds = (float)safe;
            _activeMapCharacterGroup.Animation.EvaluateClip(
                _activeMapCharacterGroup.Asset,
                _activeMapCharacterClip,
                (float)safe);

            double folded = VfxClipCueEvaluator.FoldedTime(safe, _mapAnimationVisibilityDuration);
            IReadOnlySet<uint> hidden = VfxClipCueEvaluator.HiddenSubmeshesAt(
                _mapAnimationVisibilityTimeline,
                folded);
            _activeMapCharacterGroup.SetPreviewHiddenSubmeshes(hidden);
            _vfxRenderer?.SetOwnerHiddenSubmeshes(hidden);
            UpdateMapCharacterClipVfxPose();
        }

        private void UpdateMapCharacterClipVfxPose()
        {
            if (_vfxRenderer?.ActiveSystem == null ||
                _activeMapCharacterGroup?.Animation == null ||
                _activeMapCharacterClip == null)
            {
                return;
            }

            _vfxRenderer.SetOwnerSkinningMatrices(
                _activeMapCharacterGroup.Animation.PreparedClipSkinningMatrices(_activeMapCharacterClip));
            _vfxRenderer.UpdateBoneTransforms((boneName, boneHash) =>
                _activeMapCharacterGroup.Animation.TryGetPreparedClipBoneTransform(
                    _activeMapCharacterClip,
                    boneName,
                    boneHash,
                    out Matrix4x4 transform)
                    ? transform
                    : null);
        }

        private bool HasSelectedMapClipReady() =>
            _model.SelectedMapNode?.Kind == MapBrowserNodeKind.Clip &&
            _model.SelectedMapNode.Payload is MapCharacterClipSelection selection &&
            ReferenceEquals(selection.Group, _activeMapCharacterGroup) &&
            ReferenceEquals(selection.Clip, _activeMapCharacterClip) &&
            ReferenceEquals(_activeMapCharacterGroup?.PreviewClip, _activeMapCharacterClip);

        private void AdvanceMapCharacterClip(float deltaSeconds)
        {
            if (!HasSelectedMapClipReady())
                return;

            if (_model.IsPlaying && !_isUserSeeking)
            {
                double next;
                if (_vfxRenderer?.ActiveSystem != null)
                {
                    _vfxRenderer.ActiveSystem.Speed = _model.Speed;
                    _vfxRenderer.Update(Math.Max(0f, deltaSeconds));
                    next = _vfxRenderer.PlaybackTime;
                }
                else
                {
                    next = _model.CurrentTime + Math.Max(0f, deltaSeconds) * _model.Speed;
                }

                if (ShouldRestartPreview(_model.IsPreviewLoopEnabled, next, _model.ActiveLoopDuration))
                {
                    next = ResolvePreviewLoopRestart(
                        _model.ActiveLoopStart,
                        _model.ActiveLoopDuration,
                        _model.TotalDuration);
                    _vfxRenderer?.Seek(next);
                    _vfxRenderer?.Play();
                }
                else if (next >= _model.TotalDuration)
                {
                    next = _model.TotalDuration;
                    _vfxRenderer?.Seek(next);
                    _vfxRenderer?.Pause();
                    _model.IsPlaying = false;
                }
                _model.CurrentTime = next;
            }

            SyncMapCharacterClipTime(_model.CurrentTime);
        }

        private void FocusMapBrowserPosition(Vector3 enginePosition)
        {
            if (_cameraController == null)
                return;

            if (_dummyViewport.Camera is not PerspectiveCamera camera)
            {
                _dummyViewport.Camera = _previewPerspectiveCamera;
                camera = _previewPerspectiveCamera;
            }

            var pose = CameraNavigation.FocusPose(enginePosition, camera.LookDirection);
            if (pose == null)
                return;

            _cameraController.FlyTo(
                pose.Value.Position,
                pose.Value.LookDirection,
                camera.UpDirection);
        }

        private static string MapBrowserVisibilityId(MapBrowserNode node) =>
            node?.Payload switch
            {
                MapOutlineChunkData chunk => chunk.Id,
                MapOutlineItemData item => item.Id,
                MapCharacterData character => MapOutlineSemantics.ItemId(character.ChunkHash, character.KeyHash),
                MapParticleData particle => MapOutlineSemantics.ItemId(particle.ChunkHash, particle.KeyHash),
                _ => null
            };

        private static bool IsMapBrowserNodeHidden(MapBrowserNode node, IReadOnlySet<string> hidden) =>
            node?.Payload switch
            {
                MapOutlineChunkData chunk => hidden?.Contains(chunk.Id) == true,
                MapOutlineItemData item => MapOutlineSemantics.IsHidden(hidden, item.ChunkHash, item.KeyHash),
                MapCharacterData character => MapOutlineSemantics.IsHidden(hidden, character.ChunkHash, character.KeyHash),
                MapParticleData particle => MapOutlineSemantics.IsHidden(hidden, particle.ChunkHash, particle.KeyHash),
                _ => false
            };

        private void RefreshMapBrowserVisibility(MapBrowserNode node)
        {
            if (node == null || _mapSceneRuntime == null)
                return;

            if (node.CanHide)
                node.IsHidden = IsMapBrowserNodeHidden(node, _mapSceneRuntime.Hidden);

            foreach (object child in node.Children)
            {
                if (child is MapBrowserNode mapChild)
                    RefreshMapBrowserVisibility(mapChild);
            }
        }
    }
}
