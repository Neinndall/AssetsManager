using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    /// <summary>Executes one complete VFX graph, including particle-authored child systems.</summary>
    public sealed class VfxPlaybackGraphRuntime
    {
        // LTK bounds recursive particle children at four levels and 512 live systems.
        private const int MaximumGraphDepth = 4;
        private const int MaximumActiveChildSystems = 512;

        private readonly IReadOnlyDictionary<uint, VfxSystemDefinition> _systems;
        private readonly IReadOnlyDictionary<uint, uint> _resourceMap;
        private readonly Func<VfxSystemDefinition, Matrix4x4, int, VfxPlaybackRuntime> _runtimeFactory;
        private readonly List<VfxPlaybackRuntime> _runtimes = new();
        private readonly List<VfxPlaybackRuntime> _pendingChildren = new();
        private readonly Dictionary<VfxPlaybackRuntime, int> _depth = new();
        private readonly Dictionary<VfxPlaybackRuntime, Matrix4x4> _localTransforms = new();
        private readonly Dictionary<VfxPlaybackRuntime, string> _paths = new();
        private readonly Dictionary<(VfxPlaybackRuntime Parent, int SourceOrder, uint Serial), List<CarriedChildInfo>> _carriedChildren = new();
        private readonly int _initialSeed;
        private Matrix4x4 _rootTransform;
        private Matrix4x4 _orientationRootTransform;
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

        internal sealed record RuntimeSnapshot(
            VfxSystemDefinition Definition,
            Matrix4x4 LocalTransform,
            int Depth,
            string Path,
            int Seed,
            uint InitialRandomState,
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

        internal sealed record Snapshot(
            Matrix4x4 RootTransform,
            Matrix4x4 OrientationRootTransform,
            RuntimeSnapshot[] Runtimes,
            CarriedSnapshot[] CarriedChildren,
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
            _rootTransform = rootTransform;
            _orientationRootTransform = rootTransform;

            Root = CreateRuntime(rootDefinition, Matrix4x4.Identity, 0, string.Empty, seed);
            _runtimes.Add(Root);
        }

        public VfxPlaybackRuntime Root { get; }
        public IReadOnlyList<VfxPlaybackRuntime> Runtimes => _runtimes;
        internal int InitialSeed => _initialSeed;
        public bool IsComplete => _pendingChildren.Count == 0 && _runtimes.Count == 1 && Root.IsComplete;
        public object UserTag
        {
            get => Root.UserTag;
            set => Root.UserTag = value;
        }

        public bool IsStopped
        {
            get => Root.IsStopped;
            set
            {
                foreach (VfxPlaybackRuntime runtime in _runtimes)
                {
                    runtime.IsStopped = value;
                }
            }
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

        public void SetAllEmittersVisible(bool isVisible)
        {
            _allEmittersVisible = isVisible;
            foreach (VfxPlaybackRuntime runtime in _runtimes)
            {
                foreach (VfxPlaybackRuntime.EmitterState emitter in runtime.Emitters)
                    emitter.IsVisible = isVisible;
            }
            foreach (VfxPlaybackRuntime runtime in _pendingChildren)
            {
                foreach (VfxPlaybackRuntime.EmitterState emitter in runtime.Emitters)
                    emitter.IsVisible = isVisible;
            }
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
            for (int index = _runtimes.Count - 1; index > 0; index--)
            {
                Forget(_runtimes[index]);
                _runtimes.RemoveAt(index);
            }
        }

        public void Reset()
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
            Root.ParticleLifecycle -= OnParticleLifecycle;
            Root.ParticleLifecycle += OnParticleLifecycle;
            Root.ParticleUpdated -= OnParticleUpdated;
            Root.ParticleUpdated += OnParticleUpdated;
            Root.Reset();
            Root.WarmUp();
        }

        internal Snapshot CaptureSnapshot()
        {
            var all = new List<VfxPlaybackRuntime>(_runtimes.Count + _pendingChildren.Count);
            all.AddRange(_runtimes);
            all.AddRange(_pendingChildren);
            var indices = new Dictionary<VfxPlaybackRuntime, int>(ReferenceEqualityComparer.Instance);
            var saved = new RuntimeSnapshot[all.Count];
            long bytes = 256;

            for (int index = 0; index < all.Count; index++)
            {
                VfxPlaybackRuntime runtime = all[index];
                indices[runtime] = index;
                VfxPlaybackRuntime.Snapshot state = runtime.CaptureSnapshot();
                bool pending = index >= _runtimes.Count;
                saved[index] = new RuntimeSnapshot(
                    runtime.Definition,
                    _localTransforms.GetValueOrDefault(runtime, Matrix4x4.Identity),
                    _depth.GetValueOrDefault(runtime, 0),
                    _paths.GetValueOrDefault(runtime, string.Empty),
                    runtime.Seed,
                    runtime.InitialRandomState,
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

            return new Snapshot(
                _rootTransform,
                _orientationRootTransform,
                saved,
                carried.ToArray(),
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
            _carriedChildren.Clear();
            _rootTransform = snapshot.RootTransform;
            _orientationRootTransform = snapshot.OrientationRootTransform;

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

            // Preview visibility is UI state, not simulation state. A rewind must not undo
            // the user's current global mute; restored child runtimes inherit the live setting.
            if (!_allEmittersVisible)
            {
                foreach (VfxPlaybackRuntime runtime in _runtimes.Concat(_pendingChildren))
                    foreach (VfxPlaybackRuntime.EmitterState emitter in runtime.Emitters)
                        emitter.IsVisible = false;
            }
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
            runtime.ParticleLifecycle += OnParticleLifecycle;
            runtime.ParticleUpdated += OnParticleUpdated;
            _depth[runtime] = saved.Depth;
            _localTransforms[runtime] = saved.LocalTransform;
            _paths[runtime] = saved.Path;
            AssignRenderIdentity(runtime, saved.Path);
            runtime.RestoreSnapshot(saved.State);
            return runtime;
        }

        private void Forget(VfxPlaybackRuntime runtime)
        {
            _depth.Remove(runtime);
            _localTransforms.Remove(runtime);
            _paths.Remove(runtime);

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
        }

        public void Update(float deltaTime)
        {
            if (deltaTime <= 0f || !float.IsFinite(deltaTime)) return;
            while (deltaTime > 0f)
            {
                float step = MathF.Min(deltaTime, 0.1f);
                UpdateStep(step);
                deltaTime -= step;
            }
        }

        private void UpdateStep(float deltaTime)
        {
            int runtimeCount = _runtimes.Count;
            for (int index = 0; index < runtimeCount; index++)
                _runtimes[index].Update(deltaTime);

            if (_pendingChildren.Count > 0)
            {
                _runtimes.AddRange(_pendingChildren);
                _pendingChildren.Clear();
            }

            for (int index = _runtimes.Count - 1; index > 0; index--)
            {
                VfxPlaybackRuntime runtime = _runtimes[index];
                if (!runtime.IsComplete) continue;
                runtime.ParticleLifecycle -= OnParticleLifecycle;
                runtime.ParticleUpdated -= OnParticleUpdated;
                Forget(runtime);
                _runtimes.RemoveAt(index);
            }
        }

        private VfxPlaybackRuntime CreateRuntime(
            VfxSystemDefinition definition,
            Matrix4x4 localTransform,
            int depth,
            string path,
            int seed,
            uint? initialRandomState = null)
        {
            Matrix4x4 effectiveLocalTransform =
                definition.Transform.GetValueOrDefault(Matrix4x4.Identity) * localTransform;
            VfxPlaybackRuntime runtime = _runtimeFactory(
                definition,
                effectiveLocalTransform * _rootTransform,
                seed);
            runtime.SetTransform(
                effectiveLocalTransform * _rootTransform,
                effectiveLocalTransform * _orientationRootTransform);
            if (initialRandomState.HasValue)
                runtime.SetInitialRandomState(initialRandomState.Value);
            runtime.ParticleLifecycle += OnParticleLifecycle;
            runtime.ParticleUpdated += OnParticleUpdated;
            _depth[runtime] = depth;
            _localTransforms[runtime] = effectiveLocalTransform;
            _paths[runtime] = path;
            AssignRenderIdentity(runtime, path);
            runtime.WarmUp();
            if (!_allEmittersVisible)
            {
                foreach (VfxPlaybackRuntime.EmitterState emitter in runtime.Emitters)
                    emitter.IsVisible = false;
            }
            return runtime;
        }

        private void AssignRenderIdentity(VfxPlaybackRuntime runtime, string path)
        {
            foreach (VfxPlaybackRuntime.EmitterState emitter in runtime.Emitters)
            {
                emitter.RenderGraphKey = this;
                emitter.RenderPath = path ?? string.Empty;
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

                SpawnChildren(parentRuntime, childSet, particle, carried: false);
                return;
            }

            if (!childSet.EmitOnDeath)
                SpawnChildren(parentRuntime, childSet, particle, carried: true);
        }

        private void OnParticleUpdated(
            VfxPlaybackRuntime parentRuntime,
            VfxEmitterDefinition emitter,
            VfxPlaybackRuntime.ParticleLifecycleInfo particle)
        {
            VfxChildParticleSetDefinition childSet = emitter.ChildParticleSet;
            if (childSet is null || childSet.EmitOnDeath || childSet.Children.Count == 0) return;

            var key = (Parent: parentRuntime, particle.SourceOrder, particle.Serial);
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
                    if (_runtimes.Count + _pendingChildren.Count >= MaximumActiveChildSystems) break;
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

            if (_runtimes.Count + _pendingChildren.Count >= MaximumActiveChildSystems) return;

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
            VfxSystemDefinition definition = ResolveSystem(child, _systems, _resourceMap);
            if (definition is null) return;

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
                initialRandomState);
            _pendingChildren.Add(runtime);

            if (!carried) return;
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

            Matrix4x4 effectiveLocal = definition.Transform.GetValueOrDefault(Matrix4x4.Identity) * childLocalTransform;
            _localTransforms[runtime] = effectiveLocal;
            runtime.SetTransform(
                effectiveLocal * _rootTransform,
                effectiveLocal * _orientationRootTransform);
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
                if (resourceMap.TryGetValue(reference.EffectKey, out uint mappedHash) &&
                    systems.TryGetValue(mappedHash, out definition)) return definition;
                if (systems.TryGetValue(reference.EffectKey, out definition)) return definition;
            }
            if (!string.IsNullOrWhiteSpace(reference.Name) &&
                systems.TryGetValue(Fnv1a.HashLower(reference.Name), out definition)) return definition;
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
            float[] probabilityValues = emitter.ParticleLifetime.Prob is { Length: > 0 } probabilityTables &&
                                        !probabilityTables[0].IsEmpty
                ? probabilityTables[0].Values
                : new[] { 1f };
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
                if (emitter.IsLoop)
                {
                    path.Remove(system);
                    return double.PositiveInfinity;
                }

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
                        VfxSystemDefinition childSystem = VfxPlaybackGraphRuntime.ResolveSystem(child, systems, resourceMap);
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
