using System;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Animation;

/// <summary>Root-driven spring state. Sampling a pose never advances this state.</summary>
internal sealed class SkinSpringRuntime
{
    internal SkinSpringDefinition Definition { get; }
    internal Vector3 Offset { get; private set; }
    internal Vector3 Velocity { get; private set; }
    internal float Angle { get; private set; }
    private float _spin;
    private Vector3 _lastPosition;
    private float _lastYaw;
    private int _warmed;

    internal SkinSpringRuntime(SkinSpringDefinition definition) => Definition = definition;

    internal void Reset()
    {
        Offset = Velocity = Vector3.Zero;
        Angle = _spin = 0f;
        _warmed = 0;
    }

    internal void Step(Matrix4x4 root, float dt)
    {
        if (!(dt > 1e-6f) || !float.IsFinite(dt)) return;
        Vector3 position = root.Translation;
        float yaw = MathF.Atan2(root.M31, root.M33);
        SkinSpringDefinition model = Definition;
        if (_warmed >= 2 && model.Mass > 1e-6f)
        {
            float pull = model.Stiffness / model.Mass;
            float drag = model.Damping / model.Mass;
            if (model.DoTranslation)
            {
                Offset += _lastPosition - position;
                Velocity += (-pull * Offset - drag * Velocity) * dt;
                Offset += Velocity * dt;
                float distance = Offset.Length();
                if (model.MaxDistance > 0f && distance > model.MaxDistance)
                {
                    Offset *= model.MaxDistance * 0.99f / distance;
                    Velocity = Vector3.Zero;
                }
            }
            if (model.DoRotation)
            {
                Angle += Wrap(yaw - _lastYaw);
                _spin += (-pull * Angle - drag * _spin) * dt;
                Angle = Wrap(Angle + _spin * dt);
                float limit = model.MaxAngle * MathF.PI / 180f;
                if (limit > 0f && MathF.Abs(Angle) > limit)
                {
                    Angle = MathF.CopySign(limit * 0.99f, Angle);
                    _spin = 0f;
                }
            }
            if (!SkinPoseReader.IsFinite(Offset) || !SkinPoseReader.IsFinite(Velocity) || !float.IsFinite(Angle + _spin))
                Reset();
        }
        _warmed = Math.Min(_warmed + 1, 2);
        _lastPosition = position;
        _lastYaw = yaw;
    }

    internal Matrix4x4 Apply(Matrix4x4 local, Matrix4x4 parentWorld, float weight)
    {
        if (weight == 0f || !Matrix4x4.Decompose(local, out Vector3 scale, out Quaternion rotation, out Vector3 position) ||
            !Matrix4x4.Decompose(parentWorld, out _, out Quaternion parentRotation, out _))
            return local;

        float sign = (Definition.Invert ? -1f : 1f) * weight;
        if (Definition.DoTranslation)
            position += Vector3.Transform(Offset * sign, Quaternion.Inverse(parentRotation));
        if (Definition.DoRotation)
        {
            Quaternion yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Angle * sign);
            rotation = Quaternion.Normalize(Quaternion.Inverse(parentRotation) * yaw * parentRotation * rotation);
        }
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
    }

    internal static float Wrap(float radians) => MathF.IEEERemainder(radians, MathF.Tau);
}
