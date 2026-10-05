using AssetsManager.Services.Viewer.Semantics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Hashing;
using AssetsManager.Services.Viewer.Vfx.Session;

namespace AssetsManager.Services.Viewer.Runtime
{
    /// <summary>
    /// Runtime of one non-focused Character in a 3D Studio Skin scene: owner mesh, pose evaluator,
    /// authored clip and the VFX session attached to it, looping on the shared transport. The focused
    /// actor uses the Studio's inspection pipeline; these objects move between both roles without
    /// being decoded again. GPU-bound release (mesh buffers, session renderer) belongs to the viewport.
    /// </summary>
    internal sealed class StudioSceneActorRuntime
    {
        private readonly VfxLoadingService _loading;
        private readonly HashSet<uint> _hiddenSubmeshes = new();
        private readonly CancellationTokenSource _lifetime = new();
        private IReadOnlyList<VfxClipCueEvaluator.VisibilityEntry> _visibilityTimeline =
            Array.Empty<VfxClipCueEvaluator.VisibilityEntry>();
        private IReadOnlySet<uint> _formHiddenSubmeshes;
        private IReadOnlyCollection<string> _gameStates = Array.Empty<string>();
        private Matrix4x4[] _bindSkinningMatrices;
        private Func<string, uint, Matrix4x4?> _bindBoneProvider;
        private Func<string, uint, Matrix4x4?> _poseBoneProvider;
        private bool _hiddenDirty = true;
        private int _clipGeneration;

        internal StudioSceneActorRuntime(
            VfxLoadingService loading,
            VfxLoadingService.Bundle bundle,
            CharacterFormDefinition form,
            SceneModel model,
            string sknPath,
            AnimationService animation,
            VfxRenderSession session,
            VfxClipCatalog clipCatalog,
            string searchDirectory)
        {
            _loading = loading ?? throw new ArgumentNullException(nameof(loading));
            Bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
            Model = model ?? throw new ArgumentNullException(nameof(model));
            Animation = animation ?? throw new ArgumentNullException(nameof(animation));
            Session = session ?? throw new ArgumentNullException(nameof(session));
            ClipCatalog = clipCatalog ?? new VfxClipCatalog();
            Form = form;
            SknPath = sknPath;
            SearchDirectory = searchDirectory;
            PlaybackBundle = bundle.CreateCharacterPlaybackView(form);
            AuthoredScale = PlaybackBundle.OwnerSceneContext is { SkinScale: > 0f } owner ? owner.SkinScale : 1d;
            _poseBoneProvider = (boneName, boneHash) =>
            {
                if (!string.IsNullOrEmpty(boneName) &&
                    Animation.TryGetBoneTransformExactName(boneName, out Matrix4x4 transform))
                {
                    return transform;
                }
                return boneHash != 0 && Animation.TryGetBoneTransformFnv(boneHash, out transform)
                    ? transform
                    : null;
            };
        }

        internal VfxLoadingService.Bundle Bundle { get; }
        internal string SourceIdentity { get; set; }
        internal VfxLoadingService.Bundle PlaybackBundle { get; }
        internal CharacterFormDefinition Form { get; }
        internal SceneModel Model { get; }
        internal string SknPath { get; }
        internal AnimationService Animation { get; }
        internal VfxRenderSession Session { get; }
        internal VfxClipCatalog ClipCatalog { get; }
        internal string SearchDirectory { get; }
        internal double AuthoredScale { get; }

        /// <summary>The prepared clip this actor plays, or null for bind pose.</summary>
        internal AnimationClipCatalogItem Clip { get; private set; }

        internal double LoopDuration => Clip?.Duration > 0f ? Clip.Duration : 3d;

        internal int LiveParticleCount => Session.LiveParticleCount;

