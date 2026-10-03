using System;
using System.Numerics;
using AssetsManager.Utils.Rendering;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    public sealed partial class VfxPlaybackRuntime
    {
        internal sealed record EmitterSnapshot(
            Vector3 BasePos,
            Vector3? StepStartBasePos,
            Vector3 FieldBasePos,
            Vector3 SystemOrigin,
            Vector3 SystemTarget,
            Vector3 PlacementRight,
            Vector3 PlacementUp,
            Vector3 PlacementForward,
            float SharedRandom,
            bool SharedRandomRolled,
            float EmittedThrough,
            float Age,
            float FinishedAt,
            bool BurstDone,
            bool InitialEmissionDone,
            float TrailDistance,
            Vector3? TrailSpawnedAt,
            int InstanceBufferLength,
            float[] NoiseLast,
            int[] NoiseFired,
            Particle[] Particles);

        internal sealed record Snapshot(
            float CurrentTime,
            uint InitialRandomState,
            uint RandomState,
            uint ParticleSerial,
            Matrix4x4 WorldTransform,
            Matrix4x4 OrientationRootTransform,
            Matrix4x4 InverseWorldTransform,
            Vector3 PendingOriginDelta,
            bool NeedsBuildUp,
            bool IsKilled,
            bool IsStopped,
            float ConfiguredStartDelay,
            float StartDelay,
            EmitterSnapshot[] Emitters,
            long Bytes);

        internal Snapshot CaptureSnapshot()
        {
            var emitters = new EmitterSnapshot[_emitters.Count];
            long bytes = 256;
            for (int index = 0; index < _emitters.Count; index++)
            {
                EmitterState state = _emitters[index];
                Particle[] particles = state.Particles.ToArray();
                float[] noiseLast = (float[])state.NoiseLast.Clone();
                int[] noiseFired = (int[])state.NoiseFired.Clone();
                emitters[index] = new EmitterSnapshot(
                    state.BasePos,
                    state.StepStartBasePos,
                    state.FieldBasePos,
                    state.SystemOrigin,
                    state.SystemTarget,
                    state.PlacementRight,
                    state.PlacementUp,
                    state.PlacementForward,
                    state.SharedRandom,
                    state.SharedRandomRolled,
                    state.EmittedThrough,
                    state.Age,
                    state.FinishedAt,
                    state.BurstDone,
                    state.InitialEmissionDone,
                    state.TrailDistance,
                    state.TrailSpawnedAt,
                    state.InstanceBufferCapacity,
                    noiseLast,
                    noiseFired,
                    particles);
                // Conservative accounting keeps the session checkpoint budget bounded without
                // depending on CLR struct layout details.
                bytes += 192L + particles.LongLength * 256L + noiseLast.LongLength * sizeof(float) +
                         noiseFired.LongLength * sizeof(int);
            }

            return new Snapshot(
                CurrentTime,
                _initialRandomState,
                _rng.State,
                _particleSerial,
                _worldTransform,
                _orientationRootTransform,
                _inverseWorldTransform,
                _pendingOriginDelta,
                _needsBuildUp,
                _isKilled,
                IsStopped,
                _configuredStartDelay,
                _startDelay,
                emitters,
                bytes);
        }

        internal void RestoreSnapshot(Snapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.Emitters.Length != _emitters.Count)
                throw new InvalidOperationException("VFX snapshot emitter layout no longer matches the runtime definition.");

            CurrentTime = snapshot.CurrentTime;
            _initialRandomState = snapshot.InitialRandomState;
            _rng = VfxLtkRandom.FromState(snapshot.RandomState);
            _particleSerial = snapshot.ParticleSerial;
            _worldTransform = snapshot.WorldTransform;
            _orientationRootTransform = snapshot.OrientationRootTransform;
            _inverseWorldTransform = snapshot.InverseWorldTransform;
            _pendingOriginDelta = snapshot.PendingOriginDelta;
            _needsBuildUp = snapshot.NeedsBuildUp;
            _isKilled = snapshot.IsKilled;
            IsStopped = snapshot.IsStopped;
            _configuredStartDelay = snapshot.ConfiguredStartDelay;
            _startDelay = snapshot.StartDelay;

            int live = 0;
            Matrix4x4 systemOrientation = VectorMathUtils.OrientationOnly(_worldTransform);
            for (int index = 0; index < _emitters.Count; index++)
            {
                EmitterState state = _emitters[index];
                EmitterSnapshot saved = snapshot.Emitters[index];
                state.BasePos = saved.BasePos;
                state.StepStartBasePos = saved.StepStartBasePos;
                state.FieldBasePos = saved.FieldBasePos;
                state.SystemOrigin = saved.SystemOrigin;
                state.SystemOrientation = systemOrientation;
                state.SystemTarget = saved.SystemTarget;
                state.PlacementRight = saved.PlacementRight;
                state.PlacementUp = saved.PlacementUp;
                state.PlacementForward = saved.PlacementForward;
                state.SharedRandom = saved.SharedRandom;
                state.SharedRandomRolled = saved.SharedRandomRolled;
                state.EmittedThrough = saved.EmittedThrough;
                state.Age = saved.Age;
                state.FinishedAt = saved.FinishedAt;
                state.BurstDone = saved.BurstDone;
                state.InitialEmissionDone = saved.InitialEmissionDone;
                state.TrailDistance = saved.TrailDistance;
                state.TrailSpawnedAt = saved.TrailSpawnedAt;
                state.NoiseLast = (float[])saved.NoiseLast.Clone();
                state.NoiseFired = (int[])saved.NoiseFired.Clone();
                state.Particles.Clear();
                state.Particles.AddRange(saved.Particles);
                state.ResetInstanceBuffer(saved.InstanceBufferLength);
                state.RenderTime = CurrentTime;
                live += state.Particles.Count;
            }
            LiveParticleCount = live;
        }
    }
}
