using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;

namespace AssetsManager.Views.Controls.Viewer
{
    public partial class VfxInspectorControl
    {
        private void RequestSpellPreview(VfxSpellBrowserItem spell)
        {
            if (spell == null) return;
            _pendingSpell = spell;

            if (!ReferenceEquals(_model.SelectedSkin, spell.Owner))
            {
                _model.SelectedSkin = spell.Owner;
                return;
            }

            if (!IsActiveSpellSkin(spell)) return;

            if (_championModel == null || !ReferenceEquals(_championBundle, _activeBundle))
            {
                TryLoadChampionModelAsync(_model.RootPath);
                return;
            }

            _ = PlaySelectedSpellAsync(spell);
        }

        private void TryPlayPendingSpell()
        {
            VfxSpellBrowserItem spell = _pendingSpell;
            if (spell == null || !ReferenceEquals(_model.SelectedSpell, spell)) return;
            if (!ReferenceEquals(_model.SelectedSkin, spell.Owner) ||
                !IsActiveSpellSkin(spell) ||
                _championModel == null ||
                !ReferenceEquals(_championBundle, _activeBundle))
            {
                return;
            }

            _ = PlaySelectedSpellAsync(spell);
        }

        private async Task PlaySelectedSpellAsync(VfxSpellBrowserItem spell)
        {
            if (spell == null || _activeBundle == null || _championModel == null) return;
            _pendingSpell = null;
            bool startPaused = TakeStartPreviewPaused();

            if (spell.Availability != VfxSpellAvailability.Supported)
            {
                _activeSpellPlan = null;
                _model.IsPlaying = false;
                _vfxRenderer?.Pause();
                _model.StatusText = $"{spell.Name} · {spell.AvailabilityText}.";
                return;
            }

            _animationClipCancellation?.Cancel();
            var operation = new System.Threading.CancellationTokenSource();
            _animationClipCancellation = operation;
            VfxClipCatalog catalog = _clipCatalog;
            VfxLoadingService.Bundle bundle = _activeBundle;
            VfxLoadingService.Bundle playbackBundle = GetCharacterPlaybackBundle();

            try
            {
                AnimationClipCatalogItem[] clips = _model.DetectedAnimations.ToArray();
                AnimationClipCatalogItem requestedAnimation =
                    VfxSpellPreviewComposer.ResolveAnimation(spell.Preview?.AnimationName, clips);
                if (requestedAnimation != null)
                {
                    if (catalog == null)
                        return;
                    _model.StatusText = $"{spell.Name} · loading cast animation...";
                    AnimationClipCatalogItem prepared = await catalog.PrepareAsync(
                        requestedAnimation,
                        playbackBundle,
                        path => VfxLoadingService.ResolveAssetPath(path, _animationSearchDirectory, ".anm"),
                        LogService,
                        operation.Token);
                    operation.Token.ThrowIfCancellationRequested();
                    if (prepared == null)
                    {
                        _activeSpellPlan = null;
                        _model.IsPlaying = false;
                        _model.StatusText = $"{spell.Name} · cast animation unavailable.";
                        return;
                    }

                    for (int index = 0; index < clips.Length; index++)
                    {
                        if (SameAnimationClip(clips[index], requestedAnimation))
                        {
                            clips[index] = prepared;
                            break;
                        }
                    }
                }

                if (_isCleanedUp || operation.IsCancellationRequested ||
                    !ReferenceEquals(operation, _animationClipCancellation) ||
                    !ReferenceEquals(bundle, _activeBundle) ||
                    !ReferenceEquals(spell, _model.SelectedSpell) ||
                    _championModel == null)
                {
                    return;
                }

                EnsureVfxRenderSession();
                Func<uint, string> resolveSystemPath = VfxLoadingService == null
                    ? null
                    : hash => VfxLoadingService.ResolveBinEntryPath(hash);
                VfxSpellPreviewPlan plan = VfxSpellPreviewComposer.Build(
                    spell,
                    playbackBundle,
                    clips,
                    ResolveSpellLaunchFrame,
                    resolveSystemPath);
                _activeSpellPlan = plan;
                if (plan.Availability != VfxSpellAvailability.Supported)
                {
                    _model.IsPlaying = false;
                    _vfxRenderer?.Pause();
                    _model.StatusText = $"{spell.Name} · {plan.Status}";
                    return;
                }

                ClearCompositeDiagnostics();
                _model.CurrentTime = 0d;

                // The cast animation carries its own submesh, joint and particle cues, as in the Clips preview.
                AnimationClipCatalogItem animation = plan.Animation;
                ConfigureAnimationClipCues(animation);
                _championModel.CurrentAnimation = animation?.AnimationAsset;
                _championModel.AnimationTime = 0d;
                if (animation?.AnimationAsset != null &&
                    _championAnimationService != null &&
                    _championModel.Skeleton != null)
                {
                    _championAnimationService.Update(
                        0f,
                        animation.AnimationAsset,
                        _championModel.Skeleton,
                        _championModel.SkinnedMesh,
                        _championModel.Parts,
                        _championModel.Name, _championModel);
                    _championModel.SkinningMatrices = _championAnimationService.FinalBoneTransforms;
                    _championModel.GpuSkinningData = _championAnimationService.SkinningData;
                }
                else
                {
                    _championModel.SkinningMatrices = null;
                }

                string searchDir = ResolvePreviewSearchDirectory();
                ApplyCharacterPlacement();

                bool ready = _vfxRenderer?.SetSpellSession(
                    plan.Steps,
                    bundle.Systems,
                    bundle.ResourceMap,
                    searchDir,
                    animation?.Duration ?? 0d,
                    playbackBundle.OwnerSceneContext,
                    animation?.Composition,
                    playbackBundle.IdleEffects) == true;
                if (_vfxRenderer != null)
                {
                    if (animation?.AnimationAsset != null && _championAnimationService != null)
                    {
                        float castDuration = animation.Duration;
                        _vfxRenderer.SetBoneTransformSampler((time, name, hash) =>
                            _championAnimationService.TrySampleBoneTransform(
                                SpellAnimationTime(time, castDuration), name, hash, out var transform)
                                ? transform : null);
                    }
                    _vfxRenderer.SetOwnerSkinningMatrices(_championModel.SkinningMatrices);
                    if (ready && !startPaused) _vfxRenderer.Play();
                }

                double duration = _vfxRenderer?.RigDuration ?? Math.Max(
                    animation?.Duration ?? 0d,
                    plan.Arrival + VfxSpellPreviewComposer.ImpactDuration);
                ResetPreviewLoopRange(duration);
                _model.IsPlaying = ready && !startPaused;
                _model.StatusText = $"{spell.Name} · {plan.Status} · release {plan.Release:F2}s / arrival {plan.Arrival:F2}s";
                _model.LogMessages.Add($"[PLAY SPELL] {spell.ObjectPath} · {plan.Status}.");
                UpdateTimelineTrackMetrics();
                UpdatePlayheadPosition();
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) when (_isCleanedUp || !ReferenceEquals(catalog, _clipCatalog)) { }
            catch (Exception ex)
            {
                LogService?.LogError(ex, $"Failed to prepare spell preview: {spell.Name}");
                if (ReferenceEquals(spell, _model.SelectedSpell))
                    _model.StatusText = $"{spell.Name} · preview load failed.";
            }
            finally
            {
                if (ReferenceEquals(_animationClipCancellation, operation))
                    _animationClipCancellation = null;
                operation.Dispose();
            }
        }

