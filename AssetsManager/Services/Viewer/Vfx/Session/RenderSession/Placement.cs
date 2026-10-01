using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Session
{
    public sealed partial class VfxRenderSession
    {
        public void ApplyRigTransform()
        {
            if (_graphs.Count == 0 || _graphAttachments.Count > 0 || _spellSteps.Count > 0 || _activeSystem == null) return;

            var step = VfxRigMotion.Evaluate(
                _rigSettings,
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
