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
        {
            _rootTransform = transform;
            foreach (VfxPlaybackRuntime runtime in _runtimes)
            {
                runtime.SetTransform(_localTransforms[runtime] * _rootTransform);
            }
        }
        public void SetTarget(Vector3 worldTarget)
        {
            foreach (VfxPlaybackRuntime runtime in _runtimes)
                runtime.SetTarget(worldTarget);
        }

        public void SetStartDelay(float seconds) => Root.SetStartDelay(seconds);

        public void Kill()
        {
            foreach (VfxPlaybackRuntime runtime in _runtimes)
            {
                runtime.ParticleLifecycle -= OnParticleLifecycle;
                runtime.Kill();
            }
            foreach (VfxPlaybackRuntime pending in _pendingChildren)
            {
                pending.ParticleLifecycle -= OnParticleLifecycle;
                _depth.Remove(pending);
                _localTransforms.Remove(pending);
            }
            _pendingChildren.Clear();
            for (int index = _runtimes.Count - 1; index > 0; index--)
            {
                _depth.Remove(_runtimes[index]);
                _localTransforms.Remove(_runtimes[index]);
                _runtimes.RemoveAt(index);
            }
        }

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
            Root.ParticleLifecycle -= OnParticleLifecycle;
            Root.ParticleLifecycle += OnParticleLifecycle;
            Root.Reset();
            Root.WarmUp();
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
            runtime.WarmUp();
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

    public static class VfxDurationCalculator
    {
        private const int MaximumGraphDepth = 8;
        private const double PreviewStep = 1d / 60d;
        private const double MaximumPreviewSimulation = 10d;

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

        public static double CalculatePreview(
            VfxSystemDefinition system,
            int seed,
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems = null,
            IReadOnlyDictionary<uint, uint> resourceMap = null)
        {
            double authoredDuration = Calculate(system, systems, resourceMap);
            if (!double.IsFinite(authoredDuration) || authoredDuration <= 0 ||
                authoredDuration > MaximumPreviewSimulation ||
                system.Emitters.Any(emitter => !emitter.Disabled && emitter.EmitterLifetime is null))
            {
                return authoredDuration;
            }

            systems ??= new Dictionary<uint, VfxSystemDefinition>();
            resourceMap ??= new Dictionary<uint, uint>();
            var runtime = new VfxPlaybackGraphRuntime(
                system,
                Matrix4x4.Identity,
                seed,
                systems,
                resourceMap,
                static (definition, transform, runtimeSeed) =>
                {
                    var childRuntime = new VfxPlaybackRuntime(runtimeSeed);
                    childRuntime.SetSystem(definition, transform);
                    return childRuntime;
                });

            double elapsed = 0;
            double simulationLimit = Math.Min(MaximumPreviewSimulation, authoredDuration + 1d);
            while (!runtime.IsComplete && elapsed < simulationLimit)
            {
                runtime.Update((float)PreviewStep);
                elapsed += PreviewStep;
            }

            return runtime.IsComplete ? elapsed : authoredDuration;
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