        /// <summary>
        /// Loads a Character exactly like the focused Skin preview: skin BIN graph, owner SKN/SKL with
        /// its authored materials, GPU skinning and the remembered form.
        /// </summary>
        internal static async Task<StudioSceneActorRuntime> LoadAsync(
            StudioSceneActor actor,
            VfxLoadingService loading,
            SknLoadingService sknLoading,
            LogService log,
            string searchDirectory,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(actor);
            if (loading == null || sknLoading == null)
                return null;

            searchDirectory = actor.Skin.ResourceRoot ?? searchDirectory;
            VfxLoadingService.Bundle bundle = string.IsNullOrEmpty(actor.Skin.BinPath) ? new VfxLoadingService.Bundle()
                : await loading.LoadAsync(actor.Skin.BinPath, log, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Resolve the actor's form first: a form that reloads the model brings its own SKN, SKL
            // and GearData mesh properties, exactly as the focused inspector installs it.
            CharacterFormDefinition form = CharacterFormSemantics
                .CompatibleForms(bundle?.CharacterForms, bundle?.OwnerSceneContext)
                .FirstOrDefault(candidate => candidate.PathHash == actor.SelectedCharacterFormPathHash);
            bool reloads = form is { ReloadsModel: true };
            var (model, sknPath) = await sknLoading.LoadStudioModelAsync(
                actor.Skin, bundle, form, loading, searchDirectory, cancellationToken);
            if (model == null)
                return null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool formSkeleton = reloads && !string.IsNullOrWhiteSpace(form.SkeletonPath);
                EnsureSkeleton(
                    model,
                    loading,
                    formSkeleton ? form.SkeletonPath : bundle.OwnerSceneContext?.SkeletonPath,
                    sknPath,
                    searchDirectory,
                    replaceLoaded: formSkeleton);
                EnsureGpuSkinning(model, log);

                int gearIndex = form?.GearIndex ?? -1;
                foreach (ModelPart part in model.Parts)
                    part.EquippedGearIndex = gearIndex;

                return new StudioSceneActorRuntime(
                    loading,
                    bundle,
                    form,
                    model,
                    sknPath,
                    new AnimationService(log),
                    new VfxRenderSession(log, loading),
                    new VfxClipCatalog(),
                    searchDirectory) { SourceIdentity = actor.Skin.IdentityPath };
            }
            catch
            {
                model.Dispose();
                throw;
            }
        }

        private static void EnsureSkeleton(
            SceneModel model,
            VfxLoadingService loading,
            string authoredSkeleton,
            string sknPath,
            string searchDirectory,
            bool replaceLoaded = false)
        {
            // A form's authored SKL replaces the one the loader found beside the SKN.
            if (model.Skeleton != null && !replaceLoaded) return;
            string sklPath = !string.IsNullOrEmpty(authoredSkeleton)
                ? loading.ResolveAssetPath(authoredSkeleton, searchDirectory, ".skl")
                : Path.ChangeExtension(sknPath, ".skl");
            if (string.IsNullOrEmpty(sklPath) || !File.Exists(sklPath)) return;
            using FileStream stream = File.OpenRead(sklPath);
            model.Skeleton = new RigResource(stream);
        }

        private static void EnsureGpuSkinning(SceneModel model, LogService log)
        {
            if (model.GpuSkinningData != null || model.Skeleton == null || model.SkinnedMesh == null)
                return;
            model.GpuSkinningData = GpuSkinningData.TryCreate(
                model.Skeleton,
                model.SkinnedMesh,
                model.Parts,
                out string failure);
            if (model.GpuSkinningData == null)
                log?.LogDebug($"Scene actor GPU skinning unavailable: {failure ?? "Unsupported skin data."}");
        }

