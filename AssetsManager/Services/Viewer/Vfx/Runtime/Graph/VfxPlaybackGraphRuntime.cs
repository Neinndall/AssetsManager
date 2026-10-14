using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    /// <summary>Executes one complete VFX graph, including particle-authored child systems.</summary>
    public sealed partial class VfxPlaybackGraphRuntime
    {
        // LTK bounds recursive particle children at four levels and 512 live child systems.
        // Child pools reserve a power-of-two capacity from 16..4096 against one 128K lineage budget.
        private const int MaximumGraphDepth = 4;
        private const int MaximumActiveChildSystems = 512;
        private const int MinimumChildParticleCapacity = 16;
        private const int MaximumChildParticleCapacity = 4096;
        private const int MaximumChildParticleCapacityBudget = 1 << 17;

        private static readonly IReadOnlyDictionary<VfxEmitterDefinition, IVfxEmissionSurfaceSampler> EmptyEmissionSurfaces =
            new Dictionary<VfxEmitterDefinition, IVfxEmissionSurfaceSampler>(ReferenceEqualityComparer.Instance);
        private static readonly IReadOnlyDictionary<string, IVfxMeshJointProvider> EmptyMeshJoints =
            new Dictionary<string, IVfxMeshJointProvider>(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<uint, VfxSystemDefinition> _systems;
        private readonly IReadOnlyDictionary<uint, uint> _resourceMap;
        private readonly Func<VfxSystemDefinition, Matrix4x4, int, VfxPlaybackRuntime> _runtimeFactory;
        private readonly List<VfxPlaybackRuntime> _runtimes = new();
        private readonly HashSet<VfxPlaybackRuntime> _activeRuntimes =
            new(ReferenceEqualityComparer.Instance);
        private readonly List<VfxPlaybackRuntime> _pendingChildren = new();
        private readonly Dictionary<VfxPlaybackRuntime, int> _depth = new();
        private readonly Dictionary<VfxPlaybackRuntime, Matrix4x4> _localTransforms = new();
        private readonly Dictionary<VfxPlaybackRuntime, string> _paths = new();
        private readonly Dictionary<VfxPlaybackRuntime, VfxPlaybackRuntime> _parents = new();
        // LTK's Children runtime owns its direct descendants. Keep the reverse parent map for
        // snapshots, but also index children by parent so a frame never rediscovers the tree by
        // scanning every live runtime for every node.
        private readonly Dictionary<VfxPlaybackRuntime, List<VfxPlaybackRuntime>> _childrenByParent =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<VfxPlaybackRuntime, int> _childCapacities = new();
        private readonly Dictionary<VfxPlaybackRuntime, float> _looseChildStopAfter = new();
        private readonly Dictionary<(string Path, int SourceOrder), int> _renderRanks = new();
        private readonly Dictionary<(string Path, int SourceOrder), int> _renderRoots = new();
        private readonly Dictionary<int, bool> _rootVisibility = new();
        private readonly Dictionary<(VfxPlaybackRuntime Parent, int SourceOrder, uint Serial), List<CarriedChildInfo>> _carriedChildren = new();
        private readonly Dictionary<(VfxPlaybackRuntime Parent, int SourceOrder, uint Serial), VfxPlaybackRuntime.ParticleLifecycleInfo> _deathSeen = new();
        private readonly Dictionary<VfxPlaybackRuntime, List<ChildSpawnRequest>> _spawnRequestsByParent =
            new(ReferenceEqualityComparer.Instance);
        private readonly List<(VfxPlaybackRuntime Parent, int SourceOrder, uint Serial)> _particleKeyScratch = new();
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
        private IReadOnlyDictionary<VfxEmitterDefinition, IVfxEmissionSurfaceSampler> _emissionSurfaces = EmptyEmissionSurfaces;
        private IReadOnlyDictionary<string, IVfxMeshJointProvider> _meshJoints = EmptyMeshJoints;
        private readonly Dictionary<string, IVfxMeshJointProvider> _loadedMeshJoints = new(StringComparer.Ordinal);
        private bool _allEmittersVisible = true;
        private float? _pinnedBirthChance;

        private sealed class CarriedChildInfo
        {
            public VfxPlaybackRuntime Runtime { get; init; }
            public VfxSystemDefinition Definition { get; set; }
            public VfxChildParticleSetDefinition Set { get; set; }
            public int Slot { get; init; }
            public string BoneName { get; set; }
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
            Func<VfxSystemDefinition, Matrix4x4, int, VfxPlaybackRuntime> runtimeFactory,
            int? rootParticleCapacity = null)
        {
            ArgumentNullException.ThrowIfNull(rootDefinition);
            _systems = systems ?? throw new ArgumentNullException(nameof(systems));
            _resourceMap = resourceMap ?? throw new ArgumentNullException(nameof(resourceMap));
            _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
            _initialSeed = seed;
            // Definition transforms affect each particle, independently of its rig origin.
            _rootTransform = rootTransform;
            _orientationRootTransform = _rootTransform;
            BuildRenderRanks(rootDefinition);

            Root = CreateRuntime(
                rootDefinition,
                Matrix4x4.Identity,
                0,
                string.Empty,
                seed,
                particleCapacity: rootParticleCapacity);
            _runtimes.Add(Root);
            _activeRuntimes.Add(Root);
            SyncRenderTimes();
        }

        public VfxPlaybackRuntime Root { get; }
        public IReadOnlyList<VfxPlaybackRuntime> Runtimes => _runtimes;
        internal IReadOnlyList<VfxSystemDefinition> ResourceDefinitions
        {
            get
            {
                var definitions = new Dictionary<uint, VfxSystemDefinition>();
                foreach (VfxSystemDefinition definition in _systems.Values)
                {
                    if (definition is not null)
                        definitions[definition.PathHash] = definition;
                }
                if (Root.Definition is { } root)
                    definitions[root.PathHash] = root;
                return definitions.Values.ToArray();
            }
        }
        internal int InitialSeed => _initialSeed;
        internal bool HasEmissionSurfaceDefinitions => ResourceDefinitions.Any(static definition =>
            definition?.Emitters?.Any(static emitter => emitter?.EmissionSurface is not null || emitter?.EmissionMesh is not null) == true);
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

        /// <summary>
        /// Shares one loaded emission-surface catalog across the full child lineage. The caller
        /// owns any time replay because only the session knows the historical rig transforms.
        /// </summary>
        internal bool SetEmissionSurfaces(
            IReadOnlyDictionary<VfxEmitterDefinition, IVfxEmissionSurfaceSampler> surfaces)
        {
            surfaces ??= EmptyEmissionSurfaces;
            if (VfxPlaybackRuntime.SameReferences(_emissionSurfaces, surfaces)) return false;
            _emissionSurfaces = surfaces;
            foreach (VfxPlaybackRuntime runtime in _runtimes.Concat(_pendingChildren))
                runtime.SetEmissionSurfaces(surfaces, replayCurrentTime: false);
            return true;
        }

        /// <summary>
        /// VFX-mesh joints are keyed by drawn emitter path: root "0", descendant
        /// "0.0:1", and so on. A present mesh joint table overrides the owner rig table.
        /// </summary>
        internal bool SetMeshJointProviders(IReadOnlyDictionary<string, IVfxMeshJointProvider> joints)
        {
            joints ??= EmptyMeshJoints;
            if (VfxPlaybackRuntime.SameReferences(_meshJoints, joints)) return false;
            _meshJoints = joints;
            return true;
        }


        public void SetStartDelay(float seconds) => Root.SetStartDelay(seconds);

        internal void SetPinnedBirthChance(float? chance)
        {
            _pinnedBirthChance = chance;
            foreach (VfxPlaybackRuntime runtime in _runtimes.Concat(_pendingChildren))
                runtime.SetPinnedBirthChance(chance);
        }

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
            _spawnRequestsByParent.Clear();
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
            _spawnRequestsByParent.Clear();
        }

        private void RebindRootCallbacks()
        {
            Root.ParticleLifecycle -= OnParticleLifecycle;
            Root.ParticleLifecycle += OnParticleLifecycle;
            Root.ParticleUpdated -= OnParticleUpdated;
            Root.ParticleUpdated += OnParticleUpdated;
        }

        private void Forget(VfxPlaybackRuntime runtime)
        {
            _activeRuntimes.Remove(runtime);
            _depth.Remove(runtime);
            _localTransforms.Remove(runtime);
            _paths.Remove(runtime);
            if (_parents.Remove(runtime, out VfxPlaybackRuntime parent) &&
                _childrenByParent.TryGetValue(parent, out List<VfxPlaybackRuntime> siblings))
            {
                siblings.Remove(runtime);
                if (siblings.Count == 0)
                    _childrenByParent.Remove(parent);
            }
            _childrenByParent.Remove(runtime);
            _spawnRequestsByParent.Remove(runtime);
            _looseChildStopAfter.Remove(runtime);
            if (_childCapacities.Remove(runtime, out int capacity))
                _heldChildParticleCapacity = Math.Max(0, _heldChildParticleCapacity - capacity);

            _particleKeyScratch.Clear();
            foreach (KeyValuePair<(VfxPlaybackRuntime Parent, int SourceOrder, uint Serial), List<CarriedChildInfo>> pair in _carriedChildren)
            {
                if (ReferenceEquals(pair.Key.Parent, runtime))
                {
                    _particleKeyScratch.Add(pair.Key);
                    continue;
                }

                List<CarriedChildInfo> children = pair.Value;
                for (int index = children.Count - 1; index >= 0; index--)
                {
                    if (ReferenceEquals(children[index].Runtime, runtime))
                        children.RemoveAt(index);
                }
                if (children.Count == 0)
                    _particleKeyScratch.Add(pair.Key);
            }
            foreach (var key in _particleKeyScratch)
                _carriedChildren.Remove(key);

            _particleKeyScratch.Clear();
            foreach (var key in _deathSeen.Keys)
            {
                if (ReferenceEquals(key.Parent, runtime))
                    _particleKeyScratch.Add(key);
            }
            foreach (var key in _particleKeyScratch)
                _deathSeen.Remove(key);
            _particleKeyScratch.Clear();
        }

    }
}
