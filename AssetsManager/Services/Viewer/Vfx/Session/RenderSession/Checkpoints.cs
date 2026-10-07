using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Runtime;

namespace AssetsManager.Services.Viewer.Vfx.Session
{
    public sealed partial class VfxRenderSession
    {
        private void AdvanceTo(double target, bool fixedSeekSteps)
        {
            if (!double.IsFinite(target)) return;
            while (_activeSystem.CurrentTime < target)
            {
                double previous = _activeSystem.CurrentTime;
                // LTK plays one variable step per rendered frame. Only seek/replay advances in
                // fixed 1/60 s slices; checkpoints themselves never subdivide the physics step.
                double next = fixedSeekSteps
                    ? Math.Min(target, previous + SeekStep)
                    : target;
                foreach (var kill in _scheduledEffectKills)
                    if (kill.Time > previous && kill.Time < next) next = kill.Time;
                foreach (double stopTime in _graphStopTimes.Values)
                    if (stopTime > previous && stopTime < next) next = stopTime;
                foreach (GraphAttachmentInfo attachment in _graphAttachments.Values)
                    if (attachment.StartTime > previous && attachment.StartTime < next) next = attachment.StartTime;

                KillGraphsAt(previous);

                // Keep LTK's whole-frame replay policy at a crossed rig boundary.
                // Check cycle numbers because a complete cycle can leave the same end phase.
                bool rigWrapped = DidRigWrap(previous, next);
                if (rigWrapped)
                    ReplayRigLoopAtStart();

                _activeSystem.CurrentTime = next;
                ApplyRigTransform();
                ApplySpellTransforms(next);
                if (_boneTransformSampler != null)
                    ApplyBoneTransforms((name, hash) => _boneTransformSampler(next, name, hash));
                else if (_boneTransformProvider != null)
                    ApplyBoneTransforms(_boneTransformProvider);
                foreach (var graph in _graphs) graph.Update((float)(next - previous));
                KillGraphsAt(next);
                TryCaptureCheckpoint(next);
            }
        }

        private bool DidRigWrap(double previous, double next)
        {
            if (!_usesStandaloneRig ||
                !_rigSettings.IsLooping ||
                !(RigDuration > 0d) ||
                !double.IsFinite(RigDuration))
            {
                return false;
            }

            double span = VfxRigMotion.Evaluate(_rigSettings, previous, RigDuration).TotalSpan;
            return span > 0d && double.IsFinite(span) &&
                Math.Floor(next / span) > Math.Floor(previous / span);
        }

        private void ReplayRigLoopAtStart()
        {
            VfxRigStep start = VfxRigMotion.Evaluate(_rigSettings, 0d, RigDuration);
            _lastRigOrigin = start.Origin;

            foreach (VfxPlaybackGraphRuntime graph in _graphs)
            {
                _graphPlacements[graph] = start.Transform;
                Matrix4x4 authoredWorld = RootAuthoredWorld(graph);
                Matrix4x4 startTransform = start.Transform * authoredWorld;
                Matrix4x4 orientationRoot = Matrix4x4.CreateTranslation(start.Origin) * authoredWorld;
                graph.ReplayLoop(startTransform, orientationRoot);
                graph.SetTarget(Vector3.Transform(start.Target, authoredWorld));
                graph.IsStopped = start.IsStopped;
            }
        }

        private void ClearCheckpoints()
        {
            _checkpoints.Clear();
            _checkpointBytes = 0;
            LastSeekRestoreTime = 0d;
        }

        private SessionSnapshot CaptureSessionSnapshot()
        {
            var placements = new Matrix4x4[_graphs.Count];
            var graphs = new VfxPlaybackGraphRuntime.Snapshot[_graphs.Count];
            var attachments = new AttachmentSnapshot[_graphs.Count];
            long bytes = 256;

            for (int index = 0; index < _graphs.Count; index++)
            {
                VfxPlaybackGraphRuntime graph = _graphs[index];
                placements[index] = _graphPlacements.GetValueOrDefault(graph, Matrix4x4.Identity);
                VfxPlaybackGraphRuntime.Snapshot graphState = graph.CaptureSnapshot();
                graphs[index] = graphState;
                bytes += 128L + graphState.Bytes;

                if (_graphAttachments.TryGetValue(graph, out GraphAttachmentInfo attachment))
                {
                    attachments[index] = new AttachmentSnapshot(
                        attachment.HasBoneTransform,
                        attachment.BoneTransform);
                    bytes += 80;
                }
            }

            return new SessionSnapshot(
                _activeSystem.CurrentTime,
                _lastRigOrigin,
                placements,
                graphs,
                attachments,
                bytes);
        }

