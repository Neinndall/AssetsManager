using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Session
{
    public sealed partial class VfxRenderSession
    {
        private System.Action<VfxPlaybackGraphRuntime, double> _prepareGraphStep;

        private void PrepareGraphStep(VfxPlaybackGraphRuntime graph, double time)
        {
            if (_spellSteps.TryGetValue(graph, out var spell))
            {
                Matrix4x4 placement = SpellTransformAt(spell, time);
                _graphPlacements[graph] = placement;
                Matrix4x4 world = RootAuthoredWorld(graph);
                graph.SetTransform(placement * world, Matrix4x4.CreateTranslation(placement.Translation) * world);
                graph.SetTarget(Vector3.Transform(spell.To, world));
                graph.IsStopped = time >= spell.StopTime;
            }
            else if (_graphAttachments.Count == 0 && _spellSteps.Count == 0 && _activeSystem != null)
            {
                var step = VfxRigMotion.Evaluate(EffectiveRigSettings(), time, RigDuration);
                _graphPlacements[graph] = step.Transform;
                Matrix4x4 world = RootAuthoredWorld(graph);
                graph.SetTransform(step.Transform * world, Matrix4x4.CreateTranslation(step.Origin) * world);
                graph.SetTarget(Vector3.Transform(step.Target, world));
                graph.IsStopped = step.IsStopped;
            }
            if (_boneTransformSampler != null)
                ApplyBoneTransforms((name, hash) => _boneTransformSampler(time, name, hash), graph, time);
        }

        public void ApplyRigTransform()
        {
            if (_graphs.Count == 0 || _graphAttachments.Count > 0 || _spellSteps.Count > 0 || _activeSystem == null) return;

            var step = VfxRigMotion.Evaluate(
                EffectiveRigSettings(),
                _activeSystem.CurrentTime,
                RigDuration,
                _lastRigOrigin);

            _lastRigOrigin = step.Origin;

            foreach (var graph in _graphs)
            {
                _graphPlacements[graph] = step.Transform;
                Matrix4x4 authoredWorld = RootAuthoredWorld(graph);
                Matrix4x4 orientationRoot = Matrix4x4.CreateTranslation(step.Origin) * authoredWorld;
                graph.SetTransform(step.Transform * authoredWorld, orientationRoot);
                graph.SetTarget(Vector3.Transform(step.Target, authoredWorld));
                graph.IsStopped = step.IsStopped;
            }
        }

        private VfxRigSettings EffectiveRigSettings()
            => _rigSettings.Target == null && _rigSettings.MotionKind == VfxRigMotionKind.Still &&
                VfxSystemRigResolver.BeamReach(_activeSystem?.Definition) is { } reach
                    ? _rigSettings with { Target = new Vector3(0, 0, reach) } : _rigSettings;

        private void ApplySpellTransforms(double time)
        {
            foreach ((VfxPlaybackGraphRuntime graph, VfxSpellPlaybackStep step) in _spellSteps)
            {
                Matrix4x4 placement = SpellTransformAt(step, time);
                _graphPlacements[graph] = placement;
                Matrix4x4 authoredWorld = RootAuthoredWorld(graph);
                Matrix4x4 orientationRoot = Matrix4x4.CreateTranslation(placement.Translation) * authoredWorld;
                graph.SetTransform(placement * authoredWorld, orientationRoot);
                graph.SetTarget(Vector3.Transform(step.To, authoredWorld));
                graph.IsStopped = time >= step.StopTime;
                graph.SetJointTransformProvider(null);
            }
        }

        internal static Matrix4x4 SpellTransformAt(VfxSpellPlaybackStep step, double time)
        {
            if (step is null) return Matrix4x4.Identity;
            if (step.Motion == VfxSpellPlaybackMotion.Path && step.Flight != null)
            {
                (Vector3 position, Vector3 direction) = step.Flight.Sample(time - step.StartTime);
                return VfxRigMotion.FlightTransform(position, direction);
            }
            return step.Motion == VfxSpellPlaybackMotion.Path
                ? VfxRigMotion.PathTransform(step.From, step.To, time, step.StartTime, step.StopTime)
                : Matrix4x4.CreateTranslation(step.To);
        }
    }
}
