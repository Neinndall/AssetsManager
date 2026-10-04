using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Animation;

/// <summary>Path-following joint rotations, with clip-controlled masks and turn activation.</summary>
internal sealed class SkinConformRuntime
{
    internal SkinConformDefinition Definition { get; }
    internal IEnumerable<int> AffectedSlots
    {
        get
        {
            foreach (int slot in _joints) yield return slot;
            foreach (var extra in _extra)
                foreach (int slot in extra.Joints) yield return slot;
        }
    }
    private readonly int[] _joints;
    private readonly (int[] Joints, float Bias)[] _extra;
    private readonly Vector3[] _aims;
    private readonly Vector3[] _path = new Vector3[300];
    private readonly bool[] _turns = new bool[300];
    private readonly float[] _weights;
    private readonly float[] _previousWeights;
    private uint _mask;
    private float _blendTime;
    private float _blendDuration = 0.2f;
    private int _points;
    private Vector3 _lastRoot;
    private bool _started;

    internal SkinConformRuntime(SkinConformDefinition definition, int[] parents, Func<uint, int> joint)
    {
        Definition = definition;
        _joints = Chain(parents, joint(definition.Start), joint(definition.End));
        _extra = new (int[], float)[definition.ExtraChains.Count];
        for (int at = 0; at < _extra.Length; at++)
        {
            var extra = definition.ExtraChains[at];
            _extra[at] = (Chain(parents, joint(extra.Start), joint(extra.End)), extra.RightBias);
        }
        _aims = new Vector3[_joints.Length + 1];
        _weights = new float[_joints.Length];
        _previousWeights = new float[_joints.Length];
        _mask = definition.Mask;
    }

    internal static int[] Chain(int[] parents, int start, int end)
    {
        if (parents.Length == 0) return Array.Empty<int>();
        start = start < 0 ? 0 : start;
        end = end < 0 ? 0 : end;
        var chain = new List<int> { end };
        int above = end == start ? -1 : parents[end];
        while (above >= 0 && chain.Count < parents.Length)
        {
            chain.Insert(0, above);
            if (above == start) break;
            above = parents[above];
        }
        return chain.ToArray();
    }

    internal void Reset()
    {
        _started = false;
        _points = 0;
        _mask = Definition.Mask;
        _blendTime = 0f;
        _blendDuration = 0.2f;
        Array.Clear(_weights);
        Array.Clear(_previousWeights);
    }

    internal void BlendMask(uint mask, float seconds)
    {
        _weights.CopyTo(_previousWeights, 0);
        _mask = mask;
        _blendTime = 0f;
        _blendDuration = Math.Max(seconds, 0f);
    }