        /// <summary>
        /// Resolves the actor's remembered clip (or the Skin's opening Idle) and starts it. A missing
        /// clip leaves the actor in bind pose with no VFX, matching the focused Skin preview.
        /// </summary>
        internal async Task PlayRememberedClipAsync(
            StudioSceneActor actor,
            LogService log,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            cancellationToken = linked.Token;
            cancellationToken.ThrowIfCancellationRequested();
            int generation = ++_clipGeneration;
            if (actor?.ShowsBindPose == true)
            {
                ShowBindPose();
                return;
            }
            IReadOnlyList<AnimationClipCatalogItem> clips = Clips(actor);
            AnimationClipCatalogItem requested = clips.FirstOrDefault(item =>
                    actor?.SelectedAnimationOwnerPathHash is uint owner &&
                    item.Clip?.OwnerPathHash == owner &&
                    (!actor.SelectedAnimationGraphPathHash.HasValue ||
                    item.Clip.GraphPathHash == actor.SelectedAnimationGraphPathHash)) ??
                VfxClipCatalog.OpeningClip(clips);

            AnimationClipCatalogItem prepared = requested == null
                ? null
                : await SynchronizationService.PrepareClipAsync(requested, ClipCatalog,
                    PlaybackBundle, _loading, SearchDirectory, log, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (generation != _clipGeneration) return;
            if (prepared?.AnimationAsset == null)
            {
                ShowBindPose();
                return;
            }
            StartClip(prepared);
        }

        internal void CancelClipPreparation() => ++_clipGeneration;

        internal IReadOnlyList<AnimationClipCatalogItem> OwnClips(StudioSceneActor actor) =>
            ClipCatalog.BuildMetadata(PlaybackBundle, ResolveAnimationPath, actor?.AnimationParameter);

        internal IReadOnlyList<AnimationClipCatalogItem> Clips(StudioSceneActor actor) =>
            SynchronizationService.MergeClips(actor, Model, OwnClips(actor), actor?.AnimationParameter);

        internal async Task<bool> PlaySynchronizedClipAsync(AnimationClipCatalogItem source,
            StudioSceneActor actor, LogService log, Func<bool> isCurrent)
        {
            int generation = ++_clipGeneration;
            if (source.IsBindPose)
            {
                if (!isCurrent()) return false;
                ShowBindPose();
                actor.ShowsBindPose = true;
                actor.SelectedAnimationFilePath = null;
                actor.SelectedAnimationGraphPathHash = null;
                actor.SelectedAnimationOwnerPathHash = null;
                actor.SelectedSystemPathHash = null;
                actor.SelectedSpellPathHash = null;
                return true;
            }
            AnimationClipCatalogItem requested = SynchronizationService.MatchingClip(Clips(actor), source);
            if (requested == null) return false;
            float? parameter = source.ParameterValue.HasValue && requested.ParameterValues is { Count: > 1 }
                ? AnimationGraphPlayback.NearestParameter(requested.ParameterValues, source.ParameterValue.Value)
                : requested.ParameterValue;
            requested = requested with { ParameterValue = parameter };
            if (Clip != null && Clip.Clip == requested.Clip && Clip.ParameterValue == parameter) return true;
            AnimationClipCatalogItem prepared = await SynchronizationService.PrepareClipAsync(requested,
                ClipCatalog, PlaybackBundle, _loading, SearchDirectory, log, _lifetime.Token);
            if (generation != _clipGeneration || !isCurrent() || prepared?.AnimationAsset == null) return false;
            StartClip(prepared);
            actor.ShowsBindPose = false;
            actor.SelectedAnimationFilePath = requested.FilePath;
            actor.SelectedAnimationGraphPathHash = requested.Clip?.GraphPathHash;
            actor.SelectedAnimationOwnerPathHash = requested.Clip?.OwnerPathHash;
            actor.SelectedSystemPathHash = null;
            actor.SelectedSpellPathHash = null;
            actor.AnimationParameter = requested.ParameterValue;
            return true;
        }

        /// <summary>
        /// Continues a clip that is already configured on the session (a demoted focused actor) so the
        /// hand-over does not restart the animation or its particles.
        /// </summary>
        internal void AdoptPlayingClip(AnimationClipCatalogItem clip)
        {
            Clip = clip;
            ConfigureClipCues(clip);
            Session.SetBoneTransformSampler(SampleBone);
        }

        private void StartClip(AnimationClipCatalogItem clip)
        {
            Clip = clip;
            Model.CurrentAnimation = clip.AnimationAsset;
            Model.AnimationTime = 0d;
            ConfigureClipCues(clip);
            Animation.Update(0f, clip.AnimationAsset, Model.Skeleton, Model.SkinnedMesh, Model.Parts, Model.Name);
            Session.SetBoneTransformSampler(SampleBone);

            // Same deterministic seed and inputs as the focused Clip preview.
            Session.SetAnimationSession(
                clip.Composition,
                PlaybackBundle.IdleEffects,
                Bundle.Systems,
                PlaybackBundle.ResourceMap,
                SearchDirectory,
                HashCode.Combine(clip.Name, Bundle.Systems.Count),
                LoopDuration,
                PlaybackBundle.OwnerSceneContext);
            Session.SetOwnerSkinningMatrices(Animation.FinalBoneTransforms);
            Session.Play();
        }

        private void ShowBindPose()
        {
            Clip = null;
            Model.CurrentAnimation = null;
            Model.AnimationTime = 0d;
            ConfigureClipCues(null);
            Session.SetSystem(null);
        }

        /// <summary>The GAME STATE buffs the actor has on, read with its own clip by its materials and submesh conditions.</summary>
        internal void SetGameStates(IEnumerable<string> buffs)
        {
            _gameStates = buffs?.ToArray() ?? Array.Empty<string>();
            ConfigureClipCues(Clip);
        }

        private void ConfigureClipCues(AnimationClipCatalogItem clip)
        {
            Model.GameState = GameMaterialState.Preview(_gameStates, clip, PlaybackBundle.SpellPreviews);
            _formHiddenSubmeshes = null;
            _visibilityTimeline = clip == null
                ? Array.Empty<VfxClipCueEvaluator.VisibilityEntry>()
                : VfxClipCueEvaluator.BuildVisibilityTimeline(clip.TimedCues, FormHiddenSubmeshes,
                    Model.Parts.Select(part => part.Name));
            Animation.SetPoseCues(clip?.TimedCues,
                Bundle.AnimationGraphs.FirstOrDefault(graph => graph.PathHash == clip?.Clip?.GraphPathHash)?.Masks);
            _hiddenDirty = true;
        }

        /// <summary>Submeshes hidden by the skin and its form before any clip cue applies.</summary>
        private IReadOnlySet<uint> FormHiddenSubmeshes =>
            _formHiddenSubmeshes ??= CharacterFormSemantics.HiddenSubmeshes(
                Bundle.OwnerSceneContext?.InitialHiddenSubmeshHashes,
                Form,
                Model.Parts,
                Bundle.OwnerSceneContext?.SubmeshConditions,
                Model.GameState);

        private Matrix4x4? SampleBone(double time, string name, uint hash) =>
            Animation.TrySampleBoneTransform((float)time, name, hash, out Matrix4x4 transform)
                ? transform
                : null;

        private string ResolveAnimationPath(string path) =>
            _loading.ResolveAssetPath(path, SearchDirectory, ".anm");

        /// <summary>
        /// Advances the actor on the shared transport. Background actors always loop their clip so a
        /// scene keeps moving while the focused actor is inspected.
        /// </summary>
        internal void Advance(float deltaSeconds, bool playing, float speed, StudioSceneActor actor,
            double? transportTime = null)
        {
            if (Clip?.AnimationAsset == null || Session.ActiveSystem == null)
            {
                ApplyBindPose();
                ApplySubmeshVisibility(actor, FormHiddenSubmeshes);
                return;
            }

            if (transportTime.HasValue)
            {
                double wanted = SynchronizationService.ClampTime(transportTime.Value, LoopDuration);
                double difference = wanted - Session.PlaybackTime;
                if (difference < 0d || difference > 0.1d) Session.Seek(wanted);
                else if (difference > 0d)
                {
                    Session.Play();
                    Session.ActiveSystem.Speed = 1d;
                    Session.Update((float)difference);
                }
                if (!playing) Session.Pause();
            }
            else if (playing)
            {
                Session.Play();
                Session.ActiveSystem.Speed = speed;
                Session.Update(deltaSeconds);
                if (Session.PlaybackTime >= LoopDuration)
                {
                    Session.Seek(0d);
                    Session.Play();
                }
            }
            else
            {
                Session.Pause();
            }

            double time = Session.PlaybackTime;
            Animation.Update((float)time, Clip.AnimationAsset, Model.Skeleton, Model.SkinnedMesh, Model.Parts, Model.Name, Model, deltaSeconds);
            Model.SkinningMatrices = Animation.FinalBoneTransforms;
            Model.GpuSkinningData = Animation.SkinningData;
            Session.SetOwnerSkinningMatrices(Animation.FinalBoneTransforms);
            Session.UpdateBoneTransforms(_poseBoneProvider);

            IReadOnlySet<uint> cueHidden = VfxClipCueEvaluator.HiddenSubmeshesAt(
                _visibilityTimeline,
                VfxClipCueEvaluator.FoldedTime(time, Clip.Duration));
            ApplySubmeshVisibility(actor, cueHidden);
        }

        /// <summary>Moves the actor's clip to the transport time after an explicit seek.</summary>
        internal void Seek(double time, bool loop = true)
        {
            if (Clip?.AnimationAsset == null || Session.ActiveSystem == null) return;
            Session.Seek(loop ? VfxClipCueEvaluator.FoldedTime(time, LoopDuration)
                : SynchronizationService.ClampTime(time, LoopDuration));
        }

        private void ApplyBindPose()
        {
            RigResource skeleton = Model.Skeleton;
            if (skeleton?.Joints == null || skeleton.Joints.Count == 0)
            {
                Model.SkinningMatrices = null;
                return;
            }

            _bindSkinningMatrices ??= AnimationService.CreateBindSkinningMatrices(skeleton);
            _bindBoneProvider ??= AnimationService.CreateBindBoneTransformProvider(skeleton, Model.PoseDefinition);
            Model.SkinningMatrices = _bindSkinningMatrices;
            Session.SetOwnerSkinningMatrices(_bindSkinningMatrices);
            Session.UpdateBoneTransforms(_bindBoneProvider);
        }

        private void ApplySubmeshVisibility(StudioSceneActor actor, IReadOnlySet<uint> authoredHidden)
        {
            bool changed = _hiddenDirty;
            foreach (ModelPart part in Model.Parts)
            {
                if (string.IsNullOrWhiteSpace(part.Name)) continue;
                uint hash = Fnv1a.HashLower(part.Name);
                bool manualVisible = false;
                bool overridden = actor != null && actor.SubmeshOverrides.TryGetValue(hash, out manualVisible);
                bool visible = CharacterViewportSemantics.ResolveSubmeshVisibility(
                    !authoredHidden.Contains(hash),
                    overridden,
                    manualVisible);
                if (part.IsVisible != visible) part.IsVisible = visible;
                changed |= visible ? _hiddenSubmeshes.Remove(hash) : _hiddenSubmeshes.Add(hash);
            }

            // The session renderer only exists after its GL initialization; re-push once it does.
            if (changed || !Session.IsInitialized)
            {
                Session.SetOwnerHiddenSubmeshes(_hiddenSubmeshes);
                _hiddenDirty = !Session.IsInitialized;
            }
        }

        /// <summary>Applies the actor placement to the mesh and to its character-attached VFX.</summary>
        internal void ApplyPlacement(StudioSceneActor actor)
        {
            if (actor == null) return;
            Model.PositionX = actor.PositionX;
            Model.PositionY = actor.PositionY;
            Model.PositionZ = actor.PositionZ;
            Model.RotationX = actor.RotationX;
            Model.RotationY = actor.RotationY;
            Model.RotationZ = actor.RotationZ;
            Model.Scale = AuthoredScale * actor.ScaleMultiplier;

            // Owner joints already carry the authored skinScale; only the user multiplier goes here.
            Session.SetWorldTransform(CharacterViewportSemantics.CharacterPlacementWorld(
                actor.RotationX,
                actor.RotationY,
                actor.RotationZ,
                actor.ScaleMultiplier,
                actor.PositionX,
                actor.PositionY,
                actor.PositionZ));
        }

        internal string DescribePlayback() =>
            Clip == null ? "Bind pose" : $"{Clip.DisplayName} · {Clip.Duration:F2}s";

        /// <summary>
        /// Releases CPU-owned state. The caller queues the SceneModel GPU release before this and
        /// disposes the session on the render callback.
        /// </summary>
        internal void ReleaseCpuState()
        {
            _lifetime.Cancel();
            Model.CurrentAnimation = null;
            Model.Dispose();
            ClipCatalog.Dispose();
            Animation.Dispose();
        }
    }
}
