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

/// <summary>Owns animation assets and builds graph clip previews with their authored particle timeline.</summary>
internal sealed class VfxClipCatalog : IDisposable
{
    private readonly Dictionary<string, IAnimationAsset> _assets = new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<VfxAnimationItem> Build(VfxLoadingService.Bundle bundle, Func<string, string> resolve, LogService log)
    {
        var items = new List<VfxAnimationItem>();
        foreach (var clip in bundle.Clips)
        {
            var playlist = ResolvePlaylist(clip, bundle.Clips);
            if (playlist.Count == 0) continue;
            var steps = new List<IAnimationAsset>();
            var events = new List<VfxCompositionEvent>();
            float start = 0;
            bool missing = false;
            foreach (var atomic in playlist)
            {
                string path = resolve(atomic.AnimationFilePath);
                if (path == null) { missing = true; break; }
                if (!_assets.TryGetValue(path, out var asset))
                {
                    try
                    {
                        using var stream = File.OpenRead(path);
                        asset = AnimationAsset.Load(stream);
                        _assets[path] = asset;
                    }
                    catch (Exception ex)
                    {
                        log?.LogError(ex, $"Failed to load VFX clip animation: {path}");
                        missing = true;
                        break;
                    }
                }
                steps.Add(asset);
                var composition = VfxAbilityCompositionBuilder.Build(atomic, bundle.Systems, bundle.ResourceMap);
                float tick = atomic.TickDuration > 0 ? atomic.TickDuration : 1f / (asset.Fps > 0 ? asset.Fps : 30f);
                foreach (var cue in composition.Events)
                    events.Add(cue with { Event = cue.Event with
                    {
                        StartFrame = start + cue.Event.StartFrame * tick,
                        EndFrame = cue.Event.EndFrame < 0 ? -1 : start + cue.Event.EndFrame * tick
                    } });
                start += asset.Duration;
            }
            if (missing)
            {
                log?.LogWarning($"VFX clip 0x{clip.OwnerPathHash:x8} has an unavailable animation dependency.");
                continue;
            }
            string name = clip.ClipName ?? Path.GetFileNameWithoutExtension(playlist[0].AnimationFilePath);
            int resolved = events.Count(cue => cue.System != null && !cue.Event.IsKillEvent);
            var merged = new VfxAbilityComposition(clip.OwnerPathHash, clip.OwnerClassHash, 1, 0, start,
                events.OrderBy(cue => cue.Event.StartFrame).ToArray(), name, clip.AnimationFilePath,
                clip.GraphPathHash, clip.ChildClipHashes) { ResolvedCount = events.Count(cue => cue.System != null) };
            items.Add(new VfxAnimationItem
            {
                Name = name,
                DisplayName = $"{name} · {clip.OwnerPathHash:x8}",
                FilePath = playlist[0].AnimationFilePath,
                Duration = start,
                AnimationAsset = new ClipPlaylist(steps),
                Composition = merged,
                HasVfx = resolved > 0 || bundle.IdleEffects.Count > 0,
                VfxSummary = $"{resolved} VFX"
            });
        }
        return items.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static IReadOnlyList<VfxEventSequenceDefinition> ResolvePlaylist(
        VfxEventSequenceDefinition clip, IReadOnlyList<VfxEventSequenceDefinition> clips)
    {
        var byKey = clips.Where(item => item.GraphPathHash == clip.GraphPathHash)
            .GroupBy(item => item.OwnerPathHash).ToDictionary(group => group.Key, group => group.First());
        var path = new HashSet<uint>();
        var result = new List<VfxEventSequenceDefinition>();
        void Visit(VfxEventSequenceDefinition current)
        {
            if (!path.Add(current.OwnerPathHash)) return;
            if (!string.IsNullOrWhiteSpace(current.AnimationFilePath)) result.Add(current);
            else foreach (uint child in current.ChildClipHashes ?? Array.Empty<uint>())
            {
                int before = result.Count;
                if (byKey.TryGetValue(child, out var next)) Visit(next);
                if (result.Count > before && current.OwnerClassHash != Fnv1a.HashLower("SequencerClipData")) break;
            }
            path.Remove(current.OwnerPathHash);
        }
        Visit(clip);
        return result;
    }

    public void Dispose()
    {
        foreach (var asset in _assets.Values) asset.Dispose();
        _assets.Clear();
    }

    private sealed class ClipPlaylist(IReadOnlyList<IAnimationAsset> steps) : IAnimationAsset
    {
        public float Duration { get; } = steps.Sum(step => step.Duration);
        public float Fps => steps.Count > 0 ? steps[0].Fps : 30;
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
        public void Evaluate(float time, IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose)
        {
            if (IsDisposed) return;
            float remaining = Math.Clamp(time, 0, Duration);
            for (int index = 0; index < steps.Count; index++)
            {
                var step = steps[index];
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
