using System;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    public sealed partial class VfxPlaybackRuntime
    {
        private void UpdateStep(float dt)
        {
            if (_startDelay > 0f)
            {
                _startDelay -= dt;
                if (_startDelay > 0f) return;
                dt = MathF.Min(dt, -_startDelay);   // only the portion past the trigger point
                _startDelay = 0f;
                if (dt <= 0f) return;
            }
            CurrentTime += dt;
            Vector3 systemDelta = _pendingOriginDelta;
            _pendingOriginDelta = Vector3.Zero;
            StepEmitters(dt, systemDelta);
        }

        private void StepEmitters(float dt, Vector3 systemDelta)
        {
            // Retire existing particles across the system before consuming the shared capacity.
            EnsureStepScratch();
            int availableParticleSlots = _particleCapacity;
            for (int index = 0; index < _emitters.Count; index++)
            {
                EmitterState emitter = _emitters[index];
                _stepContexts[index] = IntegrateEmitter(emitter, dt, systemDelta);
                availableParticleSlots -= emitter.Particles.Count;
            }
            availableParticleSlots = Math.Max(0, availableParticleSlots);

            Array.Fill(_newbornStarts, -1);
            for (int index = 0; index < _emitters.Count; index++)
            {
                _newbornStarts[index] = EmitEmitter(
                    _emitters[index],
                    _stepContexts[index],
                    ref availableParticleSlots);
            }

            int live = 0;
            for (int index = 0; index < _emitters.Count; index++)
            {
                EmitterState state = _emitters[index];
                if (_newbornStarts[index] >= 0)
                {
                    ApplyFieldsToNewborns(
                        state.Def.Fields,
                        state,
                        _stepContexts[index].EmitterT,
                        _stepContexts[index].PreparedNoise,
                        _stepContexts[index].FieldOrigin,
                        _newbornStarts[index]);
                }
                state.RenderTime = SimulationTime;
                state.InvalidateInstances();
                live += state.Particles.Count;
            }
            LiveParticleCount = live;
        }
        private void EnsureStepScratch()
        {
            int count = _emitters.Count;
            if (_stepContexts.Length != count)
                _stepContexts = new EmitterStepContext[count];
            if (_newbornStarts.Length != count)
                _newbornStarts = new int[count];
        }

        private void SettleEmitter(EmitterState state, float dt)
        {
            VfxEmitterDefinition definition = state.Def;
            byte kind = definition.IsSimpleEmitter ? (byte)0 : definition.ParticleLingerType;
            if (kind > 2 || (!IsStopped && kind != 2)) return;
            bool finished = !definition.IsSimpleEmitter && definition.IsSingleParticle && !definition.OverridesMaterials
                && state.BurstDone && state.EmittedThrough < state.Age;
            finished |= IsStopped
                ? state.Age > StopWaitSeconds(definition)
                : EmissionEnd(definition) is { } lifetime &&
                  state.Age > lifetime;
            if (!finished || (kind != 0 && state.FinishedAt >= 0f)) return;

            if (state.FinishedAt < 0f) state.FinishedAt = state.Age;
            float seconds = LingerSeconds(definition);
            bool capped = kind == 0;
            for (int index = 0; index < state.Particles.Count; index++)
            {
                Particle particle = state.Particles[index];
                if (particle.LingerFrom < 0f) particle.LingerFrom = particle.Age + dt;
                particle.Life = capped
                    ? MathF.Min(particle.Life, seconds)
                    : particle.Age + dt + seconds;
                state.Particles[index] = particle;
            }
        }

        private EmitterStepContext IntegrateEmitter(EmitterState s, float dt, Vector3 systemDelta)
        {
            var d = s.Def;
            s.Age += dt;

            SettleEmitter(s, dt);
            float emitterT = EmitterTime(s);
            Vector3 emitterPosition = d.EmitterPosition.Sample(emitterT);
            Vector3 fieldOrigin = Vector3.Zero;
            Matrix4x4 placement = EmitterPlacement(d);
            s.PlacementTransform = placement;
            s.BasePos = Vector3.Transform(emitterPosition, placement);

            PreparedNoiseField[] preparedNoise = PrepareNoiseFields(d.Fields, s, emitterT, SimulationTime, fieldOrigin);

            // Existing particles are integrated before this step's births. Riot spawns new
            // particles with a zero-sized birth step, so they remain exactly at their birth
            // transform until the following simulation step.
            for (int i = s.Particles.Count - 1; i >= 0; i--)
            {
                var p = s.Particles[i];
                p.Age += dt;
                if (p.Age >= p.Life)
                {
                    ParticleLifecycle?.Invoke(this, d, LifecycleInfo(s, p, died: true));
                    // LTK retires from its packed pool by moving the final live particle into
                    // the freed slot. Keep the same O(1) retirement here; renderers that need
                    // birth order (trails) reconstruct it from the particle serial.
                    int last = s.Particles.Count - 1;
                    if (i != last)
                        s.Particles[i] = s.Particles[last];
                    s.Particles.RemoveAt(last);
                    continue;
                }

                float particleT = ParticleAge01(p.Age, p.Life);
                if (d.BindWeight is { } bindWeight && systemDelta != Vector3.Zero)
                {
                    // Positive values are uncapped; nonpositive values do not bind.
                    float bind = bindWeight.Sample(particleT);
                    if (bind > 0f) p.BoundOffset += systemDelta * bind;
                }

                bool lingering = p.LingerFrom >= 0f;
                Vector3 acceleration = LifeVector(lingering ? d.Linger?.Acceleration ?? d.AccelerationOverLife
                    : d.AccelerationOverLife, particleT, p.Serial, SimulationTime);
                p.Vel += (p.BirthAcceleration + acceleration) * dt;

                Vector3 authoredVelocity = LifeVector(lingering ? d.Linger?.Velocity ?? d.VelocityOverLife
                    : d.VelocityOverLife, particleT, p.Serial, SimulationTime);
                Vector3 moving = p.Vel + authoredVelocity;
                Vector3 dragOverLife = LifeVector(lingering ? d.Linger?.Drag ?? d.DragOverLife
                    : d.DragOverLife, particleT, p.Serial, SimulationTime);
                Vector3 drag = p.BirthDrag + dragOverLife;
                if (_dragMotion == VfxDragMotion.Analytic)
                    ApplyAnalyticDrag(ref p, ref moving, p.BirthDrag, dt);
                else
                    ApplySteppedDrag(ref p.Vel, ref moving, drag, dt);

                // Force fields read the particle where the step began. Bind/root travel and the
                // emitter-space EmitterPosition shift are added only after the field pass, as in LTK.
                ApplyFields(d.Fields, s, emitterT, preparedNoise, fieldOrigin, p.LocalPosition, p.Serial, dt, ref moving, ref p.Vel);
                p.LocalPosition += moving * dt;

                // Birth angular velocity and acceleration always integrate. rotation0 is
                // a separate integrated value authored per 1/60 second and is gated only
                // by isRotationEnabled.
                RebuildRotation(ref p, s);

                Vector3 placed = MatrixTranslation(p, s);
                p.Drift = dt > 0f ? (placed - p.Placed) / dt : Vector3.Zero;
                p.Placed = placed;
                p.Pos = (d.ParticleIsLocalOrientation ? placed : Vector3.TransformNormal(placed, p.BirthFrame)) + p.BirthAnchor + p.BoundOffset;
                p.Travel = d.ParticleIsLocalOrientation ? p.Drift : Vector3.TransformNormal(p.Drift, p.BirthFrame);
                float weight = d.BindWeight?.Sample(particleT) ?? 0f;
                if (weight > 0f && dt > 0f) p.Travel += systemDelta * (weight / dt);
                s.Particles[i] = p;
                if (d.ChildParticleSet is { Children.Count: > 0 })
                    ParticleUpdated?.Invoke(this, d, LifecycleInfo(s, p, died: false));
            }

            return new EmitterStepContext(emitterT, preparedNoise, fieldOrigin, dt, systemDelta);
        }

        private int EmitEmitter(
            EmitterState s,
            EmitterStepContext context,
            ref int availableParticleSlots)
        {
            VfxEmitterDefinition d = s.Def;
            float emitterT = context.EmitterT;
            // The step that crosses the emitter's lifetime still emits, counted only up to the lifetime,
            // so an emitter shorter than one frame emits at any frame rate. Its window opens at the later
            // of the step's start and the first emission, so a lifetime over before that never emits.
            float? end = EmissionEnd(d);
            bool emitting = !d.Disabled && !s.Absent
                            && (!IsStopped || s.Age <= StopWaitSeconds(d))
                            && s.Age >= d.TimeBeforeFirstEmission
                            && (end is not { } life ||
                                MathF.Max(s.Age - context.Dt, d.TimeBeforeFirstEmission) <= life);
            if (!emitting || (d.IsSingleParticle && s.BurstDone)) return -1;
            if (d.EmissionPeriod is { } period && !period.IsActive(s.Age))
            {
                return -1;
            }
            {
                // LTK samples rate directly. Legacy rateIsPeriod is retained in the model
                // for inspection but does not reinterpret the simulation rate.
                float rate;
                if (d.RateByVelocityFunction is { } velocityRate)
                {
                    Vector2 function = velocityRate.Constant;
                    float speed = context.Dt > 0f ? context.SystemDelta.Length() / context.Dt : 0f;
                    rate = Math.Clamp(speed * function.X + function.Y, 0f, MathF.Max(0f, d.MaximumRateByVelocity ?? 300f));
                }
                else rate = MathF.Max(0f, BirthScalar(d.Rate, emitterT));
                if (!float.IsFinite(rate)) rate = 0f;
                float emittingUntil = end is { } lifetime ? MathF.Min(s.Age, lifetime) : s.Age;
                float cycleStart = d.EmissionPeriod?.Length is { } length
                    ? MathF.Truncate(s.Age / length) * length : 0f;
                int requestedCount = Math.Min(
                    WrappedCount(MathF.Max(0f, emittingUntil - MathF.Max(cycleStart, s.EmittedThrough)) * rate),
                    (WrappedCount(rate * 0.33f) + 1) & ushort.MaxValue);
                if (!s.InitialEmissionDone)
                {
                    if (d.IsSingleParticle)
                    {
                        // Riot stores the authored burst count through a uint16 lane, so
                        // values above 65535 wrap rather than clamp.
                        int wrapped = (int)(Math.Truncate((double)rate) % (ushort.MaxValue + 1d));
                        requestedCount = Math.Max(1, wrapped);
                    }
                    else if (!d.HasVariableStartTime)
                    {
                        requestedCount = Math.Max(1, requestedCount);
                    }
                }
                if (d.Trail?.MaxAddedPerFrame is > 0)
                    requestedCount = Math.Min(requestedCount, d.Trail.MaxAddedPerFrame);
                requestedCount = Math.Min(requestedCount, 1000);
                if (requestedCount <= 0) return -1;

                // LTK advances a trail odometer only when an emission batch is actually due,
                // before attempting the shared-pool spawn, including the translation override.
                AdvanceTrailOdometer(s, emitterT);

                // LTK advances the emitter by the requested batch even if its shared pool
                // fills partway through. Preserve that debt semantics instead of retrying
                // dropped births on a later frame.
                if (d.ParticlesShareRandomValue && !s.SharedRandomRolled)
                {
                    s.SharedRandom = _rng.NextUnitFloat();
                    s.SharedRandomRolled = true;
                }
                int actualCount = Math.Min(requestedCount, availableParticleSlots);
                availableParticleSlots -= actualCount;
                int firstNewborn = s.Particles.Count;
                // The engine spreads a step's births along the emitter's travel during the step, the last
                // one at its current origin, rather than stacking them all on that origin.
                Vector3 origin = EmitterPlacement(d).Translation;
                for (int born = 0; born < actualCount; born++)
                {
                    float beforeEnd = 1f - (born + 1f) / requestedCount;
                    Spawn(s, emitterT, origin - context.SystemDelta * beforeEnd, context.Dt * beforeEnd);
                }

                if (actualCount < requestedCount)
                {
                    // emit.ts draws the failed particle's roll (and its per-particle chance)
                    // before pool.spawn reports a full pool, then breaks the batch.
                    if (!d.ParticlesShareRandomValue) _rng.NextUnitFloat();
                }

                s.InitialEmissionDone = true;
                s.BurstDone = d.IsSingleParticle;
                s.EmittedThrough = s.Age;
                return actualCount > 0 ? firstNewborn : -1;
            }
        }

        private readonly record struct EmitterStepContext(
            float EmitterT,
            PreparedNoiseField[] PreparedNoise,
            Vector3 FieldOrigin,
            float Dt,
            Vector3 SystemDelta);

        private static int WrappedCount(float value)
            => (int)(Math.Truncate((double)value) % (ushort.MaxValue + 1d));

        private static void ApplyAnalyticDrag(ref Particle particle, ref Vector3 moving, Vector3 drag, float dt)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                float dragAxis = axis == 0 ? drag.X : axis == 1 ? drag.Y : drag.Z;
                if (dragAxis <= 0f)
                {
                    continue;
                }
                if (dt <= 0f) continue;
                float terminal = axis == 0 ? particle.AnalyticTerminal.X : axis == 1 ? particle.AnalyticTerminal.Y : particle.AnalyticTerminal.Z;
                float previous = axis == 0 ? particle.AnalyticOffset.X : axis == 1 ? particle.AnalyticOffset.Y : particle.AnalyticOffset.Z;
                float next = MathF.Exp(-dragAxis * particle.Age) * terminal;
                float contribution = (previous - next) / dt;
                if (axis == 0)
                {
                    moving.X += contribution;
                    particle.AnalyticOffset.X = next;
                }
                else if (axis == 1)
                {
                    moving.Y += contribution;
                    particle.AnalyticOffset.Y = next;
                }
                else
                {
                    moving.Z += contribution;
                    particle.AnalyticOffset.Z = next;
                }
            }
        }

        private static void ApplySteppedDrag(ref Vector3 kept, ref Vector3 moving, Vector3 drag, float dt)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                float movingAxis = axis == 0 ? moving.X : axis == 1 ? moving.Y : moving.Z;
                float dragAxis = axis == 0 ? drag.X : axis == 1 ? drag.Y : drag.Z;
                if (dragAxis == 0f || movingAxis == 0f) continue;
                float change = -dragAxis * movingAxis * dt;
                if ((change + movingAxis) * movingAxis < 0f) change = -movingAxis;
                if (axis == 0)
                {
                    moving.X += change;
                    kept.X += change;
                }
                else if (axis == 1)
                {
                    moving.Y += change;
                    kept.Y += change;
                }
                else
                {
                    moving.Z += change;
                    kept.Z += change;
                }
            }
        }
    }
}
