using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
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
                    emitter.TimeBeforeFirstEmission + emitting + GetMaximumParticleLifetime(emitter));
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

        /// <summary>The largest value a curve reaches: zero, its constant and every key.</summary>
        internal static double Peak(VfxCurveF curve)
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
                            system.ResourceMap,
                            resourceMap);
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
