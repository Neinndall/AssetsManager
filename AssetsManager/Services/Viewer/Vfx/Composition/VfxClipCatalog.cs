using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Vfx.Composition;

/// <summary>
/// Owns the animation assets behind AnimationGraph clips and resolves their authored event
/// timelines for VFX Studio playback and inspection.
/// </summary>
internal sealed class VfxClipCatalog : IDisposable
{
    private static readonly uint SequencerClipClass = Fnv1a.HashLower("SequencerClipData");
    private static readonly uint ParametricClipClass = Fnv1a.HashLower("ParametricClipData");
    private readonly Dictionary<string, IAnimationAsset> _assets = new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<AnimationClipCatalogItem> Build(
        VfxLoadingService.Bundle bundle,
        Func<string, string> resolve,
        LogService log,
        float? parameter = null)
    {
        var items = new List<AnimationClipCatalogItem>();
        IReadOnlyList<AnimationClipDefinition> graphClips = SelectGraphClips(
            bundle.Clips,
            bundle.OwnerSceneContext?.AnimationGraphPathHash ?? 0u);

        // LTK's skin preview reads the one AnimationGraphData referenced by
        // skinAnimationProperties.animationGraphData. Linked BINs are dependencies/resources,
        // not additional clip tables to merge into the picker.
        foreach (AnimationClipDefinition clip in graphClips)
        {
            IReadOnlyList<float> parameterValues = ParameterValues(clip);
            float? effectiveParameter = parameterValues.Count > 1
                ? NearestParameter(
                    parameterValues,
                    parameter ??
                    clip.ParametricValues?.FirstOrDefault() ??
                    parameterValues[0])
                : null;
            IReadOnlyList<AnimationClipDefinition> playlist =
                ResolvePlaylist(clip, graphClips, effectiveParameter);
            if (playlist.Count == 0) continue;

            var steps = new List<IAnimationAsset>();
            var particleEvents = new List<VfxCompositionEvent>();
            var timedCues = new List<AnimationClipTimedCue>();
            float passTime = 0f;
            int eventCount = 0;
            bool missing = false;
            string firstResolvedPath = null;

            foreach (AnimationClipDefinition atomic in playlist)
            {
                string path = resolve(atomic.AnimationFilePath);
                if (path == null)
                {
                    missing = true;
                    break;
                }

                firstResolvedPath ??= path;
                if (!_assets.TryGetValue(path, out IAnimationAsset asset))
                {
                    try
                    {
                        using var stream = File.OpenRead(path);
                        asset = AnimationAsset.Load(stream);
                        _assets[path] = asset;
                    }
                    catch (Exception ex)
                    {
                        log?.LogError(ex, $"Failed to load AnimationGraph clip: {path}");
                        missing = true;
                        break;
                    }
                }

                IAnimationAsset timedAsset = RetimeForGraph(asset, atomic.TickDuration);
                steps.Add(timedAsset);
                float tick = 1f / timedAsset.Fps;

                VfxAbilityComposition composition = VfxAbilityCompositionBuilder.Build(
                    atomic,
                    bundle.Systems,
                    bundle.ResourceMap,
                    allowEffectNameFallback: false,
                    resolverOnly: true);
                foreach (VfxCompositionEvent cue in composition.Events)
                {
                    // LTK's SkinViewport does not turn ParticleEventData kill records into
                    // drawable cues; they remain authored metadata only.
                    if (cue.Event.IsKillEvent) continue;
                    particleEvents.Add(cue with
                    {
                        Event = cue.Event with
                        {
                            StartFrame = passTime + cue.Event.StartFrame * tick,
                            EndFrame = cue.Event.EndFrame < 0f
                                ? -1f
                                : passTime + cue.Event.EndFrame * tick
                        }
                    });
                }

                foreach (AnimationClipEventDefinition authoredEvent in atomic.Events)
                {
                    eventCount++;
                    double at = passTime + authoredEvent.StartFrame * tick;
                    double? until = authoredEvent.EndFrame >= 0f
                        ? passTime + authoredEvent.EndFrame * tick
                        : null;
                    if (until <= at) until = null;

                    switch (authoredEvent)
                    {
                        case AnimationSubmeshVisibilityEventDefinition visibility:
                            timedCues.Add(new AnimationSubmeshVisibilityCue(
                                at,
                                until,
                                visibility.ShowSubmeshHashes,
                                visibility.HideSubmeshHashes));
                            break;
                        case AnimationJointSnapEventDefinition snap:
                            timedCues.Add(new AnimationJointSnapCue(
                                at,
                                until,
                                snap.JointHash,
                                snap.SnapToHash,
                                snap.Offset));
                            break;
                        case AnimationConformToPathEventDefinition conform:
                            timedCues.Add(new AnimationConformToPathCue(
                                at,
                                until,
                                conform.MaskHash,
                                conform.BlendInSeconds,
                                conform.BlendOutSeconds));
                            break;
                    }
                }

                passTime += timedAsset.Duration;
            }

            if (missing)
            {
                log?.LogWarning($"AnimationGraph clip 0x{clip.OwnerPathHash:x8} has an unavailable animation dependency.");
                continue;
            }

            // Keep the graph key as the clip's identity. LTK shows the key's resolved name
            // when available and its hex hash otherwise; it never substitutes the .anm filename.
            string name = DisplayNameFor(clip);

            int resolvedVfx = particleEvents.Count(cue => cue.System != null && !cue.Event.IsKillEvent);
            var merged = new VfxAbilityComposition(
                clip.OwnerPathHash,
                clip.OwnerClassHash,
                1f,
                0f,
                passTime,
                particleEvents.OrderBy(cue => cue.Event.StartFrame).ToArray(),
                name,
                clip.AnimationFilePath,
                clip.GraphPathHash,
                clip.ChildClipHashes)
            {
                ResolvedCount = particleEvents.Count(cue => cue.System != null)
            };

            items.Add(new AnimationClipCatalogItem(
                name,
                $"{name} · {clip.OwnerPathHash:x8}",
                firstResolvedPath ?? playlist[0].AnimationFilePath,
                passTime,
                AnimationGraphPlayback.CreatePlaylist(steps),
                clip,
                merged,
                timedCues.OrderBy(cue => cue.AtSeconds).ToArray(),
                eventCount,
                resolvedVfx > 0 || bundle.IdleEffects.Count > 0,
                $"{resolvedVfx} VFX · {eventCount} events",
                parameterValues,
                effectiveParameter));
        }

        // LTK preserves mClipDataMap order in the preview picker. Filtering playable clips
        // must not alphabetize or otherwise reorder the graph's authored map entries.
        return items.ToArray();
    }

