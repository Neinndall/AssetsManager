using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Runtime
{
    /// <summary>
    /// One set of placements that share the same authored animation name.
    /// LTK memoizes this grouping, so it is built once with the skin runtime rather than per frame.
    /// </summary>
    internal readonly record struct MapCharacterRuntimePlacement(
        MapCharacterData Placement,
        Matrix4x4 World,
        Vector3 Position);

    internal sealed record MapCharacterAnimationPlacementGroup(
        string Animation,
        IReadOnlyList<MapCharacterRuntimePlacement> Placements);

    /// <summary>
    /// One loaded MAP character skin, its shared animation evaluator and every placement that wears it.
    /// </summary>
    internal sealed class MapCharacterRuntimeGroup : IDisposable
    {
        internal MapCharacterRuntimeGroup(
            MapCharacterAssetData asset,
            MapCharacterAnimationRuntime animation,
            IReadOnlyList<MapCharacterData> placements)
        {
            Asset = asset;
            Animation = animation;
            Placements = placements ?? Array.Empty<MapCharacterData>();
            AnimationGroups = GroupByAnimation(Placements, asset?.Skin?.Scale ?? 1f);
        }

        internal MapCharacterAssetData Asset { get; }
        internal MapCharacterAnimationRuntime Animation { get; }
        internal IReadOnlyList<MapCharacterData> Placements { get; }
        internal IReadOnlyList<MapCharacterAnimationPlacementGroup> AnimationGroups { get; }

        public void Dispose() => Animation?.Dispose();

        internal static IReadOnlyList<MapCharacterAnimationPlacementGroup> GroupByAnimation(
            IReadOnlyList<MapCharacterData> placements,
            float skinScale)
        {
            if (placements == null || placements.Count == 0)
                return Array.Empty<MapCharacterAnimationPlacementGroup>();

            var order = new List<string>();
            var groups = new Dictionary<string, List<MapCharacterRuntimePlacement>>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < placements.Count; index++)
            {
                MapCharacterData placement = placements[index];
                if (placement == null) continue;

                string key = placement.Animation ?? string.Empty;
                if (!groups.TryGetValue(key, out List<MapCharacterRuntimePlacement> group))
                {
                    group = new List<MapCharacterRuntimePlacement>();
                    groups.Add(key, group);
                    order.Add(key);
                }

                Matrix4x4 world = MapCharacterSemantics.WorldTransform(placement.Transform, skinScale);
                group.Add(new MapCharacterRuntimePlacement(placement, world, world.Translation));
            }

            var result = new MapCharacterAnimationPlacementGroup[order.Count];
            for (int index = 0; index < order.Count; index++)
            {
                string key = order[index];
                result[index] = new MapCharacterAnimationPlacementGroup(key, groups[key].ToArray());
            }
            return result;
        }
    }
}
