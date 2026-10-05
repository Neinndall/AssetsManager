using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class StudioControl
    {
        private void BindAnimationCatalog(string searchDir)
        {
            ClearAnimationClipCues();
            _model.SelectedAnimation = null;
            _model.DetectedAnimations.Clear();
            _animationSearchDirectory = searchDir;
            _isUpdatingAnimationParameter = true;
            try
            {
                _model.SetAnimationParameterOptions(Array.Empty<float>(), null);
            }
            finally
            {
                _isUpdatingAnimationParameter = false;
            }

            // Skin changes dispose the previous catalog; one adopted from a scene actor already owns
            // this bundle's decoded clips and is kept.
            _clipCatalog ??= new VfxClipCatalog();
            if (_activeBundle == null || VfxLoadingService == null)
                return;

            _model.DetectedAnimations.Add(AnimationClipCatalogItem.CreateBindPoseItem());
            foreach (AnimationClipCatalogItem item in BuildAnimationCatalog(null))
                _model.DetectedAnimations.Add(item);
            _model.LogMessages.Add($"[ANIMATIONS] Loaded {_model.DetectedAnimations.Count - 1} authored clips.");
        }

        private IReadOnlyList<AnimationClipCatalogItem> BuildAnimationCatalog(float? parameter)
        {
            if (_clipCatalog == null || _activeBundle == null || VfxLoadingService == null)
                return Array.Empty<AnimationClipCatalogItem>();

            return SynchronizationService.MergeClips(FocusedActor, _championModel, _clipCatalog.BuildMetadata(
                GetCharacterPlaybackBundle(),
                path => VfxLoadingService.ResolveAssetPath(path, _animationSearchDirectory, ".anm"),
                parameter), parameter);
        }

        private bool TrySelectOpeningSkinAnimation()
        {
            // Explicit child previews always win. This method only owns the unclaimed Skin scene.
            if (_model.SelectedSystem != null ||
                _model.SelectedAnimation != null ||
                _model.SelectedSpell != null)
            {
                return false;
            }

            AnimationClipCatalogItem opening = VfxClipCatalog.OpeningClip(_model.DetectedAnimations);
            if (opening == null)
            {
                opening = _model.DetectedAnimations.FirstOrDefault(item => item.IsBindPose);
                if (opening == null)
                    return false;
            }

            _model.IsAnimationMode = true;
            _model.SelectedAnimation = opening;
            return true;
        }

        private void ConfigureAnimationParameterOptions(AnimationClipCatalogItem item)
        {
            _isUpdatingAnimationParameter = true;
            try
            {
                IReadOnlyList<float> values = item?.Clip?.UsesEquippedGearParameter == true &&
                    _model.SelectedCharacterForm != null
                    ? Array.Empty<float>() : item?.ParameterValues ?? Array.Empty<float>();
                _model.SetAnimationParameterOptions(
                    values,
                    values.Count > 1 ? item?.ParameterValue : _model.AnimationParameter);
            }
            finally
            {
                _isUpdatingAnimationParameter = false;
            }
        }

        private void ConfigureMapAnimationParameterOptions(AnimationClipDefinition clip)
        {
            _isUpdatingAnimationParameter = true;
            try
            {
                IReadOnlyList<float> values = AnimationGraphPlayback.ParameterValues(clip);
                float? selected = values.Count > 1
                    ? AnimationGraphPlayback.NearestParameter(
                        values,
                        clip?.ParametricValues?.FirstOrDefault() ?? values[0])
                    : null;
                _model.SetAnimationParameterOptions(values, selected);
            }
            finally
            {
                _isUpdatingAnimationParameter = false;
            }
        }

        private void RebuildAnimationsForParameter(float parameter)
        {
            AnimationClipCatalogItem selected = _model.SelectedAnimation;
            if (selected?.Clip == null) return;

            uint selectedGraph = selected.Clip.GraphPathHash;
            uint selectedClip = selected.Clip.OwnerPathHash;
            IReadOnlyList<AnimationClipCatalogItem> rebuilt = BuildAnimationCatalog(parameter);
            if (rebuilt.Count == 0) return;

            // Rebuilding parameter choices is metadata-only; the chosen playlist is decoded
            // when the replacement selection starts playback.
            _model.DetectedAnimations.Clear();
            _model.DetectedAnimations.Add(AnimationClipCatalogItem.CreateBindPoseItem());
            foreach (AnimationClipCatalogItem item in rebuilt)
                _model.DetectedAnimations.Add(item);

            AnimationClipCatalogItem replacement = rebuilt.FirstOrDefault(item =>
                item.Clip?.GraphPathHash == selectedGraph &&
                item.Clip?.OwnerPathHash == selectedClip);
            if (replacement != null)
                _model.SelectedAnimation = replacement;
        }

        private string ResolveSklPath(string authoredPath, string sknPath, string searchDir)
        {
            if (!string.IsNullOrEmpty(authoredPath))
                return VfxLoadingService?.ResolveAssetPath(authoredPath, searchDir, ".skl");
            string sameName = Path.ChangeExtension(sknPath, ".skl");
            return File.Exists(sameName) ? sameName : null;
        }

        private async Task PlaySelectedAnimationAsync(AnimationClipCatalogItem selectedItem)
        {
            if (selectedItem == null || selectedItem.IsBindPose || _activeBundle == null || _clipCatalog == null) return;
            if (_championModel == null)
            {
                _model.StatusText = $"{selectedItem.DisplayName} · waiting for character model.";
                TryLoadChampionModelAsync(_model.RootPath);
                return;
            }

            bool startPaused = TakeStartPreviewPaused();
            double startTime = TakeStartPreviewTime();
            _animationClipCancellation?.Cancel();
            var operation = new System.Threading.CancellationTokenSource();
            _animationClipCancellation = operation;
            VfxClipCatalog catalog = _clipCatalog;
            VfxLoadingService.Bundle bundle = _activeBundle;
            VfxLoadingService.Bundle playbackBundle = GetCharacterPlaybackBundle();

            try
            {
                _model.StatusText = $"{selectedItem.DisplayName} · loading animation...";
                AnimationClipCatalogItem animItem = await SynchronizationService.PrepareClipAsync(
                    selectedItem, catalog, playbackBundle, VfxLoadingService,
                    _animationSearchDirectory, LogService, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (_isCleanedUp || animItem == null ||
                    !ReferenceEquals(operation, _animationClipCancellation) ||
                    !ReferenceEquals(catalog, _clipCatalog) ||
                    !ReferenceEquals(bundle, _activeBundle) ||
                    !SameAnimationClip(selectedItem, _model.SelectedAnimation) ||
                    _championModel == null)
                {
                    if (animItem == null && ReferenceEquals(selectedItem, _model.SelectedAnimation))
                        _model.StatusText = $"{selectedItem.DisplayName} · animation asset unavailable.";
                    return;
                }

                _pendingSpell = null;
                _activeSpellPlan = null;

                // Animation Clip playback can trigger several VFX systems over time, so the
                // standalone emitter audit from a previously selected System is not meaningful here.
                ClearCompositeDiagnostics();

                _championModel.CurrentAnimation = animItem.AnimationAsset;
                _championModel.AnimationTime = 0;
                _model.CurrentTime = 0;

                double dur = animItem.Duration > 0 ? animItem.Duration : 3.0;
                ResetPreviewLoopRange(dur);

                string searchDir = ResolvePreviewSearchDirectory();

                EnsureVfxRenderSession();
                ConfigureAnimationClipCues(animItem);
                ApplyCharacterPlacement();
                if (_vfxRenderer != null)
                {
                    _championAnimationService?.Update(0, animItem.AnimationAsset, _championModel.Skeleton,
                        _championModel.SkinnedMesh, _championModel.Parts, _championModel.Name, _championModel);
                    _vfxRenderer.SetBoneTransformSampler((time, name, hash) =>
                        _championAnimationService != null &&
                        _championAnimationService.TrySampleBoneTransform((float)time, name, hash, out var transform)
                            ? transform : null);
                    int seed = HashCode.Combine(animItem.Name, bundle.Systems.Count);
                    _vfxRenderer.SetAnimationSession(
                        animItem.Composition,
                        playbackBundle.IdleEffects,
                        bundle.Systems,
                        playbackBundle.ResourceMap,
                        searchDir,
                        seed,
                        dur,
                        playbackBundle.OwnerSceneContext);
                    _vfxRenderer.SetOwnerSkinningMatrices(_championAnimationService?.FinalBoneTransforms);
                    if (startTime > 0d)
                    {
                        _model.CurrentTime = VfxClipCueEvaluator.FoldedTime(startTime, dur);
                        _vfxRenderer.Seek(_model.CurrentTime);
                    }
                    if (!startPaused) _vfxRenderer.Play();
                }

                int resolvedIdleVfx = VfxAbilityCompositionBuilder.CountResolvedIdleEffects(
                    playbackBundle.IdleEffects,
                    bundle.Systems,
                    playbackBundle.ResourceMap);
                _model.IsPlaying = !startPaused;
                SynchronizeStudioCatalogs();
                _ = SynchronizeStudioPlaybackAsync();
                _model.StatusText = $"{animItem.DisplayName} ({dur:F2}s) · {(animItem.HasVfx ? animItem.VfxSummary : "Animation only")}";
                _model.LogMessages.Add($"[PLAY ANIMATION] {animItem.DisplayName} ({dur:F2}s) with {(animItem.Composition?.ResolvedCount ?? 0)} VFX events & {resolvedIdleVfx} resolved idle auras.");
                UpdateTimelineTrackMetrics();
                UpdatePlayheadPosition();
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) when (_isCleanedUp || !ReferenceEquals(catalog, _clipCatalog)) { }
            catch (Exception ex)
            {
                LogService?.LogError(ex, $"Failed to prepare AnimationGraph clip: {selectedItem.DisplayName}");
                if (ReferenceEquals(selectedItem, _model.SelectedAnimation))
                    _model.StatusText = $"{selectedItem.DisplayName} · animation load failed.";
            }
            finally
            {
                if (ReferenceEquals(_animationClipCancellation, operation))
                    _animationClipCancellation = null;
                operation.Dispose();
            }
        }

        private static bool SameAnimationClip(AnimationClipCatalogItem left, AnimationClipCatalogItem right)
            => left?.Clip != null && right?.Clip != null &&
               left.Clip.GraphPathHash == right.Clip.GraphPathHash &&
               left.Clip.OwnerPathHash == right.Clip.OwnerPathHash &&
               left.ParameterValue == right.ParameterValue;

        private void ClearCompositeDiagnostics()
        {
            _model.SelectedEmitter = null;
            _model.Emitters.Clear();
            _model.Textures.Clear();
            _model.HasAnySolo = false;
            _model.IsAllMuted = false;
        }

        private void CancelTimedPreviewPreparation()
        {
            _animationClipCancellation?.Cancel();
            // The async owner disposes its CTS in finally. Clearing the shared slot here makes
            // a stale continuation fail the ReferenceEquals guard without disposing its Token
            // while PrepareAsync may still be unwinding on that token.
            _animationClipCancellation = null;
        }

        private void ResetPreviewContextForSelection(bool preserveCharacterPose = false)
        {
            CancelTimedPreviewPreparation();
            _activeSpellPlan = null;
            _model.IsPlaying = false;
            _vfxRenderer?.Pause();
            _vfxRenderer?.SetSystem(null);
            ClearAnimationClipCues();
            ClearCompositeDiagnostics();
            _model.CurrentTime = 0d;

            _isUpdatingAnimationParameter = true;
            try
            {
                _model.SetAnimationParameterOptions(Array.Empty<float>(), null);
            }
            finally
            {
                _isUpdatingAnimationParameter = false;
            }

            // Clip selection keeps the last rendered pose until the replacement is ready.
            // A newly installed Character also keeps its opening pose until preparation finishes.
            if (!preserveCharacterPose && !_championAwaitingFirstPose)
                ResetChampionToBindPose();
        }

        private void BeginExclusivePreviewSelection(bool preserveCharacterPose = false)
        {
            if (_inspectedSystem != null)
                RememberStandaloneRun(_inspectedSystem);
            _pendingSystem = null;
            _inspectedSystem = null;
            _pendingSpell = null;
            ResetPreviewContextForSelection(preserveCharacterPose);
        }

        private string ResolvePreviewSearchDirectory()
        {
            string searchDir = _model.SelectedSkin?.ResourceRoot ?? _model.RootPath;
            return !string.IsNullOrEmpty(searchDir) && File.Exists(searchDir)
                ? Path.GetDirectoryName(searchDir) ?? searchDir
                : searchDir;
        }

        private bool IsActiveSpellSkin(StudioSpellBrowserItem spell)
        {
            if (spell?.Owner == null || _activeBundle == null) return false;
            return string.Equals(
                Path.GetFullPath(_activeBundle.PrimaryBinPath ?? string.Empty),
                Path.GetFullPath(spell.Owner.BinPath ?? string.Empty),
                StringComparison.OrdinalIgnoreCase);
        }

        private void ConfigureAnimationClipCues(AnimationClipCatalogItem clip)
        {
            ClearAnimationClipCues();
            if (clip == null || _championModel == null) return;

            _activeAnimationClip = clip;
            SetPlayingAnimation(clip);
            foreach (ModelPart part in _championModel.Parts)
                _animationBasePartVisibility[part] = part.IsVisible;
            foreach (uint hash in GetCharacterFormHiddenSubmeshes())
                _animationBaseHiddenSubmeshes.Add(hash);

            _animationVisibilityTimeline = VfxClipCueEvaluator.BuildVisibilityTimeline(
                clip.TimedCues,
                _animationBaseHiddenSubmeshes,
                _activeSpellPlan == null ? _championModel.Parts.Select(part => part.Name) : null);
            _championAnimationService?.SetPoseCues(clip.TimedCues,
                _activeBundle?.AnimationGraphs.FirstOrDefault(graph => graph.PathHash == clip.Clip.GraphPathHash)?.Masks);
            ApplyAnimationClipCues(0d);
        }

        private void ApplyAnimationClipCues(double time)
        {
            if (_activeAnimationClip == null || _championModel == null) return;

            // A spell's cast animation plays once and holds its last pose; a Clip preview loops.
            double folded = _activeSpellPlan?.Animation != null
                ? SpellAnimationTime(time, _activeAnimationClip.Duration)
                : VfxClipCueEvaluator.FoldedTime(time, _activeAnimationClip.Duration);
            IReadOnlySet<uint> hidden = VfxClipCueEvaluator.HiddenSubmeshesAt(
                _animationVisibilityTimeline,
                folded);
            ApplyOwnerSubmeshVisibility(hidden);
        }

        private void ClearAnimationClipCues()
        {
            if (_championModel != null)
            {
                foreach (var (part, visible) in _animationBasePartVisibility)
                {
                    if (_championModel.Parts.Contains(part) && part.IsVisible != visible)
                        part.IsVisible = visible;
                }
            }

            _activeAnimationClip = null;
            SetPlayingAnimation(null);
            _animationBasePartVisibility.Clear();
            _animationBaseHiddenSubmeshes.Clear();
            _animationVisibilityTimeline = Array.Empty<VfxClipCueEvaluator.VisibilityEntry>();
            _championAnimationService?.SetPoseCues(Array.Empty<AnimationClipTimedCue>());
            ApplyOwnerSubmeshVisibility(GetCharacterFormHiddenSubmeshes());
        }
    }
}