    internal static IReadOnlyList<AnimationClipDefinition> SelectGraphClips(
        IReadOnlyList<AnimationClipDefinition> clips,
        uint animationGraphPathHash) =>
        AnimationGraphPlayback.SelectGraphClips(clips, animationGraphPathHash);

    internal static string DisplayNameFor(AnimationClipDefinition clip) =>
        AnimationGraphPlayback.DisplayNameFor(clip);

    internal static IReadOnlyList<float> ParameterValues(AnimationClipDefinition clip) =>
        AnimationGraphPlayback.ParameterValues(clip);

    internal static float NearestParameter(IReadOnlyList<float> values, float value) =>
        AnimationGraphPlayback.NearestParameter(values, value);

    internal static IReadOnlyList<AnimationClipDefinition> ResolvePlaylist(
        AnimationClipDefinition clip,
        IReadOnlyList<AnimationClipDefinition> clips,
        float? parameter = null) =>
        AnimationGraphPlayback.ResolvePlaylist(clip, clips, parameter);

    internal static IAnimationAsset RetimeForGraph(IAnimationAsset asset, float tickDuration) =>
        AnimationGraphPlayback.RetimeForGraph(asset, tickDuration);

    public void Dispose()
    {
        foreach (IAnimationAsset asset in _assets.Values) asset.Dispose();
        _assets.Clear();
    }

    private static bool IsHexName(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (value.Length == 16 && ulong.TryParse(
                value,
                System.Globalization.NumberStyles.HexNumber,
                null,
                out _)) return true;
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
               ulong.TryParse(
                   value.AsSpan(2),
                   System.Globalization.NumberStyles.HexNumber,
                   null,
                   out _);
    }

}
