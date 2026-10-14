using System;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;

namespace AssetsManager.Services.Viewer.Vfx.Session
{
    public sealed partial class VfxRenderSession
    {
        public void Update(float deltaTime)
        {
            if (!_isPlaying || _activeSystem == null) return;
            float frameTime = NormalizeFrameTime(deltaTime);
            if (frameTime <= 0f) return;
            if (WaitForInitialResources()) return;

            float speed = NormalizePlaybackSpeed(_activeSystem.Speed);
            float elapsed = frameTime * speed;

            AdvanceTo(_activeSystem.CurrentTime + elapsed, fixedSeekSteps: false);

            if (_usesStandaloneRig && _rigSettings.IsLooping)
                return;

            if (ShouldFinishPlayback(
                    HasFinitePlaybackDuration,
                    _activeSystem.CurrentTime,
                    RigDuration,
                    _graphs.All(graph => graph.IsComplete)))
            {
                if (HasFinitePlaybackDuration)
                    _activeSystem.CurrentTime = RigDuration;
                _isPlaying = false;
            }
        }

        private bool WaitForInitialResources()
        {
            // Only a newly opened standalone System waits. Clip/spell clocks and explicit seeks
            // retain their authored synchronization; uploads continue on the render callback.
            if (!_usesStandaloneRig || !_waitForInitialResources) return false;
            if (VfxGpuResourceUploader.HasPendingResources(_graphs))
            {
                long now = _timeProvider.GetTimestamp();
                _initialResourceWaitStarted ??= now;
                if (_timeProvider.GetElapsedTime(_initialResourceWaitStarted.Value, now) < InitialResourceWaitLimit)
                    return true;
            }
            _waitForInitialResources = false;
            _initialResourceWaitStarted = null;
            return false;
        }

        internal static bool ShouldFinishPlayback(
            bool hasFiniteDuration,
            double currentTime,
            double totalDuration,
            bool graphIsComplete)
            => hasFiniteDuration
                ? currentTime >= totalDuration
                : graphIsComplete;

        internal static float NormalizeFrameTime(float deltaTime)
        {
            if (!float.IsFinite(deltaTime) || deltaTime <= 0f) return 0f;
            return Math.Min(deltaTime, 0.1f);
        }

        internal static float NormalizePlaybackSpeed(double speed)
        {
            if (!double.IsFinite(speed)) return 1f;
            return (float)Math.Clamp(speed, 0.05d, 2d);
        }

        public void SetPinnedBirthChance(float? chance)
        {
            if (!_usesStandaloneRig) return;
            if (chance.HasValue && !float.IsFinite(chance.Value)) return;
            if (_pinnedBirthChance == chance) return;

            _pinnedBirthChance = chance;
            foreach (VfxPlaybackGraphRuntime graph in _graphs)
                graph.SetPinnedBirthChance(chance);

            // Existing particles stay as they were. Only future births use the new chance, while
            // clearing checkpoints guarantees a later backward seek cannot restore births sampled
            // under the previous pin.
            ClearCheckpoints();
        }

        public void Seek(double seconds)
        {
            if (_activeSystem == null || !double.IsFinite(seconds)) return;
            if (seconds > 0d)
            {
                _waitForInitialResources = false;
                _initialResourceWaitStarted = null;
            }
            SeekExact(QuantizeLtkSeek(seconds));
        }

        internal static double QuantizeLtkSeek(double seconds)
        {
            if (!double.IsFinite(seconds)) return 0d;
            double wanted = Math.Clamp(seconds, 0d, MaximumSeekTime);
            // JavaScript Math.round is half-up for the non-negative seek domain.
            double steps = Math.Floor((wanted / SeekStep) + 0.5d);
            return steps * SeekStep;
        }

        private void SeekExact(double target)
        {
            if (_activeSystem == null || !double.IsFinite(target)) return;
            target = Math.Max(0d, target);

            if (target + 1e-9 >= _activeSystem.CurrentTime)
            {
                LastSeekRestoreTime = _activeSystem.CurrentTime;
                AdvanceTo(target, fixedSeekSteps: true);
                return;
            }

            if (!TryRestoreCheckpoint(target))
            {
                LastSeekRestoreTime = 0d;
                ResetSimulationToStart();
            }
            AdvanceTo(target, fixedSeekSteps: true);
        }

        private void ResetSimulationToStart()
        {
            _lastRigOrigin = null;
            foreach (VfxPlaybackGraphRuntime graph in _graphs) graph.Reset();
            foreach (var attachment in _graphAttachments.Values) attachment.HasBoneTransform = false;
            _activeSystem.CurrentTime = 0;
            ApplyRigTransform();
            ApplySpellTransforms(0d);
            if (_boneTransformSampler != null)
                ApplyBoneTransforms((name, hash) => _boneTransformSampler(0, name, hash));
            else
                ApplyBoneTransforms(_boneTransformProvider);
        }

        /// <summary>
        /// Advances an animation-driven VFX session to the canonical clip time without
        /// running an independent playback clock. Backward jumps rebuild deterministically.
        /// </summary>
        public void SynchronizeTo(double seconds)
        {
            if (_activeSystem == null || !double.IsFinite(seconds)) return;
            double target = Math.Max(0d, seconds);
            if (target + 1e-9 < _activeSystem.CurrentTime)
            {
                // Animation-follow seeks use the scene clock itself rather than the VFX
                // transport's 60 Hz scrub quantization.
                SeekExact(target);
                return;
            }
            AdvanceTo(target, fixedSeekSteps: false);
        }
    }
}
