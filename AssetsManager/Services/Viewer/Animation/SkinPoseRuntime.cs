using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Animation;

/// <summary>Pose dynamics shared by mesh skinning and VFX. Clip sampling is independent of draw order.</summary>
internal sealed class SkinPoseRuntime
{
    private readonly SkinPoseDefinition _definition;
    private readonly int[] _parents;
    private readonly int[] _order;
    private readonly Func<uint, int> _joint;
    private readonly SkinSpringRuntime[] _springs;
    private readonly SkinConformRuntime[] _conforms;
    private readonly int[] _conformSlots;
    private readonly Matrix4x4[] _world;
    private readonly Dictionary<AnimationLockOrientationCue, Quaternion> _locks = new();
    private IReadOnlyList<AnimationClipTimedCue> _cues = Array.Empty<AnimationClipTimedCue>();
    private IReadOnlyList<AnimationMaskDefinition> _masks = Array.Empty<AnimationMaskDefinition>();
    private Matrix4x4 _root = Matrix4x4.Identity;
    private Matrix4x4 _previousRoot = Matrix4x4.Identity;
    private bool _hasRoot;
    private bool _live;
    private float _lastTime = float.NaN;
    private float _pendingStep;
    private Matrix4x4[] _liveConform;
    private Matrix4x4[][] _take;
    private float _takeDuration;
    private object _takeKey;

    internal SkinPoseRuntime(SkinPoseDefinition definition, int[] parents, int[] order, Func<uint, int> joint)
    {
        _definition = definition;
        _parents = parents;
        _order = order;
        _joint = joint;
        _springs = definition.Springs.Select(model => new SkinSpringRuntime(model)).ToArray();
        _conforms = definition.Conforms.Select(model => new SkinConformRuntime(model, parents, joint)).ToArray();
        _conformSlots = _conforms.SelectMany(conform => conform.AffectedSlots).Distinct().ToArray();
        _world = new Matrix4x4[parents.Length];
    }

    internal void SetCues(IReadOnlyList<AnimationClipTimedCue> cues, IReadOnlyList<AnimationMaskDefinition> masks)
    {
        _cues = cues ?? Array.Empty<AnimationClipTimedCue>();
        _masks = masks ?? Array.Empty<AnimationMaskDefinition>();
        _take = null;
        ResetLive();
    }

