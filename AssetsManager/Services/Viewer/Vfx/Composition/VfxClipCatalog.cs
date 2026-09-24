using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly object _assetGate = new();
    private bool _disposed;

    /// <summary>
    /// Builds the clip picker from AnimationGraph metadata only. Animation files are resolved to
    /// paths but are not decoded until PrepareAsync is called for the selected clip. This mirrors
    /// LTK's clip table/viewport split and keeps opening a Skin independent from its ANM count.
    /// </summary>
    internal IReadOnlyList<AnimationClipCatalogItem> BuildMetadata(
        VfxLoadingService.Bundle bundle,
        Func<string, string> resolve,
        float? parameter = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(resolve);

        var items = new List<AnimationClipCatalogItem>();
        IReadOnlyList<AnimationClipDefinition> graphClips = SelectGraphClips(
            bundle.Clips,
            bundle.OwnerSceneContext?.AnimationGraphPathHash ?? 0u);
        int resolvedIdleVfx = VfxAbilityCompositionBuilder.CountResolvedIdleEffects(
            bundle.IdleEffects,
            bundle.Systems,
            bundle.ResourceMap);

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

            string firstResolvedPath = null;
            foreach (AnimationClipDefinition atomic in playlist)
            {
                string path = resolve(atomic.AnimationFilePath);
                if (path != null)
                    firstResolvedPath ??= path;
            }

            var particleEvents = new List<VfxCompositionEvent>();
            int eventCount = 0;
            foreach (AnimationClipDefinition atomic in playlist)
            {
                VfxAbilityComposition composition = VfxAbilityCompositionBuilder.Build(
                    atomic,
                    bundle.Systems,
                    bundle.ResourceMap,
                    allowEffectNameFallback: false,
                    resolverOnly: true);
                foreach (VfxCompositionEvent cue in composition.Events)
                {
                    if (!cue.Event.IsKillEvent)
                        particleEvents.Add(cue);
                }
                eventCount += atomic.Events?.Count ?? 0;
            }

            string name = DisplayNameFor(clip);
            int resolvedVfx = particleEvents.Count(cue => cue.System != null);
            var summary = new VfxAbilityComposition(
                clip.OwnerPathHash,
                clip.OwnerClassHash,
                1f,
                0f,
                0f,
                particleEvents.ToArray(),
                name,
                clip.AnimationFilePath,
                clip.GraphPathHash,
                clip.ChildClipHashes)
            {
                ResolvedCount = resolvedVfx
            };

            items.Add(new AnimationClipCatalogItem(
                name,
                $"{name} · {clip.OwnerPathHash:x8}",
                firstResolvedPath ?? playlist[0].AnimationFilePath,
                0f,
                null,
                clip,
                summary,
                Array.Empty<AnimationClipTimedCue>(),
                eventCount,
                resolvedVfx > 0 || resolvedIdleVfx > 0,
                $"{resolvedVfx} VFX · {resolvedIdleVfx} idle · {eventCount} events",
                parameterValues,
                effectiveParameter));
        }

        // LTK preserves mClipDataMap order in the preview picker. Filtering playable clips
        // must not alphabetize or otherwise reorder the graph's authored map entries.
        return items.ToArray();
    }

    /// <summary>
    /// The clip a Skin preview opens on: the first playable authored clip whose name starts
    /// with "idle", preserving AnimationGraph order and ignoring case. No idle means bind pose.
    /// </summary>
    internal static AnimationClipCatalogItem OpeningClip(IEnumerable<AnimationClipCatalogItem> clips)
        => clips?.FirstOrDefault(item =>
            !string.IsNullOrWhiteSpace(item?.Name) &&
            item.Name.StartsWith("idle", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Decodes only the selected clip's reachable atomic playlist and builds its exact event
    /// timeline on the ANM clock. Source ANMs are cached until the catalog is disposed.
    /// </summary>
    internal async Task<AnimationClipCatalogItem> PrepareAsync(
        AnimationClipCatalogItem item,
        VfxLoadingService.Bundle bundle,
        Func<string, string> resolve,
        LogService log,
        CancellationToken cancellationToken = default)
    {
        if (item?.Clip == null || bundle == null || resolve == null)
            return null;

        IReadOnlyList<AnimationClipDefinition> graphClips = SelectGraphClips(
            bundle.Clips,
            bundle.OwnerSceneContext?.AnimationGraphPathHash ?? 0u);
        IReadOnlyList<AnimationClipDefinition> playlist =
            ResolvePlaylist(item.Clip, graphClips, item.ParameterValue);
        if (playlist.Count == 0)
            return null;

        var resolved = new (AnimationClipDefinition Clip, string Path)[playlist.Count];
        for (int index = 0; index < playlist.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AnimationClipDefinition atomic = playlist[index];
            string path = resolve(atomic.AnimationFilePath);
            if (path == null)
            {
                log?.LogWarning(
                    $"AnimationGraph clip 0x{item.Clip.OwnerPathHash:x8} has an unavailable animation dependency.");
                return null;
            }
            resolved[index] = (atomic, path);
        }

        return await Task.Run(
            () => PrepareResolved(item, bundle, resolved, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private AnimationClipCatalogItem PrepareResolved(
        AnimationClipCatalogItem item,
        VfxLoadingService.Bundle bundle,
        IReadOnlyList<(AnimationClipDefinition Clip, string Path)> playlist,
        CancellationToken cancellationToken)
    {
        var steps = new List<IAnimationAsset>(playlist.Count);
        var particleEvents = new List<VfxCompositionEvent>();
        var timedCues = new List<AnimationClipTimedCue>();
        float passTime = 0f;
        int eventCount = 0;

        foreach ((AnimationClipDefinition atomic, string path) in playlist)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IAnimationAsset asset = GetOrLoadAsset(path, cancellationToken);
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
                // LTK's SkinViewport does not turn ParticleEventData kill records into drawable cues.
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
                until = AnimationGraphPlayback.TimedEventEnd(authoredEvent, at, until);

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

        int resolvedVfx = particleEvents.Count(cue => cue.System != null);
        int resolvedIdleVfx = VfxAbilityCompositionBuilder.CountResolvedIdleEffects(
            bundle.IdleEffects,
            bundle.Systems,
            bundle.ResourceMap);
        var merged = new VfxAbilityComposition(
            item.Clip.OwnerPathHash,
            item.Clip.OwnerClassHash,
            1f,
            0f,
            passTime,
            particleEvents.OrderBy(cue => cue.Event.StartFrame).ToArray(),
            item.Name,
            item.Clip.AnimationFilePath,
            item.Clip.GraphPathHash,
            item.Clip.ChildClipHashes)
        {
            ResolvedCount = resolvedVfx
        };

        return item with
        {
            FilePath = playlist[0].Path,
            Duration = passTime,
            AnimationAsset = AnimationGraphPlayback.CreatePlaylist(steps),
            Composition = merged,
            TimedCues = timedCues.OrderBy(cue => cue.AtSeconds).ToArray(),
            EventCount = eventCount,
            HasVfx = resolvedVfx > 0 || resolvedIdleVfx > 0,
            VfxSummary = $"{resolvedVfx} VFX · {resolvedIdleVfx} idle · {eventCount} events"
        };
    }

    private IAnimationAsset GetOrLoadAsset(string path, CancellationToken cancellationToken)
    {
        lock (_assetGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_assets.TryGetValue(path, out IAnimationAsset cached))
                return cached;
        }

        cancellationToken.ThrowIfCancellationRequested();
        IAnimationAsset loaded;
        using (var stream = File.OpenRead(path))
            loaded = AnimationAsset.Load(stream);

        if (cancellationToken.IsCancellationRequested)
        {
            loaded.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }

        lock (_assetGate)
        {
            if (_disposed)
            {
                loaded.Dispose();
                throw new ObjectDisposedException(nameof(VfxClipCatalog));
            }
            if (_assets.TryGetValue(path, out IAnimationAsset cached))
            {
                loaded.Dispose();
                return cached;
            }
            _assets[path] = loaded;
            return loaded;
        }
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
        lock (_assetGate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (IAnimationAsset asset in _assets.Values) asset.Dispose();
            _assets.Clear();
        }
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
