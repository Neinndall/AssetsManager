using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Composition
{
    internal static class AnimationGraphInspectorBuilder
    {
        private const string ClipDataSuffix = "ClipData";

        internal static IReadOnlyList<AnimationGraphClipInspectorItem> BuildClips(
            AnimationGraphDefinition graph,
            IEnumerable<AnimationClipCatalogItem> catalogItems)
        {
            if (graph?.Clips == null || graph.Clips.Count == 0)
                return Array.Empty<AnimationGraphClipInspectorItem>();

            var catalog = (catalogItems ?? Array.Empty<AnimationClipCatalogItem>())
                .Where(item => item?.Clip != null)
                .GroupBy(item => (item.Clip.GraphPathHash, item.Clip.OwnerPathHash))
                .ToDictionary(group => group.Key, group => group.First());

            var playable = new HashSet<uint>(catalog
                .Where(pair => pair.Key.GraphPathHash == graph.PathHash)
                .Select(pair => pair.Key.OwnerPathHash));

            return graph.Clips
                .Select(clip =>
                {
                    catalog.TryGetValue((graph.PathHash, clip.OwnerPathHash), out AnimationClipCatalogItem catalogItem);
                    return BuildClip(clip, catalogItem, playable);
                })
                .OrderByDescending(item => IsResolvedName(item.Name, item.Hash))
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        internal static IReadOnlyList<AnimationGraphClipInspectorItem> FilterClips(
            IEnumerable<AnimationGraphClipInspectorItem> clips,
            string filter)
        {
            string wanted = filter?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(wanted))
                return (clips ?? Array.Empty<AnimationGraphClipInspectorItem>()).ToArray();

            return (clips ?? Array.Empty<AnimationGraphClipInspectorItem>())
                .Where(item => item.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        internal static IReadOnlyList<AnimationMaskInspectorItem> BuildMasks(AnimationGraphDefinition graph)
            => (graph?.Masks ?? Array.Empty<AnimationMaskDefinition>())
                .Select(mask => new AnimationMaskInspectorItem(
                    mask,
                    mask.Weights?.Count(weight => float.IsFinite(weight) && weight > 0f) ?? 0))
                .ToArray();

        internal static IReadOnlyList<AnimationMaskJointInspectorItem> BuildMaskJoints(
            AnimationMaskDefinition mask,
            IReadOnlyList<string> jointNames)
        {
            if (mask?.Weights == null || mask.Weights.Count == 0)
                return Array.Empty<AnimationMaskJointInspectorItem>();

            var rows = new List<AnimationMaskJointInspectorItem>();
            for (int slot = 0; slot < mask.Weights.Count; slot++)
            {
                float weight = mask.Weights[slot];
                if (!float.IsFinite(weight) || weight <= 0f) continue;

                string jointName = jointNames != null && slot < jointNames.Count
                    ? jointNames[slot] ?? string.Empty
                    : string.Empty;
                rows.Add(new AnimationMaskJointInspectorItem(slot, jointName, weight));
            }
            return rows;
        }

        internal static string ClipKind(AnimationClipDefinition clip)
        {
            string className = clip?.ClassName;
            if (string.IsNullOrWhiteSpace(className))
                return clip == null ? string.Empty : $"0x{clip.OwnerClassHash:x8}";
            return className.EndsWith(ClipDataSuffix, StringComparison.Ordinal)
                ? className[..^ClipDataSuffix.Length]
                : className;
        }

        internal static string RateText(AnimationClipDefinition clip, AnimationClipCatalogItem catalogItem)
        {
            if (clip == null || string.IsNullOrWhiteSpace(clip.AnimationFilePath))
                return string.Empty;

            string fileRate = catalogItem?.AnimationAsset is { Fps: > 0 } asset && float.IsFinite(asset.Fps)
                ? MathF.Round(asset.Fps).ToString("0")
                : "-";
            string tickRate = clip.TickDuration > 0f && float.IsFinite(clip.TickDuration)
                ? MathF.Round(1f / clip.TickDuration).ToString("0")
                : string.Empty;
            return string.IsNullOrEmpty(tickRate) ? fileRate : $"{fileRate}/{tickRate}";
        }

        private static AnimationGraphClipInspectorItem BuildClip(
            AnimationClipDefinition clip,
            AnimationClipCatalogItem catalogItem,
            IReadOnlySet<uint> playable)
        {
            IReadOnlyList<AnimationGraphKeyReference> references =
                clip.ChildReferences ?? Array.Empty<AnimationGraphKeyReference>();
            IReadOnlyList<float?> parameters = clip.ParametricValues ?? Array.Empty<float?>();
            var children = new List<AnimationGraphChildInspectorItem>(references.Count);
            for (int index = 0; index < references.Count; index++)
            {
                AnimationGraphKeyReference child = references[index];
                if (child == null) continue;
                children.Add(new AnimationGraphChildInspectorItem(
                    child.Hash,
                    child.Name,
                    child.Declared,
                    index < parameters.Count ? parameters[index] : null,
                    child.Declared && playable.Contains(child.Hash)));
            }

            return new AnimationGraphClipInspectorItem(
                clip,
                catalogItem,
                clip.ClipName ?? $"0x{clip.OwnerPathHash:x8}",
                ClipKind(clip),
                RateText(clip, catalogItem),
                clip.Track?.Name ?? string.Empty,
                clip.Track?.Declared ?? true,
                clip.Mask?.Name ?? string.Empty,
                clip.Mask?.Declared ?? true,
                clip.SyncGroup?.Name ?? string.Empty,
                clip.SyncGroup?.Declared ?? true,
                clip.Events?.Count ?? 0,
                children,
                BuildEvents(clip.Events));
        }

        private static IReadOnlyList<AnimationGraphEventInspectorItem> BuildEvents(
            IReadOnlyList<AnimationClipEventDefinition> events)
        {
            if (events == null || events.Count == 0)
                return Array.Empty<AnimationGraphEventInspectorItem>();

            return events.Select(item => new AnimationGraphEventInspectorItem(
                EventKind(item),
                item.EndFrame >= 0f
                    ? $"{item.StartFrame:0.###} → {item.EndFrame:0.###}"
                    : $"{item.StartFrame:0.###} → end",
                EventSummary(item))).ToArray();
        }

        private static string EventKind(AnimationClipEventDefinition item)
            => item switch
            {
                VfxParticleEventDefinition => "Particle",
                AnimationSubmeshVisibilityEventDefinition => "Submesh Visibility",
                AnimationJointSnapEventDefinition => "Joint Snap",
                AnimationConformToPathEventDefinition => "Conform To Path",
                AnimationOtherClipEventDefinition other => $"0x{other.ClassHash:x8}",
                _ => "Event"
            };

        private static string EventSummary(AnimationClipEventDefinition item)
            => item switch
            {
                VfxParticleEventDefinition particle =>
                    !string.IsNullOrWhiteSpace(particle.EffectName)
                        ? particle.EffectName
                        : $"effect 0x{particle.EffectKey:x8}",
                AnimationSubmeshVisibilityEventDefinition visibility =>
                    $"show {visibility.ShowSubmeshHashes?.Count ?? 0} · hide {visibility.HideSubmeshHashes?.Count ?? 0}",
                AnimationJointSnapEventDefinition snap =>
                    $"0x{snap.JointHash:x8} → 0x{snap.SnapToHash:x8}",
                AnimationConformToPathEventDefinition conform =>
                    $"mask 0x{conform.MaskHash:x8} · in {conform.BlendInSeconds:0.###}s · out {conform.BlendOutSeconds:0.###}s",
                AnimationOtherClipEventDefinition other => $"event 0x{other.EventHash:x8}",
                _ => $"event 0x{item.EventHash:x8}"
            };

        private static bool IsResolvedName(string name, uint hash)
            => !string.IsNullOrWhiteSpace(name) &&
               !name.Equals($"0x{hash:x8}", StringComparison.OrdinalIgnoreCase) &&
               !name.Equals(hash.ToString("x8"), StringComparison.OrdinalIgnoreCase);
    }
}
