using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Core;
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
        LogService log)
    {
        var items = new List<AnimationClipCatalogItem>();
        foreach (AnimationClipDefinition clip in bundle.Clips)
        {
            IReadOnlyList<AnimationClipDefinition> playlist = ResolvePlaylist(clip, bundle.Clips);
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

                steps.Add(asset);
                float tick = atomic.TickDuration > 0f
                    ? atomic.TickDuration
                    : 1f / (asset.Fps > 0f ? asset.Fps : 30f);

                VfxAbilityComposition composition = VfxAbilityCompositionBuilder.Build(
                    atomic,
                    bundle.Systems,
                    bundle.ResourceMap,
                    allowEffectNameFallback: false);
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

                passTime += asset.Duration;
            }

            if (missing)
            {
                log?.LogWarning($"AnimationGraph clip 0x{clip.OwnerPathHash:x8} has an unavailable animation dependency.");
                continue;
            }

            string resolvedFilename = firstResolvedPath != null
                ? Path.GetFileNameWithoutExtension(firstResolvedPath)
                : null;
            bool isHexHashName = IsHexName(clip.ClipName);
            string name = !string.IsNullOrWhiteSpace(clip.ClipName) && !isHexHashName
                ? clip.ClipName
                : resolvedFilename ?? Path.GetFileNameWithoutExtension(playlist[0].AnimationFilePath);

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
                new ClipPlaylist(steps),
                clip,
                merged,
                timedCues.OrderBy(cue => cue.AtSeconds).ToArray(),
                eventCount,
                resolvedVfx > 0 || bundle.IdleEffects.Count > 0,
                $"{resolvedVfx} VFX · {eventCount} events"));
        }

        return items.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static IReadOnlyList<AnimationClipDefinition> ResolvePlaylist(
        AnimationClipDefinition clip,
        IReadOnlyList<AnimationClipDefinition> clips,
        float? parameter = null)
    {
        var byKey = clips
            .Where(item => item.GraphPathHash == clip.GraphPathHash)
            .GroupBy(item => item.OwnerPathHash)
            .ToDictionary(group => group.Key, group => group.First());
        var path = new HashSet<uint>();
        var result = new List<AnimationClipDefinition>();

        void Visit(AnimationClipDefinition current)
        {
            if (!path.Add(current.OwnerPathHash)) return;
            try
            {
                if (!string.IsNullOrWhiteSpace(current.AnimationFilePath))
                {
                    result.Add(current);
                    return;
                }

                IReadOnlyList<uint> children = current.ChildClipHashes ?? Array.Empty<uint>();
                IEnumerable<int> order = Enumerable.Range(0, children.Count);
                if (current.OwnerClassHash == ParametricClipClass &&
                    children.Count > 0 &&
                    parameter.HasValue)
                {
                    IReadOnlyList<float> values = current.ChildParameters ?? Array.Empty<float>();
                    float selected = parameter.Value;
                    order = order
                        .OrderBy(index => MathF.Abs((index < values.Count ? values[index] : 0f) - selected))
                        .ThenBy(index => index);
                }

                foreach (int index in order)
                {
                    int before = result.Count;
                    if (byKey.TryGetValue(children[index], out AnimationClipDefinition next))
                        Visit(next);
                    if (result.Count > before && current.OwnerClassHash != SequencerClipClass)
                        break;
                }
            }
            finally
            {
                path.Remove(current.OwnerPathHash);
            }
        }

        Visit(clip);
        return result;
    }

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

    private sealed class ClipPlaylist(IReadOnlyList<IAnimationAsset> steps) : IAnimationAsset
    {
        public float Duration { get; } = steps.Sum(step => step.Duration);
        public float Fps => steps.Count > 0 ? steps[0].Fps : 30f;
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;

        public void Evaluate(
            float time,
            IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose)
        {
            if (IsDisposed) return;
            float remaining = Math.Clamp(time, 0f, Duration);
            for (int index = 0; index < steps.Count; index++)
            {
                IAnimationAsset step = steps[index];
                if (remaining < step.Duration || index == steps.Count - 1)
                {
                    step.Evaluate(Math.Min(remaining, step.Duration), pose);
                    return;
                }
                remaining -= step.Duration;
            }
        }
    }
}
