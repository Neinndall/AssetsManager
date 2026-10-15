using System;
using System.Collections.Generic;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    public sealed partial class VfxPlaybackGraphRuntime
    {
        public void Update(float deltaTime)
            => Update(deltaTime, 0d, null);

        internal void Update(float deltaTime, double frameEndTime, Action<VfxPlaybackGraphRuntime, double> prepare)
        {
            if (deltaTime <= 0f || !float.IsFinite(deltaTime)) return;
            var steps = _stepper.Advance(deltaTime, Root.Definition.Flags, EffectsQuality);
            for (int at = 0; at < steps.Count; at++)
            {
                float dt = steps[at];
                prepare?.Invoke(this, frameEndTime - _stepper.Bank - (steps.Count - at - 1) * (double)dt);
                _sourceTime += dt;
                UpdateStep(dt);
            }
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

                if (parent.Emitters.Count > 0) child.SetTarget(parent.Emitters[0].SystemTarget);
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
