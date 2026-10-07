using System;
using System.Collections.Generic;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    public sealed partial class VfxPlaybackGraphRuntime
    {
        public void Update(float deltaTime)
        {
            if (deltaTime <= 0f || !float.IsFinite(deltaTime)) return;
            // Source.time is the driver's global simulation clock and does not rewind at a
            // rig-loop replay. The root runtime's CurrentTime is the local age of this pass.
            _sourceTime += deltaTime;
            // Match LTK's variable driver: one graph step per advance. Seek/replay supplies
            // repeated 1/60 s advances from the session instead of being subdivided here.
            UpdateStep(deltaTime);
        }

        private void UpdateStep(float deltaTime)
        {
            // LTK steps the opened/root system first, then walks its child lineage recursively.
            // Each child subtree is advanced and reaped before the parent consumes this step's
            // child births, so the shared lineage budget is observed in the same depth-first order.
            Root.UpdateAtTime(deltaTime, _sourceTime);
            StepChildrenOf(Root, deltaTime);

            if (_pendingChildren.Count > 0)
            {
                foreach (VfxPlaybackRuntime pending in _pendingChildren)
                {
                    _runtimes.Add(pending);
                    _activeRuntimes.Add(pending);
                }
                _pendingChildren.Clear();
            }

            SyncRenderTimes();
        }

        private void StepChildrenOf(VfxPlaybackRuntime parent, float deltaTime)
        {
            int parentDepth = _depth.TryGetValue(parent, out int depth) ? depth : 0;
            List<VfxPlaybackRuntime> children = _childStepScratch[Math.Clamp(parentDepth, 0, MaximumGraphDepth)];
            children.Clear();
            if (_childrenByParent.TryGetValue(parent, out List<VfxPlaybackRuntime> directChildren))
            {
                // Snapshot the direct list because descendants can be reaped while this depth is
                // walking. Newly spawned children remain pending until the next graph step.
                for (int index = 0; index < directChildren.Count; index++)
                {
                    VfxPlaybackRuntime child = directChildren[index];
                    if (_activeRuntimes.Contains(child))
                        children.Add(child);
                }
            }

            for (int index = 0; index < children.Count; index++)
            {
                VfxPlaybackRuntime child = children[index];
                if (_looseChildStopAfter.TryGetValue(child, out float stopAfter) &&
                    child.CurrentTime + deltaTime >= stopAfter)
                {
                    child.IsStopped = true;
                }

                child.UpdateAtTime(deltaTime, _sourceTime);
                StepChildrenOf(child, deltaTime);
            }

            for (int index = children.Count - 1; index >= 0; index--)
            {
                VfxPlaybackRuntime child = children[index];
                if (!_activeRuntimes.Contains(child) || !child.IsComplete || HasLiveChildren(child)) continue;
                child.ParticleLifecycle -= OnParticleLifecycle;
                child.ParticleUpdated -= OnParticleUpdated;
                Forget(child);
                _runtimes.Remove(child);
            }

            children.Clear();
            ProcessSpawnRequests(parent);
        }

        private void ProcessSpawnRequests(VfxPlaybackRuntime parent)
        {
            if (!_spawnRequestsByParent.TryGetValue(parent, out List<ChildSpawnRequest> requests) ||
                requests.Count == 0)
            {
                return;
            }

            // LTK keeps births local to one Children runtime. Preserve the same stable tie break
            // while avoiding a global request scan for every parent in the graph.
            requests.Sort(CompareSpawnRequests);
            try
            {
                foreach (ChildSpawnRequest birth in requests)
                    SpawnChildren(parent, birth.Set, birth.Particle, birth.Carried);
            }
            finally
            {
                requests.Clear();
            }
        }

        private static int CompareSpawnRequests(ChildSpawnRequest left, ChildSpawnRequest right)
        {
            int order = left.Carried.CompareTo(right.Carried);
            if (order != 0) return order;
            order = left.Particle.Serial.CompareTo(right.Particle.Serial);
            return order != 0 ? order : left.Sequence.CompareTo(right.Sequence);
        }

        private bool HasLiveChildren(VfxPlaybackRuntime runtime)
            => _childrenByParent.TryGetValue(runtime, out List<VfxPlaybackRuntime> children) && children.Count > 0;

        private void RegisterParent(VfxPlaybackRuntime child, VfxPlaybackRuntime parent)
        {
            _parents[child] = parent;
            if (!_childrenByParent.TryGetValue(parent, out List<VfxPlaybackRuntime> children))
            {
                children = new List<VfxPlaybackRuntime>();
                _childrenByParent[parent] = children;
            }
            children.Add(child);
        }

        private void SyncRenderTimes()
        {
            foreach (VfxPlaybackRuntime runtime in _runtimes)
                SyncRenderTime(runtime);
            foreach (VfxPlaybackRuntime runtime in _pendingChildren)
                SyncRenderTime(runtime);
        }

        private void SyncRenderTime(VfxPlaybackRuntime runtime)
        {
            foreach (VfxPlaybackRuntime.EmitterState emitter in runtime.Emitters)
            {
                if (emitter.RenderTime != _sourceTime) emitter.InvalidateInstances();
                emitter.RenderTime = _sourceTime;
            }
        }
    }
}
