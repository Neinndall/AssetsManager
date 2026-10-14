using System;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Animation;

internal static class SkinOrientationSolver
{
    internal static Quaternion Aim(SkinOrientationDefinition model, Vector3 direction)
    {
        if (!SkinPoseReader.IsFinite(direction) || direction.LengthSquared() < 1e-12f) return Quaternion.Identity;
        Vector3 normal = model.PlaneConstraint switch { 0 => Vector3.UnitZ, 2 => Vector3.UnitX, _ => Vector3.UnitY };
        Vector3 Axis(byte value) => value switch { 1 => Vector3.UnitX, 2 => Vector3.UnitY, 3 => Vector3.UnitZ, _ => Vector3.Zero };
        Vector3 tilt = Axis(model.TiltAxis) * (model.Flipped ? -1f : 1f);
        Vector3 aim = Axis(model.AimAxis) * (model.AimNegated ? -1f : 1f);
        Quaternion turn = model.Flipped ? Quaternion.CreateFromAxisAngle(normal, MathF.PI) : Quaternion.Identity;
        direction = Vector3.Normalize(direction);
        if (tilt != Vector3.Zero && MathF.Abs(Vector3.Dot(tilt, normal)) < 0.5f)
        {
            Vector3 across = Vector3.Cross(tilt, direction);
            float angle = SignedAngle(normal, across, tilt) * (model.Flipped ? -1f : 1f);
            turn *= Quaternion.CreateFromAxisAngle(tilt, angle);
        }
        if (aim != Vector3.Zero && MathF.Abs(Vector3.Dot(aim, normal)) < 0.5f)
        {
            Vector3 across = Vector3.Transform(direction, Quaternion.Inverse(turn));
            float limit = Math.Max(model.MaxAngle, 0f) * MathF.PI / 180f;
            float angle = Math.Clamp(SignedAngle(aim, across, normal), -limit, limit);
            turn *= Quaternion.CreateFromAxisAngle(normal, angle);
        }
        return Quaternion.Normalize(turn);
    }

    private static float SignedAngle(Vector3 a, Vector3 b, Vector3 axis)
    {
        float size = a.Length() * b.Length();
        if (size < 1e-6f) return 0f;
        return MathF.Acos(Math.Clamp(Vector3.Dot(a, b) / size, -1f, 1f)) *
            (Vector3.Dot(Vector3.Cross(a, b), axis) > 0f ? 1f : -1f);
    }
}
