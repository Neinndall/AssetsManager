using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
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
                double lastEmission = emitter.TimeBeforeFirstEmission +
                    (emitter.IsSingleParticle ? 0 : Math.Max(0, emitter.EmitterLifetime ?? 0));
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