    internal void Advance(float time, Matrix4x4 root, float dt)
    {
        if (!Matrix4x4.Decompose(root, out Vector3 scale, out _, out Vector3 position) ||
            !SkinPoseReader.IsFinite(scale) || !SkinPoseReader.IsFinite(position)) root = Matrix4x4.Identity;
        bool seek = float.IsFinite(_lastTime) && (time < _lastTime || time - _lastTime > 0.25f);
        if (seek) ResetLive();
        _lastTime = time;
        if (_hasRoot && root != _root) _live = true;
        _previousRoot = _hasRoot ? _root : root;
        _root = root;
        _hasRoot = true;
        foreach (AnimationLockOrientationCue cue in _cues.OfType<AnimationLockOrientationCue>())
        {
            if (AnimationPoseCues.Active(cue, time) && !_locks.ContainsKey(cue))
            {
                Matrix4x4.Decompose(_previousRoot, out _, out Quaternion captured, out _);
                _locks[cue] = captured;
            }
        }
        _pendingStep = _live && float.IsFinite(dt) ? Math.Clamp(dt, 0f, 1f / 15f) : 0f;

        if (_pendingStep > 0f)
        {
            // Bound integration at 60 Hz while distributing the unit's movement across substeps.
            int count = Math.Max(1, (int)MathF.Ceiling(_pendingStep * 60f));
            Matrix4x4.Decompose(_previousRoot, out Vector3 beforeScale, out Quaternion beforeRotation, out Vector3 beforePosition);
            Matrix4x4.Decompose(root, out Vector3 nextScale, out Quaternion nextRotation, out Vector3 nextPosition);
            for (int step = 1; step <= count; step++)
            {
                float t = (float)step / count;
                Matrix4x4 moved = Matrix4x4.CreateScale(Vector3.Lerp(beforeScale, nextScale, t)) *
                    Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(beforeRotation, nextRotation, t)) *
                    Matrix4x4.CreateTranslation(Vector3.Lerp(beforePosition, nextPosition, t));
                foreach (SkinSpringRuntime spring in _springs) spring.Step(moved, _pendingStep / count);
            }
        }
        else if (!_live)
        {
            // Two frames of warm-up precede the first movement, as in the game reader.
            foreach (SkinSpringRuntime spring in _springs) spring.Step(root, 1f / 60f);
        }
    }

    internal void ResetLive()
    {
        foreach (SkinSpringRuntime spring in _springs) spring.Reset();
        foreach (SkinConformRuntime conform in _conforms) conform.Reset();
        _locks.Clear();
        _live = false;
        _liveConform = null;
        _pendingStep = 0f;
        _hasRoot = false;
        _lastTime = float.NaN;
    }

    internal void ApplyConforms(float time, float duration, object clipKey, Matrix4x4[] locals,
        Action<float, Matrix4x4[]> sampleBase)
    {
        if (_conforms.Length == 0) return;
        if (_live)
        {
            if (_pendingStep > 0f)
            {
                Matrix4x4[] world = _world;
                Action compose = () => Compose(locals, world, _root);
                compose();
                float before = Math.Max(time - _pendingStep, 0f);
                foreach (SkinConformRuntime conform in _conforms)
                {
                    CrossMask(conform, before, time);
                    conform.Step(locals, world, _parents, _root, _pendingStep, MaskWeight, compose);
                }
                _liveConform ??= new Matrix4x4[locals.Length];
                locals.CopyTo(_liveConform, 0);
            }
            else if (_liveConform != null) CopyRotations(_liveConform, locals);
            return;
        }

        if (_take == null || !ReferenceEquals(clipKey, _takeKey) || duration != _takeDuration)
            Bake(duration, clipKey, sampleBase);
        if (_take is not { Length: > 0 }) return;
        float frame = AnimationService.FoldAnimationTime(time, duration) / duration * _take.Length;
        int at = Math.Min((int)frame, _take.Length - 1), next = (at + 1) % _take.Length;
        float blend = frame - at;
        foreach (int slot in _conformSlots)
        {
            if (!Matrix4x4.Decompose(locals[slot], out Vector3 scale, out _, out Vector3 position) ||
                !Matrix4x4.Decompose(_take[at][slot], out _, out Quaternion a, out _) ||
                !Matrix4x4.Decompose(_take[next][slot], out _, out Quaternion b, out _)) continue;
            locals[slot] = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(a, b, blend)) *
                Matrix4x4.CreateTranslation(position);
        }
    }

    private void Bake(float duration, object key, Action<float, Matrix4x4[]> sampleBase)
    {
        _takeDuration = duration;
        _takeKey = key;
        if (!(duration > 0f) || !float.IsFinite(duration)) { _take = Array.Empty<Matrix4x4[]>(); return; }
        // At most 16 MiB of matrices per actor. Long clips lower the bake rate rather than grow without bound.
        int frames = Math.Clamp((int)Math.Min(Math.Ceiling(duration * 60d), int.MaxValue), 1,
            Math.Max(1, Math.Min(4096, 262144 / Math.Max(1, _parents.Length))));
        float dt = duration / frames;
        _take = new Matrix4x4[frames][];
        var locals = new Matrix4x4[_parents.Length];
        var world = new Matrix4x4[_parents.Length];
        Action compose = () => Compose(locals, world, Matrix4x4.Identity);
        foreach (SkinConformRuntime conform in _conforms) conform.Reset();
        for (int pass = 0; pass < 3; pass++)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                float time = frame * dt;
                sampleBase(time, locals);
                compose();
                foreach (SkinConformRuntime conform in _conforms)
                {
                    CrossMask(conform, frame == 0 ? -1f : (frame - 1) * dt, time);
                    conform.Step(locals, world, _parents, Matrix4x4.Identity, dt, MaskWeight, compose);
                }
                if (pass == 2) _take[frame] = (Matrix4x4[])locals.Clone();
            }
            foreach (SkinConformRuntime conform in _conforms) conform.BlendMask(conform.Definition.Mask, 0f);
        }
    }

    private void CrossMask(SkinConformRuntime conform, float before, float time)
    {
        foreach (AnimationConformToPathCue cue in _cues.OfType<AnimationConformToPathCue>())
        {
            if (cue.AtSeconds > before && cue.AtSeconds <= time) conform.BlendMask(cue.MaskHash, cue.BlendInSeconds);
            if (cue.UntilSeconds is { } end && end > before && end <= time)
                conform.BlendMask(conform.Definition.Mask, cue.BlendOutSeconds);
        }
    }

    private float MaskWeight(uint hash, int slot)
    {
        AnimationMaskDefinition mask = _masks.FirstOrDefault(each => each.Hash == hash);
        return mask == null ? 1f : slot < mask.Weights.Count && float.IsFinite(mask.Weights[slot])
            ? Math.Clamp(mask.Weights[slot], 0f, 1f) : 0f;
    }

    private void CopyRotations(Matrix4x4[] from, Matrix4x4[] into)
    {
        foreach (int slot in _conformSlots)
        {
            if (!Matrix4x4.Decompose(into[slot], out Vector3 scale, out _, out Vector3 position) ||
                !Matrix4x4.Decompose(from[slot], out _, out Quaternion rotation, out _)) continue;
            into[slot] = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
        }
    }

    internal void ApplyLocks(float time, Matrix4x4[] locals)
    {
        if (!_live) return;
        Matrix4x4.Decompose(_root, out _, out Quaternion facing, out _);
        foreach (AnimationLockOrientationCue cue in _cues.OfType<AnimationLockOrientationCue>())
        {
            int slot = _joint(cue.JointHash);
            if (slot < 0) continue;
            bool active = AnimationPoseCues.Active(cue, time);
            if (!_locks.TryGetValue(cue, out Quaternion held)) continue;
            float weight = active ? 1f : cue.UntilSeconds is { } end && cue.BlendOutSeconds > 0f
                ? Math.Clamp(1f - ((time - (float)end) / cue.BlendOutSeconds), 0f, 1f) : 0f;
            if (weight <= 0f) continue;
            if (!Matrix4x4.Decompose(locals[slot], out Vector3 scale, out Quaternion rotation, out Vector3 position)) continue;
            Quaternion delta = Quaternion.Slerp(Quaternion.Identity, Quaternion.Inverse(facing) * held, weight);
            rotation = Quaternion.Normalize(delta * rotation);
            locals[slot] = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
        }
    }

    internal void ApplyPost(float time, Matrix4x4[] locals, Matrix4x4[] worlds)
    {
        if (_springs.Length == 0 && _definition.Orientations.Count == 0) return;
        var world = _world;
        Action compose = () => Compose(locals, world, _root);
        compose();
        foreach (SkinSpringRuntime spring in _springs)
        {
            int slot = _joint(spring.Definition.Joint);
            if (slot < 0) continue;
            float weight = spring.Definition.DefaultOn ? 1f : 0f;
            foreach (AnimationSpringCue cue in _cues.OfType<AnimationSpringCue>())
            {
                if (AnimationPoseCues.Active(cue, time) &&
                    (cue.SpringHash == 0 || cue.SpringHash == spring.Definition.Name))
                    weight = spring.Definition.DefaultOn ? 0f : 1f;
            }
            locals[slot] = spring.Apply(locals[slot], _parents[slot] < 0 ? _root : world[_parents[slot]], weight);
            compose();
        }
        foreach (SkinOrientationDefinition orientation in _definition.Orientations)
        {
            // Dynamic logic drivers need a live unit. Preserve the clip until their source can be evaluated.
            if (orientation.ConstantSource is not { } source) continue;
            float weight = OrientationWeight(time, orientation.DefaultOn);
            foreach (uint hash in orientation.Joints)
            {
                int slot = _joint(hash);
                if (slot < 0 || weight <= 0f) continue;
                Vector3 direction = source - (orientation.OrientationType == 1 ? world[slot].Translation : Vector3.Zero);
                if (direction.LengthSquared() <= 1e-12f) continue;
                Matrix4x4.Decompose(_parents[slot] < 0 ? _root : world[_parents[slot]], out _, out Quaternion parent, out _);
                if (!Matrix4x4.Decompose(locals[slot], out Vector3 scale, out Quaternion rotation, out Vector3 position)) continue;
                Quaternion aimed = SkinOrientationSolver.Aim(orientation, direction);
                rotation = Quaternion.Slerp(rotation, Quaternion.Normalize(Quaternion.Inverse(parent) * aimed), weight);
                locals[slot] = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
                compose();
            }
        }
        Compose(locals, worlds, Matrix4x4.Identity);
        _pendingStep = 0f;
    }

    private float OrientationWeight(float time, bool defaultOn)
    {
        float resting = defaultOn ? 1f : 0f, weight = resting, from = resting, target = resting, duration = 0f;
        double last = 0d;
        var moments = _cues.OfType<AnimationOrientationCue>().Where(cue => cue.BlendFromSeconds.HasValue)
            .SelectMany(cue => cue.UntilSeconds is { } end
                ? new[] { (At: cue.AtSeconds, Target: 1f - resting, Duration: cue.BlendFromSeconds.Value), (At: end, Target: resting, Duration: cue.BlendToSeconds) }
                : new[] { (At: cue.AtSeconds, Target: 1f - resting, Duration: cue.BlendFromSeconds.Value) })
            .OrderBy(moment => moment.At);
        foreach (var moment in moments)
        {
            if (moment.At > time) break;
            weight = duration > 0f ? float.Lerp(from, target, Math.Clamp((float)(moment.At - last) / duration, 0f, 1f)) : target;
            from = weight;
            target = moment.Target;
            duration = Math.Max(moment.Duration, 0f);
            last = moment.At;
        }
        return duration > 0f ? float.Lerp(from, target, Math.Clamp((float)(time - last) / duration, 0f, 1f)) : target;
    }

    private void Compose(Matrix4x4[] locals, Matrix4x4[] worlds, Matrix4x4 root)
    {
        foreach (int slot in _order)
            worlds[slot] = locals[slot] * (_parents[slot] < 0 ? root : worlds[_parents[slot]]);
    }
}