    internal void Step(Matrix4x4[] locals, Matrix4x4[] world, int[] parents, Matrix4x4 root,
        float dt, Func<uint, int, float> maskWeight, Action compose)
    {
        int count = _joints.Length;
        if (count == 0) return;
        if (Definition.OnlyInTurns) TrackPath(root.Translation);
        if (!_started)
        {
            for (int at = 0; at < count; at++) _aims[at] = world[_joints[at]].Translation;
            ExtendAims();
            _lastRoot = root.Translation;
        }

        float blend = _blendDuration <= 1e-6f ? 1f : Math.Clamp(_blendTime / _blendDuration, 0f, 1f);
        for (int at = 0; at < count; at++)
            _weights[at] = float.Lerp(_previousWeights[at], maskWeight(_mask, _joints[at]), blend);
        _blendTime += dt;

        Vector3 speed = dt > 1e-6f ? (root.Translation - _lastRoot) / dt : Vector3.Zero;
        _lastRoot = root.Translation;
        float pull = 36f * Definition.Frequency * Definition.Frequency;
        float eased = pull * dt / (1f + 9f * Definition.Frequency * Definition.Damping * dt + pull * dt * dt);
        float limit = Math.Max(Definition.MaxAngle, 0f) * MathF.PI / 180f;
        for (int at = 0; _started && at < count; at++)
        {
            int slot = _joints[at];
            Vector3 position = world[slot].Translation;
            Vector3 along = at < count - 1 ? world[_joints[at + 1]].Translation - position
                : count > 1 ? _aims[count - 1] - _aims[count - 2] : -position;
            float carried = (1f - (float)at / count) * Definition.VelocityMultiplier;
            _aims[at + 1] += ((position - _aims[at + 1]) * eased + speed * carried) * dt;
            Vector3 toward = _aims[at + 1] - position;
            along.Y = toward.Y = 0f;
            float angle = 0f;
            if (along.LengthSquared() > 1e-12f && toward.LengthSquared() > 1e-12f)
            {
                angle = Math.Min(MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(along), Vector3.Normalize(toward)), -1f, 1f)), limit);
                if (toward.X * along.Z - toward.Z * along.X <= 0f) angle = -angle;
            }
            angle *= _weights[at] * (Definition.OnlyInTurns ? NearTurn(position) : 1f);
            _aims[at] = position;
            Quaternion yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle);
            if (Matrix4x4.Decompose(world[slot], out _, out Quaternion worldRotation, out _) &&
                Matrix4x4.Decompose(locals[slot], out Vector3 scale, out _, out Vector3 localPosition) &&
                Matrix4x4.Decompose(parents[slot] < 0 ? root : world[parents[slot]], out _, out Quaternion parentRotation, out _))
            {
                Quaternion rotation = Quaternion.Normalize(Quaternion.Inverse(parentRotation) * yaw * worldRotation);
                locals[slot] = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(localPosition);
                compose();
            }

            foreach (var extra in _extra)
            {
                if (at >= extra.Joints.Length) continue;
                int other = extra.Joints[at];
                float biased = angle - MathF.Abs(angle) * ((float)(extra.Joints.Length - at) / extra.Joints.Length) * extra.Bias;
                if (!Matrix4x4.Decompose(locals[other], out Vector3 extraScale, out Quaternion rotation, out Vector3 translation)) continue;
                rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, biased) * rotation);
                locals[other] = Matrix4x4.CreateScale(extraScale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
            }
        }
        if (_started) ExtendAims();
        _started = true;
        compose();
    }

    private void ExtendAims()
    {
        int count = _joints.Length;
        _aims[count] = count > 1 ? 2f * _aims[count - 1] - _aims[count - 2] : _aims[0] + new Vector3(20f, 0f, 0f);
    }

    private void TrackPath(Vector3 position)
    {
        if (_points > 0 && Vector2.Distance(new(_path[0].X, _path[0].Z), new(position.X, position.Z)) <= 1f) return;
        _points = Math.Min(_points + 1, _path.Length);
        Array.Copy(_path, 0, _path, 1, _points - 1);
        _path[0] = position;
        Array.Clear(_turns);
        float sharp = MathF.Cos((180f - Definition.ActivationAngle) * MathF.PI / 180f);
        for (int at = 1; at < _points - 1; at++)
        {
            Vector3 before = _path[at - 1] - _path[at], after = _path[at + 1] - _path[at];
            before.Y = after.Y = 0f;
            float size = before.Length() * after.Length();
            _turns[at] = size > 1e-6f && Vector3.Dot(before, after) > sharp * size;
        }
    }

    private float NearTurn(Vector3 position)
    {
        float near = 0f;
        for (int at = 0; at < _points; at++)
        {
            if (!_turns[at]) continue;
            float distance = Vector2.Distance(new(_path[at].X, _path[at].Z), new(position.X, position.Z));
            if (distance < Definition.ActivationDistance) return 1f;
            if (distance < Definition.BlendDistance && Definition.BlendDistance > Definition.ActivationDistance)
                near = Math.Max(near, 1f - (distance - Definition.ActivationDistance) / (Definition.BlendDistance - Definition.ActivationDistance));
        }
        return near;
    }
}
