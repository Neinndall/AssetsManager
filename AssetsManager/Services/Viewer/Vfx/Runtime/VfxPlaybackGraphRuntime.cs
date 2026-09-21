using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    /// <summary>Executes one complete VFX graph, including particle-authored child systems.</summary>
    public sealed class VfxPlaybackGraphRuntime
    {
        // LTK bounds recursive particle children at four levels and 512 live child systems.
        // Child pools reserve a power-of-two capacity from 16..4096 against one 128K lineage budget.
        private const int MaximumGraphDepth = 4;
        private const int MaximumActiveChildSystems = 512;
        private const int MinimumChildParticleCapacity = 16;
        private const int MaximumChildParticleCapacity = 4096;
        private const int MaximumChildParticleCapacityBudget = 1 << 17;

        private readonly IReadOnlyDictionary<uint, VfxSystemDefinition> _systems;
        private readonly IReadOnlyDictionary<uint, uint> _resourceMap;
        private readonly Func<VfxSystemDefinition, Matrix4x4, int, VfxPlaybackRuntime> _runtimeFactory;
        private readonly List<VfxPlaybackRuntime> _runtimes = new();
        private readonly List<VfxPlaybackRuntime> _pendingChildren = new();
        private readonly Dictionary<VfxPlaybackRuntime, int> _depth = new();
        private readonly Dictionary<VfxPlaybackRuntime, Matrix4x4> _localTransforms = new();
        private readonly Dictionary<VfxPlaybackRuntime, string> _paths = new();
        private readonly Dictionary<VfxPlaybackRuntime, VfxPlaybackRuntime> _parents = new();
        private readonly Dictionary<VfxPlaybackRuntime, int> _childCapacities = new();
        private readonly Dictionary<VfxPlaybackRuntime, float> _looseChildStopAfter = new();
        private readonly Dictionary<(string Path, int SourceOrder), int> _renderRanks = new();
        private readonly Dictionary<(string Path, int SourceOrder), int> _renderRoots = new();
        private readonly Dictionary<int, bool> _rootVisibility = new();
        private readonly Dictionary<(VfxPlaybackRuntime Parent, int SourceOrder, uint Serial), List<CarriedChildInfo>> _carriedChildren = new();
        private readonly Dictionary<(VfxPlaybackRuntime Parent, int SourceOrder, uint Serial), VfxPlaybackRuntime.ParticleLifecycleInfo> _deathSeen = new();
        private readonly List<ChildSpawnRequest> _spawnRequests = new();
        private readonly List<ChildSpawnRequest> _spawnBatch = new();
        private readonly List<VfxPlaybackRuntime>[] _childStepScratch =
        {
            new(), new(), new(), new(), new()
        };
        private readonly int _initialSeed;
        private long _nextSpawnRequestSequence;
        private int _heldChildParticleCapacity;
        private Matrix4x4 _rootTransform;
        private Matrix4x4 _orientationRootTransform;
        private float _sourceTime;
        private Func<string, Matrix4x4?> _jointTransformProvider;
        private bool _allEmittersVisible = true;

        private sealed class CarriedChildInfo
        {
            public VfxPlaybackRuntime Runtime { get; init; }
            public VfxSystemDefinition Definition { get; init; }
            public VfxChildParticleSetDefinition Set { get; init; }
            public int Slot { get; init; }
            public string BoneName { get; init; }
        }

        private sealed record ChildSpawnRequest(
            VfxPlaybackRuntime Parent,
            VfxChildParticleSetDefinition Set,
            VfxPlaybackRuntime.ParticleLifecycleInfo Particle,
            bool Carried,
            long Sequence);

        internal sealed record RuntimeSnapshot(
            VfxSystemDefinition Definition,
            Matrix4x4 LocalTransform,
            int Depth,
            string Path,
            int ParentIndex,
            int Seed,
            uint InitialRandomState,
            int ParticleCapacity,
            float? LooseStopAfterSeconds,
            bool Pending,
            VfxPlaybackRuntime.Snapshot State);

        internal sealed record CarriedSnapshot(
            int ParentIndex,
            int SourceOrder,
            uint Serial,
            int ChildIndex,
            VfxSystemDefinition Definition,
            VfxChildParticleSetDefinition Set,
            int Slot,
            string BoneName);

        internal sealed record DeathSeenSnapshot(
            int ParentIndex,
            int SourceOrder,
            uint Serial,
            VfxPlaybackRuntime.ParticleLifecycleInfo State);

        internal sealed record Snapshot(
            Matrix4x4 RootTransform,
            Matrix4x4 OrientationRootTransform,
            float SourceTime,
            RuntimeSnapshot[] Runtimes,
            CarriedSnapshot[] CarriedChildren,
            DeathSeenSnapshot[] DeathSeen,
            long Bytes);

        public VfxPlaybackGraphRuntime(
            VfxSystemDefinition rootDefinition,
            Matrix4x4 rootTransform,
            int seed,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap,
            Func<VfxSystemDefinition, Matrix4x4, int, VfxPlaybackRuntime> runtimeFactory)
        {
            ArgumentNullException.ThrowIfNull(rootDefinition);
            _systems = systems ?? throw new ArgumentNullException(nameof(systems));
            _resourceMap = resourceMap ?? throw new ArgumentNullException(nameof(resourceMap));
            _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
            _initialSeed = seed;
            // The root definition transform is the outermost authored VFX factor in LTK.
            // The constructor receives the scene/world transform that follows it.
            Matrix4x4 rootDefinitionTransform =
                rootDefinition.Transform.GetValueOrDefault(Matrix4x4.Identity);
            _rootTransform = rootDefinitionTransform * rootTransform;
            _orientationRootTransform = _rootTransform;
            BuildRenderRanks(rootDefinition);

            Root = CreateRuntime(rootDefinition, Matrix4x4.Identity, 0, string.Empty, seed);
            _runtimes.Add(Root);
            SyncRenderTimes();
        }

        public VfxPlaybackRuntime Root { get; }
        public IReadOnlyList<VfxPlaybackRuntime> Runtimes => _runtimes;
        internal int InitialSeed => _initialSeed;
        internal int LiveChildSystemCount => Math.Max(0, _runtimes.Count - 1) + _pendingChildren.Count;
        internal int HeldChildParticleCapacity => _heldChildParticleCapacity;
        public bool IsComplete => _pendingChildren.Count == 0 && _runtimes.Count == 1 && Root.IsComplete;
        public object UserTag
        {
            get => Root.UserTag;
            set => Root.UserTag = value;
        }

        public bool IsStopped
        {
            get => Root.IsStopped;
            set => Root.IsStopped = value;
        }

        public void SetTransform(Matrix4x4 transform)
            => SetTransform(transform, transform);

        public void SetTransform(Matrix4x4 transform, Matrix4x4 orientationRootTransform)
        {
            _rootTransform = transform;
            _orientationRootTransform = orientationRootTransform;
            foreach (VfxPlaybackRuntime runtime in _runtimes)
            {
                Matrix4x4 local = _localTransforms[runtime];
                runtime.SetTransform(local * _rootTransform, local * _orientationRootTransform);
            }
        }
        public void SetTarget(Vector3 worldTarget)
        {
            foreach (VfxPlaybackRuntime runtime in _runtimes)
                runtime.SetTarget(worldTarget);
        }

        /// <summary>
        /// Provides the current character-joint transforms used by boneToSpawnAt child sets.
        /// The session normalizes/scales these transforms to the same rig space as the VFX graph.
        /// </summary>
        internal void SetJointTransformProvider(Func<string, Matrix4x4?> provider)
            => _jointTransformProvider = provider;

        public void SetStartDelay(float seconds) => Root.SetStartDelay(seconds);

        public bool SetEmitterVisibility(int rootSourceOrder, bool isVisible)
        {
            bool known = _renderRoots.Values.Contains(rootSourceOrder);
            if (!known) return false;

            _rootVisibility[rootSourceOrder] = isVisible;
            foreach (VfxPlaybackRuntime runtime in _runtimes.Concat(_pendingChildren))
            {
                foreach (VfxPlaybackRuntime.EmitterState emitter in runtime.Emitters)
                {
                    if (emitter.RenderRootSourceOrder == rootSourceOrder)
                        emitter.IsVisible = isVisible;
                }
            }
            return true;
        }

        public void SetAllEmittersVisible(bool isVisible)
        {
            _allEmittersVisible = isVisible;
            _rootVisibility.Clear();
            foreach (VfxPlaybackRuntime runtime in _runtimes.Concat(_pendingChildren))
                ApplyVisibility(runtime);
        }

        private bool RootIsVisible(int rootSourceOrder)
            => _rootVisibility.TryGetValue(rootSourceOrder, out bool visible)
                ? visible
                : _allEmittersVisible;

        private void ApplyVisibility(VfxPlaybackRuntime runtime)
        {
            foreach (VfxPlaybackRuntime.EmitterState emitter in runtime.Emitters)
                emitter.IsVisible = RootIsVisible(emitter.RenderRootSourceOrder);
        }

        public void Kill()
        {
            foreach (VfxPlaybackRuntime runtime in _runtimes)
            {
                runtime.ParticleLifecycle -= OnParticleLifecycle;
                runtime.ParticleUpdated -= OnParticleUpdated;
                runtime.Kill();
            }
            foreach (VfxPlaybackRuntime pending in _pendingChildren)
            {
                pending.ParticleLifecycle -= OnParticleLifecycle;
                pending.ParticleUpdated -= OnParticleUpdated;
                Forget(pending);
            }
            _pendingChildren.Clear();
            _carriedChildren.Clear();
            _deathSeen.Clear();
            _spawnRequests.Clear();
            for (int index = _runtimes.Count - 1; index > 0; index--)
            {
                Forget(_runtimes[index]);
                _runtimes.RemoveAt(index);
            }
        }

        public void Reset()
        {
            ClearChildRuns();
            RebindRootCallbacks();
            _sourceTime = 0f;
            Root.Reset();
            Root.WarmUp();
            SyncRenderTimes();
        }

        internal void ReplayLoop()
            => ReplayLoop(_rootTransform, _orientationRootTransform);

        internal void ReplayLoop(Matrix4x4 startTransform, Matrix4x4 startOrientationRootTransform)
        {
            ClearChildRuns();
            RebindRootCallbacks();

            // LTK replays from phase zero before it runs the wrapping frame. Put the root on
            // that start frame before resetting/warming so build-up and field origins do not
            // inherit the transform from the end of the previous pass.
            _rootTransform = startTransform;
            _orientationRootTransform = startOrientationRootTransform;
            Root.SetTransform(startTransform, startOrientationRootTransform);
            Root.ReplayLoop();
            Root.WarmUp();
            SyncRenderTimes();
        }

        private void ClearChildRuns()
        {
            for (int index = _runtimes.Count - 1; index > 0; index--)
            {
                VfxPlaybackRuntime runtime = _runtimes[index];
                runtime.ParticleLifecycle -= OnParticleLifecycle;
                runtime.ParticleUpdated -= OnParticleUpdated;
                Forget(runtime);
                _runtimes.RemoveAt(index);
            }
            foreach (VfxPlaybackRuntime pending in _pendingChildren)
            {
                pending.ParticleLifecycle -= OnParticleLifecycle;
                pending.ParticleUpdated -= OnParticleUpdated;
                Forget(pending);
            }
            _pendingChildren.Clear();
            _carriedChildren.Clear();
            _deathSeen.Clear();
            _spawnRequests.Clear();
        }

        private void RebindRootCallbacks()
        {
            Root.ParticleLifecycle -= OnParticleLifecycle;
            Root.ParticleLifecycle += OnParticleLifecycle;
            Root.ParticleUpdated -= OnParticleUpdated;
            Root.ParticleUpdated += OnParticleUpdated;
        }

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
            _pendingChildren.Clear();
            _depth.Clear();
            _localTransforms.Clear();
            _paths.Clear();
            _parents.Clear();
            _childCapacities.Clear();
            _looseChildStopAfter.Clear();
            _heldChildParticleCapacity = 0;
            _carriedChildren.Clear();
            _deathSeen.Clear();
            _spawnRequests.Clear();
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
                if (saved.Pending) _pendingChildren.Add(runtime);
                else _runtimes.Add(runtime);
            }

            for (int index = 1; index < snapshot.Runtimes.Length; index++)
            {
                int parentIndex = snapshot.Runtimes[index].ParentIndex;
                if ((uint)parentIndex < (uint)restored.Length)
                    _parents[restored[index]] = restored[parentIndex];
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

        private void Forget(VfxPlaybackRuntime runtime)
        {
            _depth.Remove(runtime);
            _localTransforms.Remove(runtime);
            _paths.Remove(runtime);
            _parents.Remove(runtime);
            _looseChildStopAfter.Remove(runtime);
            if (_childCapacities.Remove(runtime, out int capacity))
                _heldChildParticleCapacity = Math.Max(0, _heldChildParticleCapacity - capacity);

            foreach (var key in _carriedChildren.Keys.ToArray())
            {
                if (ReferenceEquals(key.Parent, runtime))
                {
                    _carriedChildren.Remove(key);
                    continue;
                }

                List<CarriedChildInfo> children = _carriedChildren[key];
                children.RemoveAll(child => ReferenceEquals(child.Runtime, runtime));
                if (children.Count == 0) _carriedChildren.Remove(key);
            }

            foreach (var key in _deathSeen.Keys.ToArray())
            {
                if (ReferenceEquals(key.Parent, runtime))
                    _deathSeen.Remove(key);
            }
        }

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
            Root.Update(deltaTime);
            StepChildrenOf(Root, deltaTime);

            if (_pendingChildren.Count > 0)
            {
                _runtimes.AddRange(_pendingChildren);
                _pendingChildren.Clear();
            }

            SyncRenderTimes();
        }

        private void StepChildrenOf(VfxPlaybackRuntime parent, float deltaTime)
        {
            int parentDepth = _depth.TryGetValue(parent, out int depth) ? depth : 0;
            List<VfxPlaybackRuntime> children = _childStepScratch[Math.Clamp(parentDepth, 0, MaximumGraphDepth)];
            children.Clear();
            foreach (VfxPlaybackRuntime runtime in _runtimes)
            {
                if (_parents.TryGetValue(runtime, out VfxPlaybackRuntime owner) && ReferenceEquals(owner, parent))
                    children.Add(runtime);
            }

            for (int index = 0; index < children.Count; index++)
            {
                VfxPlaybackRuntime child = children[index];
                if (_looseChildStopAfter.TryGetValue(child, out float stopAfter) &&
                    child.CurrentTime + deltaTime >= stopAfter)
                {
                    child.IsStopped = true;
                }

                child.Update(deltaTime);
                StepChildrenOf(child, deltaTime);
            }

            for (int index = children.Count - 1; index >= 0; index--)
            {
                VfxPlaybackRuntime child = children[index];
                if (!_runtimes.Contains(child) || !child.IsComplete || HasLiveChildren(child)) continue;
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
            _spawnBatch.Clear();
            int retained = 0;
            for (int index = 0; index < _spawnRequests.Count; index++)
            {
                ChildSpawnRequest request = _spawnRequests[index];
                if (ReferenceEquals(request.Parent, parent))
                {
                    _spawnBatch.Add(request);
                    continue;
                }

                if (retained != index)
                    _spawnRequests[retained] = request;
                retained++;
            }
            if (retained < _spawnRequests.Count)
                _spawnRequests.RemoveRange(retained, _spawnRequests.Count - retained);
            if (_spawnBatch.Count == 0) return;

            // Sequence is the previous LINQ stable-order tie break, but List.Sort avoids allocating
            // an ordered enumerable and keeps large birth batches O(n log n).
            _spawnBatch.Sort(CompareSpawnRequests);

            try
            {
                foreach (ChildSpawnRequest birth in _spawnBatch)
                    SpawnChildren(birth.Parent, birth.Set, birth.Particle, birth.Carried);
            }
            finally
            {
                _spawnBatch.Clear();
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
        {
            foreach (VfxPlaybackRuntime parent in _parents.Values)
            {
                if (ReferenceEquals(parent, runtime)) return true;
            }
            return false;
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
                emitter.RenderTime = _sourceTime;
        }

        private VfxPlaybackRuntime CreateRuntime(
            VfxSystemDefinition definition,
            Matrix4x4 localTransform,
            int depth,
            string path,
            int seed,
            uint? initialRandomState = null,
            int? particleCapacity = null)
        {
            Matrix4x4 effectiveLocalTransform = depth == 0
                ? Matrix4x4.Identity
                : ComposeChildTransform(definition.Transform.GetValueOrDefault(Matrix4x4.Identity), localTransform);
            VfxPlaybackRuntime runtime = _runtimeFactory(
                definition,
                effectiveLocalTransform * _rootTransform,
                seed);
            runtime.SetTransform(
                effectiveLocalTransform * _rootTransform,
                effectiveLocalTransform * _orientationRootTransform);
            if (particleCapacity.HasValue)
                runtime.SetParticleCapacity(particleCapacity.Value);
            if (initialRandomState.HasValue)
                runtime.SetInitialRandomState(initialRandomState.Value);
            runtime.ParticleLifecycle += OnParticleLifecycle;
            runtime.ParticleUpdated += OnParticleUpdated;
            _depth[runtime] = depth;
            _localTransforms[runtime] = effectiveLocalTransform;
            _paths[runtime] = path;
            AssignRenderIdentity(runtime, path);
            if (depth == 0) runtime.WarmUp();
            else runtime.SuppressBuildUp();
            return runtime;
        }

        private void BuildRenderRanks(VfxSystemDefinition rootDefinition)
        {
            _renderRanks.Clear();
            _renderRoots.Clear();
            int nextRank = 0;
            CollectRenderRanks(rootDefinition, string.Empty, 0, -1, ref nextRank);
        }

        private void CollectRenderRanks(
            VfxSystemDefinition definition,
            string path,
            int depth,
            int rootSourceOrder,
            ref int nextRank)
        {
            if (definition is null || depth > MaximumGraphDepth) return;

            string renderPath = path ?? string.Empty;
            int[] localOrder = Enumerable.Range(0, definition.Emitters.Count).ToArray();
            Array.Sort(localOrder, (left, right) => VfxDrawOrderSemantics.Compare(
                definition.Emitters[left],
                left,
                definition.Emitters[right],
                right));
            foreach (int sourceOrder in localOrder)
            {
                _renderRanks[(renderPath, sourceOrder)] = nextRank++;
                _renderRoots[(renderPath, sourceOrder)] = depth == 0 ? sourceOrder : rootSourceOrder;
            }

            if (depth >= MaximumGraphDepth) return;
            for (int sourceOrder = 0; sourceOrder < definition.Emitters.Count; sourceOrder++)
            {
                VfxEmitterDefinition emitter = definition.Emitters[sourceOrder];
                VfxChildParticleSetDefinition childSet = emitter.ChildParticleSet;
                if (emitter.Disabled || childSet is null || childSet.Children.Count == 0) continue;

                for (int slot = 0; slot < childSet.Children.Count; slot++)
                {
                    VfxChildSystemReference child = childSet.Children[slot];
                    VfxSystemDefinition childDefinition = ResolveSystem(
                        child,
                        _systems,
                        definition.ResourceMap ?? _resourceMap);
                    if (childDefinition is null) continue;

                    string emitterPath = string.IsNullOrEmpty(path)
                        ? sourceOrder.ToString()
                        : $"{path}/{sourceOrder}";
                    string childPath = $"{emitterPath}.{slot}";
                    int childRoot = depth == 0 ? sourceOrder : rootSourceOrder;
                    CollectRenderRanks(childDefinition, childPath, depth + 1, childRoot, ref nextRank);
                }
            }
        }

        private void AssignRenderIdentity(VfxPlaybackRuntime runtime, string path)
        {
            string renderPath = path ?? string.Empty;
            foreach (VfxPlaybackRuntime.EmitterState emitter in runtime.Emitters)
            {
                emitter.RenderGraphKey = this;
                emitter.RenderPath = renderPath;
                emitter.RenderRootSourceOrder = _renderRoots.GetValueOrDefault(
                    (renderPath, emitter.SourceOrder),
                    emitter.SourceOrder);
                emitter.RenderRank = _renderRanks.GetValueOrDefault((renderPath, emitter.SourceOrder), int.MaxValue);
                emitter.IsVisible = RootIsVisible(emitter.RenderRootSourceOrder);
            }
        }

        private void OnParticleLifecycle(
            VfxPlaybackRuntime parentRuntime,
            VfxEmitterDefinition emitter,
            VfxPlaybackRuntime.ParticleLifecycleInfo particle)
        {
            VfxChildParticleSetDefinition childSet = emitter.ChildParticleSet;
            if (childSet is null || childSet.Children.Count == 0) return;

            var key = (Parent: parentRuntime, particle.SourceOrder, particle.Serial);
            if (particle.Died)
            {
                if (!childSet.EmitOnDeath)
                {
                    StopCarriedChildren(key);
                    return;
                }

                if (_deathSeen.Remove(key, out VfxPlaybackRuntime.ParticleLifecycleInfo lastSeen))
                {
                    // LTK spawns an on-death child from the last live Seen record: both its
                    // bearing and its lifetime come from the last step where the particle was
                    // still present. A linger settle may rewrite lifetime on the death step,
                    // but that rewritten value was never observed by children.step.
                    VfxPlaybackRuntime.ParticleLifecycleInfo deathBearing = lastSeen with
                    {
                        ParticleTime = lastSeen.ParticleLifetime,
                        Died = true
                    };
                    QueueChildSpawn(parentRuntime, childSet, deathBearing, carried: false);
                }
                return;
            }

            if (childSet.EmitOnDeath)
            {
                _deathSeen[key] = particle;
                return;
            }

            QueueChildSpawn(parentRuntime, childSet, particle, carried: true);
        }

        private void QueueChildSpawn(
            VfxPlaybackRuntime parentRuntime,
            VfxChildParticleSetDefinition childSet,
            VfxPlaybackRuntime.ParticleLifecycleInfo particle,
            bool carried)
            => _spawnRequests.Add(new ChildSpawnRequest(
                parentRuntime,
                childSet,
                particle,
                carried,
                _nextSpawnRequestSequence++));

        private void OnParticleUpdated(
            VfxPlaybackRuntime parentRuntime,
            VfxEmitterDefinition emitter,
            VfxPlaybackRuntime.ParticleLifecycleInfo particle)
        {
            VfxChildParticleSetDefinition childSet = emitter.ChildParticleSet;
            if (childSet is null || childSet.Children.Count == 0) return;

            var key = (Parent: parentRuntime, particle.SourceOrder, particle.Serial);
            if (childSet.EmitOnDeath)
            {
                _deathSeen[key] = particle;
                return;
            }
            if (!_carriedChildren.TryGetValue(key, out List<CarriedChildInfo> children)) return;

            Matrix4x4 bearing = ChildBearing(childSet, particle);
            foreach (CarriedChildInfo child in children)
            {
                Matrix4x4 placement = bearing;
                if (!string.IsNullOrEmpty(child.BoneName))
                {
                    Matrix4x4? joint = _jointTransformProvider?.Invoke(child.BoneName);
                    if (!joint.HasValue) continue;
                    placement = ReRootOnJoint(bearing, joint.Value);
                }
                UpdateChildPlacement(child.Runtime, child.Definition, placement);
            }
        }

        private void StopCarriedChildren(
            (VfxPlaybackRuntime Parent, int SourceOrder, uint Serial) key)
        {
            if (!_carriedChildren.Remove(key, out List<CarriedChildInfo> children)) return;
            foreach (CarriedChildInfo child in children)
                child.Runtime.IsStopped = true;
        }

        private void SpawnChildren(
            VfxPlaybackRuntime parentRuntime,
            VfxChildParticleSetDefinition childSet,
            VfxPlaybackRuntime.ParticleLifecycleInfo particle,
            bool carried)
        {
            int parentDepth = _depth.TryGetValue(parentRuntime, out int value) ? value : 0;
            if (parentDepth >= MaximumGraphDepth) return;

            string parentPath = _paths.GetValueOrDefault(parentRuntime, string.Empty);
            string emitterPath = $"{(string.IsNullOrEmpty(parentPath) ? string.Empty : parentPath + "/")}{particle.SourceOrder}";
            Matrix4x4 bearing = ChildBearing(childSet, particle);
            IReadOnlyList<string> bones = childSet.Bones ?? Array.Empty<string>();

            // LTK treats a bone set as a positional list: it spawns one child per slot,
            // bypasses childrenProbability, and rejects the set when there are too few bones.
            if (bones.Count > 0)
            {
                if (bones.Count < childSet.Children.Count || _jointTransformProvider is null) return;
                for (int slot = 0; slot < childSet.Children.Count; slot++)
                {
                    if (LiveChildSystemCount >= MaximumActiveChildSystems) break;
                    string bone = bones[slot];
                    Matrix4x4? joint = _jointTransformProvider(bone);
                    if (!joint.HasValue) continue;
                    Matrix4x4 placement = ReRootOnJoint(bearing, joint.Value);
                    int childSeed = ChildSeed(_initialSeed, $"{emitterPath}.{slot}", particle.Serial);
                    SpawnChild(parentRuntime, childSet, particle, parentDepth, emitterPath, slot,
                        placement, childSeed, unchecked((uint)childSeed), carried, bone);
                }
                return;
            }

            if (LiveChildSystemCount >= MaximumActiveChildSystems) return;

            // A normal child uses one lineage stream both to select its probability slot and
            // to seed the child itself. Keep the post-selection RNG state for exact replay.
            int lineageSeed = ChildSeed(_initialSeed, emitterPath, particle.Serial);
            var rng = new VfxLtkRandom(unchecked((uint)lineageSeed));
            int selectedSlot;
            if (childSet.Children.Count == 1)
            {
                selectedSlot = 0;
            }
            else
            {
                float selected = childSet.Probability.Prob is { Length: > 0 } && !childSet.Probability.Prob[0].IsEmpty
                    ? childSet.Probability.SampleBirth(particle.ParticleTime, rng)
                    : childSet.Probability.Sample(particle.ParticleTime);
                selectedSlot = Math.Max(0, (int)MathF.Truncate(selected)) % childSet.Children.Count;
            }

            SpawnChild(parentRuntime, childSet, particle, parentDepth, emitterPath, selectedSlot,
                bearing, lineageSeed, rng.State, carried, null);
        }

        private void SpawnChild(
            VfxPlaybackRuntime parentRuntime,
            VfxChildParticleSetDefinition childSet,
            VfxPlaybackRuntime.ParticleLifecycleInfo particle,
            int parentDepth,
            string emitterPath,
            int slot,
            Matrix4x4 childWorldTransform,
            int childSeed,
            uint initialRandomState,
            bool carried,
            string boneName)
        {
            if ((uint)slot >= (uint)childSet.Children.Count) return;
            VfxChildSystemReference child = childSet.Children[slot];
            VfxSystemDefinition definition = ResolveSystem(
                child,
                _systems,
                parentRuntime.Definition.ResourceMap ?? _resourceMap);
            if (definition is null) return;

            int particleCapacity = ChildCapacityOf(definition);
            if (LiveChildSystemCount >= MaximumActiveChildSystems ||
                _heldChildParticleCapacity + particleCapacity > MaximumChildParticleCapacityBudget)
            {
                return;
            }

            Matrix4x4 childLocalTransform = childWorldTransform;
            if (Matrix4x4.Invert(_rootTransform, out Matrix4x4 inverseRoot))
                childLocalTransform = childWorldTransform * inverseRoot;

            string childPath = $"{emitterPath}.{slot}";
            VfxPlaybackRuntime runtime = CreateRuntime(
                definition,
                childLocalTransform,
                parentDepth + 1,
                childPath,
                childSeed,
                initialRandomState,
                particleCapacity);
            RegisterChildCapacity(runtime, particleCapacity);
            _parents[runtime] = parentRuntime;
            _pendingChildren.Add(runtime);

            if (!carried)
            {
                _looseChildStopAfter[runtime] = (float)VfxDurationCalculator.SystemSpan(definition);
                return;
            }
            var key = (Parent: parentRuntime, particle.SourceOrder, particle.Serial);
            if (!_carriedChildren.TryGetValue(key, out List<CarriedChildInfo> tracked))
            {
                tracked = new List<CarriedChildInfo>();
                _carriedChildren[key] = tracked;
            }
            tracked.Add(new CarriedChildInfo
            {
                Runtime = runtime,
                Definition = definition,
                Set = childSet,
                Slot = slot,
                BoneName = boneName
            });
        }

        private void RegisterChildCapacity(VfxPlaybackRuntime runtime, int capacity)
        {
            if (_childCapacities.ContainsKey(runtime)) return;
            int held = Math.Clamp(capacity, MinimumChildParticleCapacity, MaximumChildParticleCapacity);
            _childCapacities[runtime] = held;
            _heldChildParticleCapacity += held;
        }

        /// <summary>
        /// Matches LTK childPool.capacityOf: estimate the child's peak simultaneous demand,
        /// then reserve the next power-of-two pool between 16 and 4096 particles.
        /// </summary>
        internal static int ChildCapacityOf(VfxSystemDefinition system)
        {
            if (system is null) return MinimumChildParticleCapacity;

            double wanted = 0d;
            foreach (VfxEmitterDefinition emitter in system.Emitters)
            {
                if (emitter.Disabled) continue;
                double rate = Peak(emitter.Rate);
                if (emitter.IsSingleParticle)
                {
                    int burst = (int)(Math.Truncate(rate) % (ushort.MaxValue + 1d));
                    wanted += Math.Max(burst, 1);
                }
                else
                {
                    double lifetime = Peak(emitter.ParticleLifetime);
                    wanted += Math.Ceiling(rate * (lifetime + VfxPlaybackRuntime.LingerSeconds(emitter))) +
                              Math.Ceiling(rate) + 1d;
                }
            }

            int capacity = MinimumChildParticleCapacity;
            while (capacity < wanted && capacity < MaximumChildParticleCapacity)
                capacity *= 2;
            return capacity;
        }

        private static double Peak(VfxCurveF curve)
        {
            double most = Math.Max(curve.Constant, 0f);
            if (curve.Values is not { Length: > 0 }) return most;
            foreach (float value in curve.Values)
                most = Math.Max(most, value);
            return most;
        }

        private static Matrix4x4 ChildBearing(
            VfxChildParticleSetDefinition childSet,
            VfxPlaybackRuntime.ParticleLifecycleInfo particle)
        {
            int mode = childSet.InheritanceMode;
            Matrix4x4 bearing = (mode & 0x2) != 0 ? particle.Frame : particle.Basis;
            Vector3 relativeOffset = childSet.RelativeOffset.Sample(0f);
            if ((mode & 0x1) == 0)
                relativeOffset = Vector3.TransformNormal(relativeOffset, particle.Basis);
            Vector3 childPosition = particle.Position + relativeOffset;
            bearing.M41 = childPosition.X;
            bearing.M42 = childPosition.Y;
            bearing.M43 = childPosition.Z;
            return bearing;
        }

        private static Matrix4x4 ReRootOnJoint(Matrix4x4 bearing, Matrix4x4 joint)
        {
            Matrix4x4 bearingTurn = OrientationOnly(bearing);
            Matrix4x4 jointTurn = OrientationOnly(joint);
            Vector3 jointOffset = Vector3.TransformNormal(joint.Translation, bearingTurn);
            Vector3 position = bearing.Translation + jointOffset;

            // System.Numerics uses row vectors: local joint turn followed by the particle
            // bearing is the same composition as LTK's bearingYaw * jointBasis.
            Matrix4x4 result = jointTurn * bearingTurn;
            result.M41 = position.X;
            result.M42 = position.Y;
            result.M43 = position.Z;
            return result;
        }

        private void UpdateChildPlacement(
            VfxPlaybackRuntime runtime,
            VfxSystemDefinition definition,
            Matrix4x4 childWorldTransform)
        {
            Matrix4x4 childLocalTransform = childWorldTransform;
            if (Matrix4x4.Invert(_rootTransform, out Matrix4x4 inverseRoot))
                childLocalTransform = childWorldTransform * inverseRoot;

            Matrix4x4 effectiveLocal = ComposeChildTransform(
                definition.Transform.GetValueOrDefault(Matrix4x4.Identity),
                childLocalTransform);
            _localTransforms[runtime] = effectiveLocal;
            runtime.SetTransform(
                effectiveLocal * _rootTransform,
                effectiveLocal * _orientationRootTransform);
        }

        private static Matrix4x4 ComposeChildTransform(Matrix4x4 authored, Matrix4x4 bearing)
        {
            Vector3 authoredOffset = authored.Translation;
            authored.M41 = 0f;
            authored.M42 = 0f;
            authored.M43 = 0f;
            Matrix4x4 result = authored * bearing;
            Vector3 origin = bearing.Translation + authoredOffset;
            result.M41 = origin.X;
            result.M42 = origin.Y;
            result.M43 = origin.Z;
            return result;
        }

        private static Matrix4x4 OrientationOnly(Matrix4x4 transform)
        {
            static Vector3 Safe(Vector3 value, Vector3 fallback)
                => value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : fallback;

            Vector3 right = Safe(Vector3.TransformNormal(Vector3.UnitX, transform), Vector3.UnitX);
            Vector3 up = Safe(Vector3.TransformNormal(Vector3.UnitY, transform), Vector3.UnitY);
            Vector3 forward = Safe(Vector3.TransformNormal(Vector3.UnitZ, transform), Vector3.UnitZ);
            return new Matrix4x4(
                right.X, right.Y, right.Z, 0f,
                up.X, up.Y, up.Z, 0f,
                forward.X, forward.Y, forward.Z, 0f,
                0f, 0f, 0f, 1f);
        }

        private static int ChildSeed(int seed, string path, uint serial)
        {
            uint pathHash = Fnv1a.HashLower(path ?? string.Empty);
            uint lineage = unchecked((serial + 1u) * 0x9e3779b1u);
            return unchecked(seed ^ (int)pathHash ^ (int)lineage);
        }

        internal static VfxSystemDefinition ResolveSystem(
            VfxChildSystemReference reference,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap)
        {
            if (reference is null) return null;
            if (reference.SystemHash != 0 && systems.TryGetValue(reference.SystemHash, out VfxSystemDefinition definition))
                return definition;
            if (reference.EffectKey != 0)
            {
                if (resourceMap.TryGetValue(reference.EffectKey, out uint mappedHash))
                {
                    // An effect key only names a child through its ResourceResolver scope.
                    // A resolver hit is authoritative even when it maps to null or to an unavailable object.
                    return mappedHash != 0 && systems.TryGetValue(mappedHash, out definition)
                        ? definition
                        : null;
                }
            }
            return null;
        }
    }

    public static class VfxDurationCalculator
    {
        private const int MaximumGraphDepth = 8;
        private const double EndlessSpan = 5d;
        private const double MinimumSpan = 1d;
        private const double MaximumSpan = 60d;

        public static double Calculate(
            VfxSystemDefinition system,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems = null,
            IReadOnlyDictionary<uint, uint> resourceMap = null)
        {
            if (system is null) return 0;
            systems ??= new Dictionary<uint, VfxSystemDefinition>();
            resourceMap ??= new Dictionary<uint, uint>();
            return CalculateSystem(
                system,
                systems,
                resourceMap,
                new HashSet<VfxSystemDefinition>(ReferenceEqualityComparer.Instance),
                0);
        }

        public static double GetMaximumParticleLifetime(VfxEmitterDefinition emitter)
        {
            if (emitter is null) return 0;
            float[] authoredValues = emitter.ParticleLifetime.Values is { Length: > 0 } values
                ? values.Append(emitter.ParticleLifetime.Constant).ToArray()
                : new[] { emitter.ParticleLifetime.Constant };
            float[] probabilityValues = ProbabilityValues(emitter.ParticleLifetime.Prob);
            double[] possibleLifetimes = authoredValues
                .SelectMany(value => probabilityValues.Select(probability => (double)value * probability))
                .ToArray();
            if (possibleLifetimes.Any(value => value < 0))
            {
                return double.PositiveInfinity;
            }
            double maximum = possibleLifetimes.Max();
            return Math.Max(0.05, maximum);
        }

        private static float[] ProbabilityValues(VfxProbTable[] tables)
        {
            if (tables is not { Length: > 0 } || tables[0].IsEmpty)
                return new[] { 1f };

            VfxProbTable table = tables[0];
            return table.Values is { Length: > 0 }
                ? table.Values
                : new[] { table.Single };
        }

        public static double SystemSpan(VfxSystemDefinition system)
        {
            if (system is null) return MinimumSpan;

            double span = MinimumSpan;
            foreach (VfxEmitterDefinition emitter in system.Emitters)
            {
                if (emitter.Disabled) continue;
                double emitting = emitter.EmitterLifetime ?? EndlessSpan;
                span = Math.Max(
                    span,
                    emitter.TimeBeforeFirstEmission + emitting + Peak(emitter.ParticleLifetime));
            }
            return Math.Min(span, MaximumSpan);
        }

        public static double LingerTail(VfxSystemDefinition system, double stoppedAt)
        {
            if (system is null) return 0d;

            double age = stoppedAt + system.BuildUpTime;
            double tail = 0d;
            foreach (VfxEmitterDefinition emitter in system.Emitters)
            {
                if (emitter.Disabled) continue;
                double wait = Math.Max(VfxPlaybackRuntime.StopWaitSeconds(emitter) - age, 0d);
                tail = Math.Max(tail, wait + VfxPlaybackRuntime.LingerSeconds(emitter));
            }
            return tail;
        }

        private static double Peak(VfxCurveF curve)
        {
            double peak = Math.Max(curve.Constant, 0f);
            if (curve.Values is not { Length: > 0 }) return peak;
            foreach (float value in curve.Values) peak = Math.Max(peak, value);
            return peak;
        }

        private static double CalculateSystem(
            VfxSystemDefinition system,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap,
            HashSet<VfxSystemDefinition> path,
            int depth)
        {
            if (!path.Add(system)) return double.PositiveInfinity;

            double systemEnd = 0;
            foreach (VfxEmitterDefinition emitter in system.Emitters.Where(item => !item.Disabled))
            {
                double particleLifetime = GetMaximumParticleLifetime(emitter);
                if (double.IsInfinity(particleLifetime))
                {
                    path.Remove(system);
                    return double.PositiveInfinity;
                }
                if (emitter.EmitterLifetime is { } life && emitter.TimeBeforeFirstEmission > life) continue;
                double lastEmission = emitter.IsSingleParticle
                    ? emitter.TimeBeforeFirstEmission
                    : Math.Max(emitter.TimeBeforeFirstEmission, emitter.EmitterLifetime ?? 0);
                double emitterEnd = lastEmission + particleLifetime;

                if (depth < MaximumGraphDepth && emitter.ChildParticleSet is { Children.Count: > 0 } childSet)
                {
                    double childDuration = 0;
                    foreach (VfxChildSystemReference child in childSet.Children)
                    {
                        VfxSystemDefinition childSystem = VfxPlaybackGraphRuntime.ResolveSystem(
                            child,
                            systems,
                            system.ResourceMap ?? resourceMap);
                        if (childSystem is null) continue;
                        childDuration = Math.Max(
                            childDuration,
                            CalculateSystem(childSystem, systems, resourceMap, path, depth + 1));
                    }

                    if (double.IsInfinity(childDuration))
                    {
                        path.Remove(system);
                        return double.PositiveInfinity;
                    }

                    double childTrigger = lastEmission + (childSet.EmitOnDeath ? particleLifetime : 0);
                    emitterEnd = Math.Max(emitterEnd, childTrigger + childDuration);
                }

                systemEnd = Math.Max(systemEnd, emitterEnd);
            }

            path.Remove(system);
            return systemEnd;
        }
    }
}
