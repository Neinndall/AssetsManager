using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Animation;

/// <summary>Skin sockets resolve against the final pose without entering the skinning palette.</summary>
internal sealed class SkinSocketResolver
{
    private readonly IReadOnlyList<SkinSocketDefinition> _sockets;
    private readonly Matrix4x4[] _bind;
    private readonly Func<uint, int> _joint;

    internal SkinSocketResolver(SkinPoseDefinition definition, Matrix4x4[] bind, Func<uint, int> joint)
    {
        _sockets = definition.Sockets;
        _bind = bind;
        _joint = joint;
    }

    internal bool TryResolve(string name, uint hash, IReadOnlyList<Matrix4x4> pose, out Matrix4x4 transform)
    {
        transform = Matrix4x4.Identity;
        foreach (SkinSocketDefinition socket in _sockets)
        {
            bool matches = !string.IsNullOrEmpty(name)
                ? string.Equals(socket.Name, name, StringComparison.OrdinalIgnoreCase)
                : hash != 0u && (Fnv1a.HashLower(socket.Name) == hash || Elf.HashLower(socket.Name) == hash);
            if (!matches) continue;

            // The first socket owns the name even when it cannot resolve its parent.
            if (socket.Kind == SkinSocketKind.World)
            {
                transform = Matrix4x4.CreateTranslation(socket.Position);
                return true;
            }
            int slot = _joint(socket.Parent);
            if (socket.Kind != SkinSocketKind.Joint || slot < 0 || slot >= pose.Count || slot >= _bind.Length)
                return false;
            return Resolve(socket, pose[slot], _bind[slot], out transform);
        }
        return false;
    }

    internal static bool Resolve(SkinSocketDefinition socket, Matrix4x4 joint, Matrix4x4 bind, out Matrix4x4 transform)
    {
        transform = Matrix4x4.Identity;
        if (!Matrix4x4.Decompose(joint, out Vector3 scale, out Quaternion facing, out Vector3 position) ||
            !Matrix4x4.Decompose(bind, out Vector3 bindScale, out Quaternion bindFacing, out Vector3 bindPosition))
            return false;

        Vector3 offset = Vector3.Transform(socket.Position, Quaternion.Inverse(bindFacing));
        offset = new(bindScale.X == 0f ? 0f : offset.X / bindScale.X,
            bindScale.Y == 0f ? 0f : offset.Y / bindScale.Y, bindScale.Z == 0f ? 0f : offset.Z / bindScale.Z);
        Vector3 place = position + Vector3.Transform(offset * scale, facing);
        Vector3 held = bindPosition + Vector3.Transform(offset * bindScale, bindFacing);
        place = Freeze(place, held, socket.FreezePosition);

        Quaternion turn = Euler(socket.Rotation);
        Quaternion rotation = Quaternion.Normalize(facing * turn);
        if (socket.FreezeRotation != Vector3.Zero)
        {
            Vector3 angles = Freeze(Euler(rotation), Euler(Quaternion.Normalize(bindFacing * turn)), socket.FreezeRotation);
            rotation = Euler(angles);
        }

        if (!SkinPoseReader.IsFinite(place)) return false;
        transform = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(place);
        return true;
    }

    private static Vector3 Freeze(Vector3 value, Vector3 held, Vector3 flags) => new(
        flags.X != 0f ? held.X : value.X, flags.Y != 0f ? held.Y : value.Y, flags.Z != 0f ? held.Z : value.Z);

    // Riot sockets use XZY Euler angles, distinct from CreateFromYawPitchRoll's YXZ order.
    internal static Quaternion Euler(Vector3 degrees)
    {
        Vector3 half = degrees * (MathF.PI / 360f);
        float sx = MathF.Sin(half.X), cx = MathF.Cos(half.X);
        float sy = MathF.Sin(half.Y), cy = MathF.Cos(half.Y);
        float sz = MathF.Sin(half.Z), cz = MathF.Cos(half.Z);
        return Quaternion.Normalize(new(sx * cy * cz + cx * sy * sz, cx * sy * cz + sx * cy * sz,
            cx * cy * sz - sx * sy * cz, cx * cy * cz - sx * sy * sz));
    }

    internal static Vector3 Euler(Quaternion q)
    {
        float m10 = 2f * (q.X * q.Y + q.Z * q.W);
        float z = MathF.Asin(Math.Clamp(m10, -1f, 1f));
        float x, y;
        if (MathF.Abs(m10) < 1f - 1e-6f)
        {
            x = MathF.Atan2(-2f * (q.Y * q.Z - q.X * q.W), 1f - 2f * (q.X * q.X + q.Z * q.Z));
            y = MathF.Atan2(-2f * (q.X * q.Z - q.Y * q.W), 1f - 2f * (q.Y * q.Y + q.Z * q.Z));
        }
        else
        {
            x = 0f;
            y = MathF.Atan2(2f * (q.X * q.Z + q.Y * q.W), 1f - 2f * (q.X * q.X + q.Y * q.Y));
        }
        return new Vector3(x, y, z) * (180f / MathF.PI);
    }
}