        private void RestoreSessionSnapshot(SessionSnapshot snapshot)
        {
            if (snapshot.Graphs.Length != _graphs.Count)
                throw new InvalidOperationException("VFX session checkpoint graph layout no longer matches the active session.");

            _activeSystem.CurrentTime = snapshot.Time;
            _lastRigOrigin = snapshot.LastRigOrigin;
            for (int index = 0; index < _graphs.Count; index++)
            {
                VfxPlaybackGraphRuntime graph = _graphs[index];
                _graphPlacements[graph] = snapshot.Placements[index];
                graph.RestoreSnapshot(snapshot.Graphs[index]);

                AttachmentSnapshot savedAttachment = snapshot.Attachments[index];
                if (savedAttachment is not null && _graphAttachments.TryGetValue(graph, out GraphAttachmentInfo attachment))
                {
                    attachment.HasBoneTransform = savedAttachment.HasBoneTransform;
                    attachment.BoneTransform = savedAttachment.BoneTransform;
                }
            }
        }

        private void TryCaptureCheckpoint(double time)
        {
            // LTK stores the first state that crosses each quarter-second mark; it does not
            // force the simulation to land exactly on the mark just to make a checkpoint.
            int mark = (int)Math.Floor(time / CheckpointInterval + 1e-9);
            if (mark < 1 || mark > MaximumCheckpointMarks || _checkpoints.ContainsKey(mark)) return;

            SessionSnapshot state = CaptureSessionSnapshot();
            long size = state.Bytes;
            if (size <= 0 || size > CheckpointBudgetBytes) return;

            int remaining = MaximumCheckpointMarks - mark + 1;
            int stride = 1;
            while (Math.Ceiling(remaining / (double)stride) * size > CheckpointBudgetBytes)
                stride *= 2;
            if (mark % stride != 0) return;

            if (_checkpointBytes + size > CheckpointBudgetBytes)
                ThinCheckpoints(size);
            if (_checkpointBytes + size > CheckpointBudgetBytes) return;

            var checkpoint = new Checkpoint(mark, state);
            _checkpoints[mark] = checkpoint;
            _checkpointBytes += checkpoint.Bytes;
        }

        private void ThinCheckpoints(long requiredBytes)
        {
            for (int stride = 2; _checkpointBytes + requiredBytes > CheckpointBudgetBytes && stride <= 512; stride *= 2)
            {
                foreach (int mark in _checkpoints.Keys.Where(mark => mark % stride != 0).ToArray())
                {
                    _checkpointBytes -= _checkpoints[mark].Bytes;
                    _checkpoints.Remove(mark);
                }
            }
        }

        private bool TryRestoreCheckpoint(double target)
        {
            int mark = Math.Min(MaximumCheckpointMarks, (int)Math.Floor(target / CheckpointInterval + 1e-9));
            for (; mark >= 1; mark--)
            {
                if (!_checkpoints.TryGetValue(mark, out Checkpoint checkpoint)) continue;
                // A variable playback frame may have crossed this mark after the exact target.
                // LTK also rejects checkpoints whose recorded seek step lies beyond the request.
                if (checkpoint.Time > target + 1e-9) continue;
                RestoreSessionSnapshot(checkpoint.State);
                LastSeekRestoreTime = checkpoint.Time;
                return true;
            }
            return false;
        }

        private void KillGraphsAt(double seconds)
        {
            foreach (var (graph, stopTime) in _graphStopTimes)
            {
                if (seconds >= stopTime) graph.IsStopped = true;
            }
            foreach (var kill in _scheduledEffectKills)
                if (seconds >= kill.Time)
                    foreach (var (graph, attachment) in _graphAttachments)
                        if (attachment.EffectKey == kill.EffectKey && attachment.StartTime <= kill.Time)
                            graph.Kill();
        }
    }
}
