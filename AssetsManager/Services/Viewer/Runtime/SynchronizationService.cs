using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;

namespace AssetsManager.Services.Viewer.Runtime;

/// <summary>Shared synchronization rules; viewers retain scene membership and runtime ownership.</summary>
internal sealed class SynchronizationService
{
    private bool _applying;

    internal void SynchronizeParts(ModelPart source, IEnumerable<SceneModel> targets, bool textures,
        Action<SceneModel, ModelPart> applied = null)
    {
        if (_applying || source == null) return;
        _applying = true;
        try
        {
            foreach (SceneModel model in targets)
            foreach (ModelPart part in model.Parts.Where(part => part != source &&
                string.Equals(part.Name, source.Name, StringComparison.OrdinalIgnoreCase)))
            {
                if (textures)
                {
                    string texture = MatchingTexture(source, part);
                    if (texture == null) continue;
                    part.SelectedTextureName = texture;
                }
                else part.IsVisible = source.IsVisible;
                applied?.Invoke(model, part);
            }
        }
        finally { _applying = false; }
    }

    internal static string MatchingTexture(ModelPart source, ModelPart target)
    {
        if (source?.SelectedTextureName == null || target == null) return null;
        string name = PathUtils.TruncateAtDot(source.SelectedTextureName);
        string exact = target.AvailableTextureNames.FirstOrDefault(texture =>
            string.Equals(PathUtils.TruncateAtDot(texture), name, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        // Preserve each chroma's own textures by transferring its selection slot, never bitmap data.
        int index = source.AvailableTextureNames.IndexOf(source.SelectedTextureName);
        return index >= 0 && index < target.AvailableTextureNames.Count
            ? target.AvailableTextureNames[index] : null;
    }

    internal static void ShareAnimations(IEnumerable<AnimationData> animations, IEnumerable<SceneModel> targets)
    {
        AnimationData[] source = animations.ToArray();
        foreach (SceneModel target in targets)
        foreach (AnimationData animation in source)
            if (!target.Animations.Any(item => item.Name == animation.Name))
                target.Animations.Add(animation);
    }

    internal static AnimationData MatchingAnimation(SceneModel model, AnimationData source) =>
        source == null ? null : model?.Animations.FirstOrDefault(item =>
            string.Equals(item.Name, source.Name, StringComparison.OrdinalIgnoreCase));

    internal static AnimationClipCatalogItem MatchingClip(IEnumerable<AnimationClipCatalogItem> clips,
        AnimationClipCatalogItem source) => source == null ? null : clips.FirstOrDefault(item =>
            string.Equals(item.Name, source.Name, StringComparison.OrdinalIgnoreCase));

    internal static void StartAnimation(SceneModel model, AnimationData animation)
    {
        if (model == null || animation?.AnimationAsset == null) return;
        model.CurrentAnimation = animation.AnimationAsset;
        model.AnimationTime = 0d;
        model.IsAnimationPaused = false;
    }

    internal static void PauseAnimation(SceneModel model, bool paused)
    {
        if (model?.CurrentAnimation != null) model.IsAnimationPaused = paused;
    }

    internal static double ClampTime(double time, double duration) =>
        duration > 0d ? Math.Clamp(time, 0d, duration) : Math.Max(0d, time);

    internal static void SeekAnimation(SceneModel model, double time)
    {
        if (model?.CurrentAnimation != null)
            model.AnimationTime = ClampTime(time, model.CurrentAnimation.Duration);
    }

    internal static void StopAnimation(SceneModel model)
    {
        if (model == null) return;
        model.CurrentAnimation = null;
        model.AnimationTime = 0d;
        model.IsAnimationPaused = true;
        // A cleared live palette restores the authored pose without changing the model's lighting path.
        model.SkinningMatrices = null;
    }

    internal static string SkeletonSignature(RigResource skeleton)
    {
        if (skeleton?.Joints is not { Count: > 0 }) return null;
        return string.Join(";", skeleton.Joints.Select(joint =>
        {
            string parent = joint.ParentId >= 0 && joint.ParentId < skeleton.Joints.Count
                ? skeleton.Joints[joint.ParentId].Name : string.Empty;
            return $"{joint.Name?.ToLowerInvariant()}:{parent?.ToLowerInvariant()}";
        }).OrderBy(name => name, StringComparer.Ordinal));
    }

    internal static void ImportClips(StudioSceneActor target, SceneModel targetModel,
        IEnumerable<SynchronizedAnimationSource> sources, IEnumerable<AnimationClipCatalogItem> ownClips)
    {
        string signature = SkeletonSignature(targetModel?.Skeleton);
        if (signature == null) return;
        var names = new HashSet<string>(ownClips.Select(item => item.Name), StringComparer.OrdinalIgnoreCase);
        names.UnionWith(target.ImportedAnimations.Where(source => source.SkeletonSignature == signature)
            .Select(source => source.Item.Name));
        foreach (SynchronizedAnimationSource source in sources)
            if (signature == source.SkeletonSignature && names.Add(source.Item.Name))
                target.ImportedAnimations.Add(source);
    }

    internal static IEnumerable<SynchronizedAnimationSource> ExportClips(StudioSceneActor actor,
        SceneModel model, IEnumerable<AnimationClipCatalogItem> clips, VfxLoadingService.Bundle bundle, string directory)
    {
        string signature = SkeletonSignature(model.Skeleton);
        foreach (AnimationClipCatalogItem item in clips.Where(item => !item.IsBindPose))
            yield return new SynchronizedAnimationSource(item with
            {
                AnimationAsset = null, Composition = null, TimedCues = Array.Empty<AnimationClipTimedCue>(),
                EventCount = 0, HasVfx = false, VfxSummary = "Shared animation"
            }, bundle, directory, signature);
        foreach (SynchronizedAnimationSource source in actor.ImportedAnimations) yield return source;
    }

    internal static IReadOnlyList<AnimationClipCatalogItem> MergeClips(StudioSceneActor actor,
        SceneModel model, IEnumerable<AnimationClipCatalogItem> ownClips, float? parameter = null)
    {
        string signature = SkeletonSignature(model?.Skeleton);
        var clips = ownClips.Where(item => item.SharedSource == null ||
            signature != null && item.SharedSource.SkeletonSignature == signature).ToList();
        var names = new HashSet<string>(clips.Select(item => item.Name), StringComparer.OrdinalIgnoreCase);
        foreach (SynchronizedAnimationSource source in actor?.ImportedAnimations ?? Enumerable.Empty<SynchronizedAnimationSource>())
            if (signature != null && signature == source.SkeletonSignature && names.Add(source.Item.Name))
                clips.Add(source.Item with
                {
                    SharedSource = source,
                    ParameterValue = parameter.HasValue && source.Item.ParameterValues is { Count: > 1 }
                        ? AnimationGraphPlayback.NearestParameter(source.Item.ParameterValues, parameter.Value)
                        : source.Item.ParameterValue
                });
        return clips;
    }

    internal static async Task<AnimationClipCatalogItem> PrepareClipAsync(AnimationClipCatalogItem item,
        VfxClipCatalog catalog, VfxLoadingService.Bundle ownBundle,
        VfxLoadingService loading, string searchDirectory, LogService log, CancellationToken token)
    {
        SynchronizedAnimationSource imported = item?.SharedSource;
        AnimationClipCatalogItem prepared = await catalog.PrepareAsync(item, imported?.Bundle ?? ownBundle,
            path => loading.ResolveAssetPath(path, imported?.SearchDirectory ?? searchDirectory, ".anm"), log, token);
        // Imported motion must not install another skin's material, visibility or particle events.
        return imported == null || prepared == null ? prepared : prepared with
        {
            Composition = null, TimedCues = Array.Empty<AnimationClipTimedCue>(), EventCount = 0,
            HasVfx = false, VfxSummary = "Shared animation"
        };
    }
}
