using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Resources;
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
        private readonly SemaphoreSlim _vfxResourcesGate = new(1, 1);
        private VfxSceneResourceContext _vfxResources;
        private bool _disposed;
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
        internal AnimationClipDefinition PreviewClip { get; private set; }
        internal MapCharacterData PreviewPlacement { get; private set; }
        internal float PreviewTimeSeconds { get; set; }
        internal IReadOnlySet<uint> PreviewHiddenSubmeshes { get; private set; } = new HashSet<uint>();

        internal void SetPreviewClip(AnimationClipDefinition clip, MapCharacterData placement = null)
        {
            PreviewClip = clip;
            PreviewPlacement = placement ?? Placements?.FirstOrDefault();
            PreviewTimeSeconds = 0f;
            PreviewHiddenSubmeshes = new HashSet<uint>();
        }

        internal void SetPreviewHiddenSubmeshes(IReadOnlySet<uint> hidden)
            => PreviewHiddenSubmeshes = hidden ?? new HashSet<uint>();

        internal void ClearPreviewClip()
        {
            PreviewClip = null;
            PreviewPlacement = null;
            PreviewTimeSeconds = 0f;
            PreviewHiddenSubmeshes = new HashSet<uint>();
        }

        /// <summary>
        /// Owns one lazily materialized VFX resource overlay for this MAP Skin. Every clip and
        /// placement wearing the Skin reuses it; the overlay dies with the scene group, not with
        /// a transient browser selection.
        /// </summary>
        internal async Task<VfxSceneResourceContext> EnsureVfxResourcesAsync(
            Func<CancellationToken, Task<VfxSceneResourceContext>> create,
            Func<VfxSceneResourceContext, CancellationToken, Task> ensure,
            CancellationToken cancellationToken)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MapCharacterRuntimeGroup));
            ArgumentNullException.ThrowIfNull(create);

            await _vfxResourcesGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(MapCharacterRuntimeGroup));

                if (_vfxResources == null)
                {
                    VfxSceneResourceContext created = await create(cancellationToken).ConfigureAwait(false);
                    if (_disposed)
                    {
                        created?.Dispose();
                        throw new ObjectDisposedException(nameof(MapCharacterRuntimeGroup));
                    }
                    _vfxResources = created;
                }
                else if (ensure != null)
                {
                    await ensure(_vfxResources, cancellationToken).ConfigureAwait(false);
                    if (_disposed)
                        throw new ObjectDisposedException(nameof(MapCharacterRuntimeGroup));
                }

                return _vfxResources;
            }
            finally
            {
                _vfxResourcesGate.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Animation?.Dispose();
            _vfxResources?.Dispose();
            _vfxResources = null;
        }

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
