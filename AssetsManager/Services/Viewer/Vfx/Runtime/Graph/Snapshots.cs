using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    public sealed partial class VfxPlaybackGraphRuntime
    {
        internal Snapshot CaptureSnapshot()
        {
            var all = new List<VfxPlaybackRuntime>(_runtimes.Count + _pendingChildren.Count);
            all.AddRange(_runtimes);
            all.AddRange(_pendingChildren);
            var indices = new Dictionary<VfxPlaybackRuntime, int>(ReferenceEqualityComparer.Instance);
            for (int index = 0; index < all.Count; index++)
                indices[all[index]] = index;

            var saved = new RuntimeSnapshot[all.Count];
            long bytes = 256;

            for (int index = 0; index < all.Count; index++)
            {
                VfxPlaybackRuntime runtime = all[index];
                VfxPlaybackRuntime.Snapshot state = runtime.CaptureSnapshot();
                bool pending = index >= _runtimes.Count;
                saved[index] = new RuntimeSnapshot(
                    runtime.Definition,
                    _localTransforms.GetValueOrDefault(runtime, Matrix4x4.Identity),
                    _depth.GetValueOrDefault(runtime, 0),
                    _paths.GetValueOrDefault(runtime, string.Empty),
                    _parents.TryGetValue(runtime, out VfxPlaybackRuntime parent) && indices.TryGetValue(parent, out int parentIndex)
                        ? parentIndex
                        : -1,
                    runtime.Seed,
                    runtime.InitialRandomState,
                    runtime.ParticleCapacity,
                    _looseChildStopAfter.TryGetValue(runtime, out float stopAfter) ? stopAfter : null,
                    pending,
                    state);
                bytes += 256L + state.Bytes;
            }

            var carried = new List<CarriedSnapshot>();
            foreach (var (key, children) in _carriedChildren)
            {
                if (!indices.TryGetValue(key.Parent, out int parentIndex)) continue;
                foreach (CarriedChildInfo child in children)
                {
                    if (!indices.TryGetValue(child.Runtime, out int childIndex)) continue;
                    carried.Add(new CarriedSnapshot(
                        parentIndex,
                        key.SourceOrder,
                        key.Serial,
                        childIndex,
                        child.Definition,
                        child.Set,
                        child.Slot,
                        child.BoneName));
                }
            }
            bytes += carried.Count * 192L;

            var deathSeen = new List<DeathSeenSnapshot>();
            foreach (var (key, state) in _deathSeen)
            {
                if (!indices.TryGetValue(key.Parent, out int parentIndex)) continue;
                deathSeen.Add(new DeathSeenSnapshot(
                    parentIndex,
                    key.SourceOrder,
                    key.Serial,
                    state));
            }
            bytes += deathSeen.Count * 160L;

            return new Snapshot(
                _rootTransform,
                _orientationRootTransform,
                _sourceTime,
                saved,
                carried.ToArray(),
                deathSeen.ToArray(),
                bytes);
        }

        internal void RestoreSnapshot(Snapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.Runtimes.Length == 0)
                throw new InvalidOperationException("VFX graph snapshot does not contain its root runtime.");

            foreach (VfxPlaybackRuntime runtime in _runtimes.Skip(1).Concat(_pendingChildren).ToArray())
            {
                runtime.ParticleLifecycle -= OnParticleLifecycle;
                runtime.ParticleUpdated -= OnParticleUpdated;
            }

            _runtimes.Clear();
            _activeRuntimes.Clear();
            _pendingChildren.Clear();
            _depth.Clear();
            _localTransforms.Clear();
            _paths.Clear();
            _parents.Clear();
            _childrenByParent.Clear();
            _childCapacities.Clear();
            _looseChildStopAfter.Clear();
            _heldChildParticleCapacity = 0;
            _carriedChildren.Clear();
            _deathSeen.Clear();
            _spawnRequestsByParent.Clear();
            _rootTransform = snapshot.RootTransform;
            _orientationRootTransform = snapshot.OrientationRootTransform;
            _sourceTime = snapshot.SourceTime;

            RuntimeSnapshot rootSaved = snapshot.Runtimes[0];
            if (!ReferenceEquals(rootSaved.Definition, Root.Definition) &&
                rootSaved.Definition?.PathHash != Root.Definition?.PathHash)
            {
                throw new InvalidOperationException("VFX graph snapshot root definition no longer matches this graph.");
            }
            Root.ParticleLifecycle -= OnParticleLifecycle;
            Root.ParticleLifecycle += OnParticleLifecycle;
            Root.ParticleUpdated -= OnParticleUpdated;
            Root.ParticleUpdated += OnParticleUpdated;
            Root.RestoreSnapshot(rootSaved.State);
            _runtimes.Add(Root);
            _activeRuntimes.Add(Root);
            _depth[Root] = rootSaved.Depth;
            _localTransforms[Root] = rootSaved.LocalTransform;
            _paths[Root] = rootSaved.Path;
            AssignRenderIdentity(Root, rootSaved.Path);

            var restored = new VfxPlaybackRuntime[snapshot.Runtimes.Length];
            restored[0] = Root;
            for (int index = 1; index < snapshot.Runtimes.Length; index++)
            {
                RuntimeSnapshot saved = snapshot.Runtimes[index];
                VfxPlaybackRuntime runtime = RestoreRuntime(saved);
                restored[index] = runtime;
                if (saved.Pending)
                {
                    _pendingChildren.Add(runtime);
                }
                else
                {
                    _runtimes.Add(runtime);
                    _activeRuntimes.Add(runtime);
                }
            }

            for (int index = 1; index < snapshot.Runtimes.Length; index++)
            {
                int parentIndex = snapshot.Runtimes[index].ParentIndex;
                if ((uint)parentIndex < (uint)restored.Length)
                    RegisterParent(restored[index], restored[parentIndex]);
            }

            foreach (CarriedSnapshot saved in snapshot.CarriedChildren)
            {
                if ((uint)saved.ParentIndex >= (uint)restored.Length ||
                    (uint)saved.ChildIndex >= (uint)restored.Length) continue;
                var key = (restored[saved.ParentIndex], saved.SourceOrder, saved.Serial);
                if (!_carriedChildren.TryGetValue(key, out List<CarriedChildInfo> list))
                {
                    list = new List<CarriedChildInfo>();
                    _carriedChildren[key] = list;
                }
                list.Add(new CarriedChildInfo
                {
                    Runtime = restored[saved.ChildIndex],
                    Definition = saved.Definition,
                    Set = saved.Set,
                    Slot = saved.Slot,
                    BoneName = saved.BoneName
                });
            }

            foreach (DeathSeenSnapshot saved in snapshot.DeathSeen)
            {
                if ((uint)saved.ParentIndex >= (uint)restored.Length) continue;
                _deathSeen[(restored[saved.ParentIndex], saved.SourceOrder, saved.Serial)] = saved.State;
            }

            // Preview visibility is UI state, not simulation state. A rewind must not undo
            // the user's current root mute/solo state; restored descendants inherit it live.
            foreach (VfxPlaybackRuntime runtime in _runtimes.Concat(_pendingChildren))
                ApplyVisibility(runtime);
            SyncRenderTimes();
        }

        private VfxPlaybackRuntime RestoreRuntime(RuntimeSnapshot saved)
        {
            VfxPlaybackRuntime runtime = _runtimeFactory(
                saved.Definition,
                saved.LocalTransform * _rootTransform,
                saved.Seed);
            runtime.SetTransform(
                saved.LocalTransform * _rootTransform,
                saved.LocalTransform * _orientationRootTransform);
            runtime.SetEmissionSurfaces(_emissionSurfaces, replayCurrentTime: false);
            runtime.SetInitialRandomState(saved.InitialRandomState);
            runtime.SetParticleCapacity(saved.ParticleCapacity);
            runtime.ParticleLifecycle += OnParticleLifecycle;
            runtime.ParticleUpdated += OnParticleUpdated;
            _depth[runtime] = saved.Depth;
            _localTransforms[runtime] = saved.LocalTransform;
            _paths[runtime] = saved.Path;
            AssignRenderIdentity(runtime, saved.Path);
            RegisterChildCapacity(runtime, saved.ParticleCapacity);
            if (saved.LooseStopAfterSeconds.HasValue)
                _looseChildStopAfter[runtime] = saved.LooseStopAfterSeconds.Value;
            runtime.RestoreSnapshot(saved.State);
            return runtime;
        }
    }
}
