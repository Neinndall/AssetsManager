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
            // LTK integrates and retires the entire shared pool before any emitter is
            // allowed to consume slots for newborns. Keep the per-emitter storage, but
            // preserve that system-wide phase ordering.
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
                state.RenderTime = CurrentTime;
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
            bool finished = IsStopped
                ? state.Age > StopWaitSeconds(definition)
                : definition.ParticleLingerType == 2 &&
                  definition.EmitterLifetime is { } lifetime &&
                  state.Age > lifetime;
            if (!finished || state.FinishedAt >= 0f) return;

            state.FinishedAt = state.Age;
            float seconds = LingerSeconds(definition);
            bool capped = definition.ParticleLingerType == 0;
            for (int index = 0; index < state.Particles.Count; index++)
            {
                Particle particle = state.Particles[index];
                particle.LingerFrom = particle.Age + dt;
                particle.Life = capped
                    ? MathF.Min(particle.Life, seconds)
                    : particle.LingerFrom + seconds;
                state.Particles[index] = particle;
            }
        }

        private EmitterStepContext IntegrateEmitter(EmitterState s, float dt, Vector3 systemDelta)
        {
            var d = s.Def;
            float previousEmitterT = EmitterTime(s);
            Vector3 previousEmitterPosition = d.EmitterPosition.Sample(previousEmitterT);
            s.Age += dt;

            SettleEmitter(s, dt);
            float emitterT = EmitterTime(s);
            Vector3 emitterPosition = d.EmitterPosition.Sample(emitterT);
            Vector3 emitterPositionDelta = emitterPosition - previousEmitterPosition;
            Vector3 previousFieldBasePos = s.FieldBasePos;
            // LTK rides emitter-space fields on EmitterPosition under the emitter's basis, but
            // translationOverride belongs to the spawn frame only and must not move field centres.
            Vector3 fieldOrigin = d.IsEmitterSpace
                ? previousFieldBasePos - systemDelta
                : s.SystemOrigin - systemDelta;
            Matrix4x4 placement = EmitterPlacement(d);
            s.PlacementTransform = placement;
            s.BasePos = Vector3.Transform(emitterPosition, placement);
            s.FieldBasePos = EmitterFieldPosition(d, emitterT);

            PreparedNoiseField[] preparedNoise = PrepareNoiseFields(d.Fields, s, emitterT, CurrentTime, fieldOrigin);

            // Existing particles are integrated before this step's births. Riot spawns new
            // particles with a zero-sized birth step, so they remain exactly at their birth
            // transform until the following simulation step.
            for (int i = s.Particles.Count - 1; i >= 0; i--)
            {
                var p = s.Particles[i];
                Vector3 positionBeforeStep = p.Pos;

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
                Vector3 kinematicShift = Vector3.Zero;
                if (d.IsEmitterSpace && emitterPositionDelta != Vector3.Zero)
                    kinematicShift += Vector3.TransformNormal(emitterPositionDelta, p.BirthFrame);
                if (d.BindWeight is { } bindWeight && systemDelta != Vector3.Zero)
                {
                    // LTK forwards the authored bind value verbatim. Values outside 0..1
                    // intentionally over-/counter-follow the moving system origin.
                    float bind = bindWeight.Sample(emitterT);
                    if (bind != 0f) kinematicShift += systemDelta * bind;
                }

                float lingerT = LingerProgress(s);
                Vector3 acceleration = s.FinishedAt >= 0f && d.Linger?.Acceleration is { } lingerAcceleration
                    ? lingerAcceleration.Sample(lingerT)
                    : d.AccelerationOverLife?.Sample(emitterT) ?? Vector3.Zero;
                acceleration = Vector3.TransformNormal(acceleration, p.BirthFrame);
                // LTK does not carry a per-particle birthAcceleration term. Only the
                // emitter's acceleration (or keyed linger replacement) is integrated.
                p.Vel += acceleration * dt;

                Vector3 authoredVelocity = s.FinishedAt >= 0f && d.Linger?.Velocity is { } lingerVelocity
                    ? lingerVelocity.Sample(lingerT)
                    : d.VelocityOverLife?.Sample(emitterT) ?? Vector3.Zero;
                authoredVelocity = Vector3.TransformNormal(authoredVelocity, p.BirthFrame);
                Vector3 moving = p.Vel + authoredVelocity;
                Vector3 dragOverLife = s.FinishedAt >= 0f && d.Linger?.Drag is { } lingerDrag
                    ? lingerDrag.Sample(lingerT)
                    : d.DragOverLife?.Sample(emitterT) ?? Vector3.Zero;
                Vector3 drag = p.BirthDrag + dragOverLife;
                if (_dragMotion == VfxDragMotion.Analytic)
                    ApplyAnalyticDrag(ref p, ref moving, drag, dt);
                else
                    ApplySteppedDrag(ref p.Vel, ref moving, drag, dt);

                // Force fields read the particle where the step began. Bind/root travel and the
                // emitter-space EmitterPosition shift are added only after the field pass, as in LTK.
                ApplyFields(d.Fields, s, emitterT, preparedNoise, fieldOrigin, p.Pos, p.Serial, dt, ref moving, ref p.Vel);
                p.Pos += moving * dt + kinematicShift;

                // Birth angular velocity and acceleration always integrate. rotation0 is
                // a separate integrated value authored per 1/60 second and is gated only
                // by isRotationEnabled.
                p.BirthRotation += (p.RotationalVelocity + p.RotationalAcceleration * p.Age) * dt;
                if (d.IsRotationEnabled && d.RotationOverLife is { } rotationCurve)
                {
                    Vector3 rotationRate = rotationCurve.Sample(particleT);
                    if (s.FinishedAt >= 0f && d.Linger?.Rotation is { } lingerRotation)
                    {
                        // LTK samples LingerRotation on the particle's rewritten age01,
                        // unlike linger acceleration/velocity/drag which use emitter linger progress.
                        rotationRate = lingerRotation.Sample(particleT);
                    }
                    p.BirthRotation += rotationRate * (60f * MathF.PI / 180f) * dt;
                }

                if (d.ParticleUvScrollRate is { } uvScroll)
                    p.IntegratedUvOffset += uvScroll.Sample(particleT) * dt;
                if (d.ParticleUvRotateRate is { } uvRotate)
                    p.IntegratedUvRotation += uvRotate.Sample(particleT) * dt;
                if (d.TextureMultParticleUvScroll is { } multScroll)
                    p.IntegratedTextureMultUvOffset += multScroll.Sample(particleT) * dt;
                if (d.TextureMultParticleUvRotate is { } multRotate)
                    p.IntegratedTextureMultUvRotation += multRotate.Sample(particleT) * dt;

                Vector3 displacement = p.Pos - positionBeforeStep;
                p.Travel = dt > 0f ? displacement / dt : Vector3.Zero;
                s.Particles[i] = p;
                if (d.ChildParticleSet is { Children.Count: > 0 })
                    ParticleUpdated?.Invoke(this, d, LifecycleInfo(s, p, died: false));
            }

            return new EmitterStepContext(emitterT, preparedNoise, fieldOrigin);
        }

        private int EmitEmitter(
            EmitterState s,
            EmitterStepContext context,
            ref int availableParticleSlots)
        {
            VfxEmitterDefinition d = s.Def;
            float emitterT = context.EmitterT;
            bool emitting = !d.Disabled
                            && !IsStopped
                            && s.Age >= d.TimeBeforeFirstEmission
                            && (d.EmitterLifetime is not { } life || s.Age <= life);
            if (!emitting || (d.IsSingleParticle && s.BurstDone)) return -1;
            if (d.EmissionPeriod is { } period && !period.IsActive(s.Age - d.TimeBeforeFirstEmission))
            {
                // A cycle's pause discards emission debt without stopping existing particles.
                s.EmittedThrough = s.Age;
                return -1;
            }
            {
                // LTK samples rate directly. Legacy rateIsPeriod is retained in the model
                // for inspection but does not reinterpret the simulation rate.
                float rate = MathF.Max(0f, d.Rate.Sample(emitterT));
                if (!float.IsFinite(rate)) rate = 0f;
                float owed = MathF.Min(
                    MathF.Truncate(MathF.Max(0f, s.Age - s.EmittedThrough) * rate),
                    MathF.Truncate(rate * 0.33f) + 1f);
                int requestedCount = owed >= int.MaxValue ? int.MaxValue : (int)MathF.Max(0f, owed);
                if (!s.InitialEmissionDone)
                {
                    if (d.IsSingleParticle)
                    {
                        // Riot stores the authored burst count through a uint16 lane, so
                        // values above 65535 wrap rather than clamp.
                        int wrapped = (int)(Math.Truncate((double)rate) % (ushort.MaxValue + 1d));
                        requestedCount = Math.Max(1, wrapped);
                    }
                    else
                    {
                        requestedCount = Math.Max(1, requestedCount);
                    }
                }
                if (d.Trail?.MaxAddedPerFrame is > 0)
                    requestedCount = Math.Min(requestedCount, d.Trail.MaxAddedPerFrame);
                if (requestedCount <= 0) return -1;

                // LTK advances a trail odometer only when an emission batch is actually due,
                // before attempting the shared-pool spawn. TranslationOverride and shape offset
                // do not participate; only EmitterPosition stood on the spawn frame does.
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
                for (int born = 0; born < actualCount; born++) Spawn(s, emitterT);

                if (actualCount < requestedCount)
                {
                    // emit.ts draws the failed particle's roll (and its per-particle chance)
                    // before pool.spawn reports a full pool, then breaks the batch.
                    _rng.NextUnitFloat();
                    if (!d.ParticlesShareRandomValue) _rng.NextUnitFloat();
                }

                s.InitialEmissionDone = true;
                s.BurstDone = d.IsSingleParticle;
                s.EmittedThrough = rate > 0f ? s.EmittedThrough + requestedCount / rate : s.Age;
                return actualCount > 0 ? firstNewborn : -1;
            }
        }

        private readonly record struct EmitterStepContext(
            float EmitterT,
            PreparedNoiseField[] PreparedNoise,
            Vector3 FieldOrigin);

        private static void ApplyAnalyticDrag(ref Particle particle, ref Vector3 moving, Vector3 drag, float dt)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                float dragAxis = axis == 0 ? drag.X : axis == 1 ? drag.Y : drag.Z;
                if (dragAxis <= 0f)
                {
                    Vector3 kept = particle.Vel;
                    ApplySteppedDragAxis(ref kept, ref moving, dragAxis, dt, axis);
                    particle.Vel = kept;
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

        private static void ApplySteppedDragAxis(ref Vector3 kept, ref Vector3 moving, float dragAxis, float dt, int axis)
        {
            float movingAxis = axis == 0 ? moving.X : axis == 1 ? moving.Y : moving.Z;
            if (dragAxis == 0f || movingAxis == 0f) return;
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
