using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

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
        private readonly Random _random;
        private int _nextSeed;

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
            _random = new Random(seed);
            _nextSeed = seed;

            Root = CreateRuntime(rootDefinition, rootTransform, 0);
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
            foreach (VfxPlaybackRuntime runtime in _runtimes)
            {
                runtime.SetTransform(transform);
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
                _runtimes.RemoveAt(index);
            }
            foreach (VfxPlaybackRuntime pending in _pendingChildren)
            {
                pending.ParticleLifecycle -= OnParticleLifecycle;
                _depth.Remove(pending);
            }
            _pendingChildren.Clear();
            Root.Reset();
        }

        public void Update(float deltaTime)
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
                _runtimes.RemoveAt(index);
            }
        }

        private VfxPlaybackRuntime CreateRuntime(VfxSystemDefinition definition, Matrix4x4 transform, int depth)
        {
            VfxPlaybackRuntime runtime = _runtimeFactory(definition, transform, unchecked(++_nextSeed));
            runtime.ParticleLifecycle += OnParticleLifecycle;
            _depth[runtime] = depth;
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

            Vector3 relativeOffset = childSet.RelativeOffset.SampleBirth(_random);
            relativeOffset = parentRuntime.TransformOffset(relativeOffset);
            Matrix4x4 childTransform = Matrix4x4.CreateTranslation(particlePosition + relativeOffset);

            foreach (VfxChildSystemReference child in childSet.Children)
            {
                uint systemHash = child.SystemHash;
                VfxSystemDefinition definition = null;
                if (systemHash != 0) _systems.TryGetValue(systemHash, out definition);
                if (definition is null && child.EffectKey != 0)
                {
                    if (_resourceMap.TryGetValue(child.EffectKey, out uint mappedHash))
                        _systems.TryGetValue(mappedHash, out definition);
                    if (definition is null) _systems.TryGetValue(child.EffectKey, out definition);
                }
                if (definition is null && !string.IsNullOrEmpty(child.Name))
                {
                    uint lowerHash = VfxResourceResolver.Fnv1a(child.Name);
                    _systems.TryGetValue(lowerHash, out definition);
                }
                if (definition is null) continue;
                if (_runtimes.Count + _pendingChildren.Count >= MaximumActiveChildSystems) break;
                _pendingChildren.Add(CreateRuntime(definition, childTransform, parentDepth + 1));
            }
        }

    }
}
