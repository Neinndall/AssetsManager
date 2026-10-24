using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Session
{
    public sealed partial class VfxRenderSession
    {
        private readonly List<VfxPlaybackGraphRuntime> _persistentGraphs = new();
        private readonly HashSet<int> _persistentActive = new();
        private readonly Dictionary<int, List<VfxPlaybackGraphRuntime>> _persistentCache = new();

        private void ClearPersistentEffects()
        {
            _persistentGraphs.Clear();
            _persistentActive.Clear();
            _persistentCache.Clear();
        }

        /// <summary>Applies the preview's current conditions and reseeks changed effects on the session clock.</summary>
        internal void SetOwnerGameState(GameMaterialState state)
        {
            if (_usesStandaloneRig || _activeSystem == null) return;
            state ??= GameMaterialState.Resting with { Gear = -1 };
            var conditions = _ownerSceneContext?.SubmeshConditions ?? Array.Empty<VfxSubmeshCondition>();
            var active = new HashSet<int>();
            for (int index = 0; index < conditions.Count; index++)
                if (conditions[index].Effects.Count > 0 && VfxPersistentEffectSemantics.Active(conditions[index], index, state))
                    active.Add(index);
            if (_persistentActive.SetEquals(active)) return;

            foreach (var graph in _persistentGraphs)
            {
                _graphs.Remove(graph);
                graph.Reset();
            }
            _persistentGraphs.Clear();
            _persistentActive.Clear();
            _persistentActive.UnionWith(active);
            int insertion = _graphs.TakeWhile(graph => _graphAttachments.TryGetValue(graph, out var attachment)
                && attachment.IsIdleEffect).Count();
            // Rebuild in authored order, independent of the order in which preview switches changed.
            for (int index = 0; index < conditions.Count; index++)
            {
                if (!active.Contains(index)) continue;
                if (_persistentCache.TryGetValue(index, out var cached))
                {
                    _graphs.AddRange(cached);
                    _persistentGraphs.AddRange(cached);
                    continue;
                }
                int start = _graphs.Count;
                AddIdleEffects(conditions[index].Effects, _activeSystem.SystemCatalog, _activeSystem.ResourceMap,
                    _activeSystem.SearchDirectory, _ownerSceneContext);
                var created = _graphs.Skip(start).ToList();
                _persistentCache.Add(index, created);
                _persistentGraphs.AddRange(created);
            }
            foreach (var graph in _persistentGraphs) _graphs.Remove(graph);
            _graphs.InsertRange(insertion, _persistentGraphs);
            _graph = _graphs.FirstOrDefault();
            // Keep each condition's resources for this session. A global purge would invalidate
            // GPU handles still owned by idle/cue graphs whose pending uploads are already consumed.
            // Snapshots address graphs by position: a changed layout must never restore an older layout.
            ReplayAfterLineageResourceChange();
        }
    }
}
