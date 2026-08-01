using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Vfx
{
    /// <summary>Executes one complete VFX graph, including particle-authored child systems.</summary>
    public sealed class VfxPlaybackGraphRuntime
    {
        private const int MaximumGraphDepth = 8;
        private const int MaximumActiveChildSystems = 2048;

        private readonly IReadOnlyDictionary<uint, VfxSystemDefinition> _systems;
        private readonly IReadOnlyDictionary<uint, uint> _resourceMap;
        private readonly Func<VfxSystemDefinition, Matrix4x4, int, VfxPlaybackRuntime> _runtimeFactory;
        private readonly List<VfxPlaybackRuntime> _runtimes = new();
        private readonly List<VfxPlaybackRuntime> _pendingChildren = new();
        private readonly Dictionary<VfxPlaybackRuntime, int> _depth = new();
        private readonly Dictionary<VfxPlaybackRuntime, Matrix4x4> _localTransforms = new();
        private readonly int _initialSeed;
        private Random _random;
        private int _nextSeed;
        private Matrix4x4 _rootTransform;

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
            _random = new Random(seed);
            _nextSeed = seed;
            _rootTransform = rootTransform;

            Root = CreateRuntime(rootDefinition, Matrix4x4.Identity, 0);
            _runtimes.Add(Root);
        }

        public VfxPlaybackRuntime Root { get; }
        public IReadOnlyList<VfxPlaybackRuntime> Runtimes => _runtimes;
        public bool IsComplete => _pendingChildren.Count == 0 && _runtimes.Count == 1 && Root.IsComplete;
        public object UserTag
        {
            get => Root.UserTag;
            set => Root.UserTag = value;
        }

        public void SetTransform(Matrix4x4 transform)
        {
            _rootTransform = transform;
            foreach (VfxPlaybackRuntime runtime in _runtimes)
            {
                runtime.SetTransform(_localTransforms[runtime] * _rootTransform);
            }
        }
        public void SetStartDelay(float seconds) => Root.SetStartDelay(seconds);

        public void Reset()
        {
            for (int index = _runtimes.Count - 1; index > 0; index--)
            {
                VfxPlaybackRuntime runtime = _runtimes[index];
                runtime.ParticleLifecycle -= OnParticleLifecycle;
                _depth.Remove(runtime);
                _localTransforms.Remove(runtime);
                _runtimes.RemoveAt(index);
            }
            foreach (VfxPlaybackRuntime pending in _pendingChildren)
            {
                pending.ParticleLifecycle -= OnParticleLifecycle;
                _depth.Remove(pending);
                _localTransforms.Remove(pending);
            }
            _pendingChildren.Clear();
            _random = new Random(_initialSeed);
            _nextSeed = unchecked(_initialSeed + 1);
            Root.Reset();
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
                _depth.Remove(runtime);
                _localTransforms.Remove(runtime);
                _runtimes.RemoveAt(index);
            }
        }

        private VfxPlaybackRuntime CreateRuntime(VfxSystemDefinition definition, Matrix4x4 localTransform, int depth)
        {
            Matrix4x4 effectiveLocalTransform =
                definition.Transform.GetValueOrDefault(Matrix4x4.Identity) * localTransform;
            VfxPlaybackRuntime runtime = _runtimeFactory(
                definition,
                localTransform * _rootTransform,
                unchecked(++_nextSeed));
            runtime.ParticleLifecycle += OnParticleLifecycle;
            _depth[runtime] = depth;
            _localTransforms[runtime] = effectiveLocalTransform;
            return runtime;
        }

        private void OnParticleLifecycle(
            VfxPlaybackRuntime parentRuntime,
            VfxEmitterDefinition emitter,
            Vector3 particlePosition,
            bool died)
        {
            VfxChildParticleSetDefinition childSet = emitter.ChildParticleSet;
            if (childSet is null || childSet.EmitOnDeath != died || childSet.Children.Count == 0) return;

            int parentDepth = _depth.TryGetValue(parentRuntime, out int value) ? value : 0;
            if (parentDepth >= MaximumGraphDepth || _runtimes.Count + _pendingChildren.Count >= MaximumActiveChildSystems)
                return;

            float probability = Math.Clamp(childSet.Probability.SampleBirth(_random), 0f, 1f);
            if (_random.NextDouble() > probability) return;

            Vector3 relativeOffset = parentRuntime.TransformOffset(childSet.RelativeOffset.SampleBirth(_random));
            Vector3 childPosition = particlePosition + relativeOffset;
            Matrix4x4 childWorldTransform = parentRuntime.WorldTransform;
            childWorldTransform.M41 = childPosition.X;
            childWorldTransform.M42 = childPosition.Y;
            childWorldTransform.M43 = childPosition.Z;

            Matrix4x4 childLocalTransform = childWorldTransform;
            if (Matrix4x4.Invert(_rootTransform, out Matrix4x4 inverseRoot))
                childLocalTransform = childWorldTransform * inverseRoot;

            foreach (VfxChildSystemReference child in childSet.Children)
            {
                VfxSystemDefinition definition = ResolveSystem(child, _systems, _resourceMap);
                if (definition is null) continue;
                if (_runtimes.Count + _pendingChildren.Count >= MaximumActiveChildSystems) break;
                _pendingChildren.Add(CreateRuntime(definition, childLocalTransform, parentDepth + 1));
            }
        }

        internal static VfxSystemDefinition ResolveSystem(
            VfxChildSystemReference reference,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
            IReadOnlyDictionary<uint, uint> resourceMap)
        {
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
}