        private (Vector3 Origin, Vector3 Forward)? ResolveSpellLaunchFrame(
            AnimationClipCatalogItem animation,
            double time,
            string boneName)
        {
            if (_championModel?.Skeleton == null) return null;

            Matrix4x4 boneTransform = Matrix4x4.Identity;
            Matrix4x4 rootTransform = Matrix4x4.Identity;
            bool hasRootTransform;
            bool hasLaunchBone = !string.IsNullOrWhiteSpace(boneName);

            if (animation?.AnimationAsset != null && _championAnimationService != null)
            {
                _championAnimationService.Update(
                    0f,
                    animation.AnimationAsset,
                    _championModel.Skeleton,
                    _championModel.SkinnedMesh,
                    _championModel.Parts,
                    _championModel.Name, _championModel);
                float sampleTime = SpellAnimationTime(time, animation.Duration);
                if (hasLaunchBone &&
                    !_championAnimationService.TrySampleBoneTransform(
                        sampleTime,
                        boneName,
                        Fnv1a.HashLower(boneName),
                        out boneTransform))
                {
                    return null;
                }

                hasRootTransform = _championAnimationService.TrySampleRootTransform(
                    sampleTime,
                    out rootTransform);
            }
            else
            {
                if (hasLaunchBone &&
                    !AnimationService.TryGetBindBoneTransform(
                        _championModel.Skeleton,
                        boneName,
                        out boneTransform))
                {
                    return null;
                }

                hasRootTransform = AnimationService.TryGetBindRootTransform(
                    _championModel.Skeleton,
                    out rootTransform);
            }

            float skinScale = GetCharacterPlaybackBundle()?.OwnerSceneContext is { SkinScale: > 0f } context
                ? context.SkinScale
                : 1f;
            Vector3 origin = hasLaunchBone
                ? VfxRenderSession.PrepareBoneAnchorTransform(
                    boneTransform,
                    Vector3.Zero,
                    skinScale).Translation
                : Vector3.Zero;
            // The model faces +Z in its bind pose; the root joint carries its own authored rotation,
            // so only the turn the animation adds on top of that bind rotation changes the facing.
            Vector3 forward = Vector3.UnitZ;
            if (hasRootTransform &&
                AnimationService.TryGetBindRootTransform(_championModel.Skeleton, out Matrix4x4 bindRoot) &&
                Matrix4x4.Invert(bindRoot, out Matrix4x4 inverseBindRoot))
            {
                forward = Vector3.TransformNormal(Vector3.UnitZ, inverseBindRoot * rootTransform);
            }
            return (origin, forward);
        }

        internal static float SpellAnimationTime(double time, float duration)
        {
            if (!(duration > 0f) || !float.IsFinite(duration) || !double.IsFinite(time)) return 0f;
            return (float)Math.Clamp(time, 0d, Math.Max(0d, duration - 0.000001d));
        }
    }
}
