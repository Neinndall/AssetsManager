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

namespace AssetsManager.Services.Viewer.Vfx.Session
{
    /// <summary>
    /// Runtime of one non-focused Character in a VFX Studio Skin scene: owner mesh, pose evaluator,
    /// authored clip and the VFX session attached to it, looping on the shared transport. The focused
    /// actor uses the Studio's inspection pipeline; these objects move between both roles without
    /// being decoded again. GPU-bound release (mesh buffers, session renderer) belongs to the viewport.
    /// </summary>
    internal sealed class VfxSceneActorRuntime
    {
        private readonly VfxLoadingService _loading;
        private readonly HashSet<uint> _hiddenSubmeshes = new();
        private readonly CancellationTokenSource _lifetime = new();
        private IReadOnlyList<VfxClipCueEvaluator.VisibilityEntry> _visibilityTimeline =
            Array.Empty<VfxClipCueEvaluator.VisibilityEntry>();
        private IReadOnlySet<uint> _formHiddenSubmeshes;
        private Matrix4x4[] _bindSkinningMatrices;
        private Func<string, uint, Matrix4x4?> _bindBoneProvider;
        private Func<string, uint, Matrix4x4?> _poseBoneProvider;
        private bool _hiddenDirty = true;

        internal VfxSceneActorRuntime(
            VfxLoadingService loading,
            VfxLoadingService.Bundle bundle,
            VfxCharacterFormDefinition form,
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
        internal VfxLoadingService.Bundle PlaybackBundle { get; }
        internal VfxCharacterFormDefinition Form { get; }
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
        internal static async Task<VfxSceneActorRuntime> LoadAsync(
            VfxSceneActor actor,
            VfxLoadingService loading,
            SknLoadingService sknLoading,
            LogService log,
            string searchDirectory,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(actor);
            if (loading == null || sknLoading == null)
                return null;

            VfxLoadingService.Bundle bundle = await loading.LoadAsync(actor.Skin.BinPath, log, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Resolve the actor's form first: a form that reloads the model brings its own SKN, SKL
            // and GearData mesh properties, exactly as the focused inspector installs it.
            VfxCharacterFormDefinition form = VfxCharacterFormSemantics
                .CompatibleForms(bundle?.CharacterForms, bundle?.OwnerSceneContext)
                .FirstOrDefault(candidate => candidate.PathHash == actor.SelectedCharacterFormPathHash);
            bool reloads = form is { ReloadsModel: true };
            string authoredMesh = reloads && !string.IsNullOrWhiteSpace(form.MeshPath)
                ? form.MeshPath
                : bundle?.OwnerSceneContext?.MeshPath;
            string sknPath = loading.ResolveAssetPath(authoredMesh, searchDirectory, ".skn");
            if (string.IsNullOrEmpty(sknPath) || !File.Exists(sknPath))
                return null;

            SceneModel model = await sknLoading.LoadModelWithSkinBin(
                sknPath,
                bundle.PrimaryBinPath,
                searchDirectory,
                cancellationToken,
                gearUpgradePathHash: reloads ? form.PathHash : 0u);
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

                return new VfxSceneActorRuntime(
                    loading,
                    bundle,
                    form,
                    model,
                    sknPath,
                    new AnimationService(log),
                    new VfxRenderSession(log, loading),
                    new VfxClipCatalog(),
                    searchDirectory);
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
            VfxSceneActor actor,
            LogService log,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            cancellationToken = linked.Token;
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<AnimationClipCatalogItem> clips = ClipCatalog.BuildMetadata(
                PlaybackBundle,
                ResolveAnimationPath,
                actor?.AnimationParameter);
            AnimationClipCatalogItem requested = clips.FirstOrDefault(item =>
                    actor?.SelectedAnimationOwnerPathHash is uint owner &&
                    item.Clip?.OwnerPathHash == owner &&
                    (!actor.SelectedAnimationGraphPathHash.HasValue ||
                     item.Clip.GraphPathHash == actor.SelectedAnimationGraphPathHash)) ??
                VfxClipCatalog.OpeningClip(clips);

            AnimationClipCatalogItem prepared = requested == null
                ? null
                : await ClipCatalog.PrepareAsync(requested, PlaybackBundle, ResolveAnimationPath, log, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (prepared?.AnimationAsset == null)
            {
                ShowBindPose();
                return;
            }
            StartClip(prepared);
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

        private void ConfigureClipCues(AnimationClipCatalogItem clip)
        {
            _visibilityTimeline = clip == null
                ? Array.Empty<VfxClipCueEvaluator.VisibilityEntry>()
                : VfxClipCueEvaluator.BuildVisibilityTimeline(clip.TimedCues, FormHiddenSubmeshes);
            Animation.SetJointSnapCues(
                clip?.TimedCues.OfType<AnimationJointSnapCue>().ToArray() ?? Array.Empty<AnimationJointSnapCue>());
            _hiddenDirty = true;
        }

        /// <summary>Submeshes hidden by the skin and its form before any clip cue applies.</summary>
        private IReadOnlySet<uint> FormHiddenSubmeshes =>
            _formHiddenSubmeshes ??= VfxCharacterFormSemantics.HiddenSubmeshes(
                Bundle.OwnerSceneContext?.InitialHiddenSubmeshHashes,
                Form,
                Model.Parts);

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
        internal void Advance(float deltaSeconds, bool playing, float speed, VfxSceneActor actor)
        {
            if (Clip?.AnimationAsset == null || Session.ActiveSystem == null)
            {
                ApplyBindPose();
                ApplySubmeshVisibility(actor, FormHiddenSubmeshes);
                return;
            }

            if (playing)
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
            Animation.Update((float)time, Clip.AnimationAsset, Model.Skeleton, Model.SkinnedMesh, Model.Parts, Model.Name);
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
        internal void Seek(double time)
        {
            if (Clip?.AnimationAsset == null || Session.ActiveSystem == null) return;
            Session.Seek(VfxClipCueEvaluator.FoldedTime(time, LoopDuration));
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
            _bindBoneProvider ??= AnimationService.CreateBindBoneTransformProvider(skeleton);
            Model.SkinningMatrices = _bindSkinningMatrices;
            Session.SetOwnerSkinningMatrices(_bindSkinningMatrices);
            Session.UpdateBoneTransforms(_bindBoneProvider);
        }

        private void ApplySubmeshVisibility(VfxSceneActor actor, IReadOnlySet<uint> authoredHidden)
        {
            bool changed = _hiddenDirty;
            foreach (ModelPart part in Model.Parts)
            {
                if (string.IsNullOrWhiteSpace(part.Name)) continue;
                uint hash = Fnv1a.HashLower(part.Name);
                bool manualVisible = false;
                bool overridden = actor != null && actor.SubmeshOverrides.TryGetValue(hash, out manualVisible);
                bool visible = VfxCharacterViewportSemantics.ResolveSubmeshVisibility(
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
        internal void ApplyPlacement(VfxSceneActor actor, double autoYawDegrees)
        {
            if (actor == null) return;
            double yaw = actor.RotationY + autoYawDegrees;
            Model.PositionX = actor.PositionX;
            Model.PositionY = actor.PositionY;
            Model.PositionZ = actor.PositionZ;
            Model.RotationX = actor.RotationX;
            Model.RotationY = yaw;
            Model.RotationZ = actor.RotationZ;
            Model.Scale = AuthoredScale * actor.ScaleMultiplier;

            // Owner joints already carry the authored skinScale; only the user multiplier goes here.
            Session.SetWorldTransform(VfxCharacterViewportSemantics.CharacterPlacementWorld(
                actor.RotationX,
                yaw,
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
