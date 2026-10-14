using System;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Runtime;

public sealed partial class VfxPlaybackRuntime
{
    private Vector3 SampleSpawnShape(VfxSpawnShape shape, float phase, out Matrix4x4 rotation)
    {
        if (shape.Kind != VfxSpawnShapeKind.Legacy)
            return shape.SampleOffset(_rng, phase, _pinnedBirthChance, out rotation);
        Vector3 offset = BirthVector(shape.EmitOffset, phase) + BirthVector(shape.BirthTranslation, phase);
        rotation = Matrix4x4.Identity;
        for (int index = 0; index < Math.Min(shape.RotationAxes.Count, shape.RotationAngles.Count); index++)
        {
            Vector3 axis = shape.RotationAxes[index];
            double length = Math.Sqrt((double)axis.X * axis.X + (double)axis.Y * axis.Y + (double)axis.Z * axis.Z);
            if (length == 0d) continue;
            float radians = BirthScalar(shape.RotationAngles[index], phase) * (MathF.PI / 180f);
            rotation *= Matrix4x4.CreateFromAxisAngle(axis / (float)length, radians);
        }
        return Vector3.TransformNormal(offset, rotation);
    }

    private float BirthScalar(VfxCurveF curve, float phase)
    {
        float value = curve.Sample(phase);
        if (curve.Prob is not { Length: > 0 }) return value;
        for (int channel = 0; channel < curve.Prob.Length; channel++)
        {
            if (curve.Prob[channel].IsEmpty) continue;
            float drawn = _rng.NextUnitFloat();
            if (channel == 0) value *= curve.Prob[channel].Sample(_pinnedBirthChance ?? drawn);
        }
        return value;
    }

    private Vector3 BirthVector(VfxCurve3? curve, float phase, Vector3 baseline = default)
    {
        if (curve is not { } held) return baseline;
        Vector3 value = held.SampleOver(phase, baseline);
        if (held.Prob is null) return value;
        int width = VfxCurve.SampleWidth(held.ConstantWidth, held.Times, held.Values?.Length ?? 0, held.ValueWidths, phase, 3);
        for (int channel = 0; channel < held.Prob.Length; channel++)
        {
            if (held.Prob[channel].IsEmpty) continue;
            float drawn = _rng.NextUnitFloat();
            if (channel < width) value[channel] *= held.Prob[channel].Sample(_pinnedBirthChance ?? drawn);
        }
        return value;
    }

    private Vector4 BirthColor(VfxCurve4 curve, float phase)
    {
        Vector4 value = curve.SampleOver(phase, Vector4.One);
        if (curve.Prob is null) return value;
        int width = VfxCurve.SampleWidth(curve.ConstantWidth, curve.Times, curve.Values?.Length ?? 0, curve.ValueWidths, phase, 4);
        for (int channel = 0; channel < curve.Prob.Length; channel++)
        {
            if (curve.Prob[channel].IsEmpty) continue;
            float drawn = _rng.NextUnitFloat();
            if (channel < width) value[channel] *= curve.Prob[channel].Sample(_pinnedBirthChance ?? drawn);
        }
        return value;
    }

    private static float Flicker(float value, VfxProbTable[] tables, int channel, uint serial, float now)
    {
        if (tables is null || channel >= tables.Length || tables[channel].IsEmpty) return value;
        uint step = unchecked((serial + 1u) * 0x9e3779b1u) ^ BitConverter.SingleToUInt32Bits(now);
        int slot = 0;
        for (int index = 0; index < channel; index++) if (!tables[index].IsEmpty) slot++;
        float chance = (float)(Mix32(unchecked(step + (uint)slot * 0x85ebca6bu)) / 4294967296d);
        return value * tables[channel].Sample(chance);
    }

    private static Vector3 LifeVector(VfxCurve3? curve, float age, uint serial, float now, Vector3 baseline = default)
    {
        if (curve is not { } held) return baseline;
        Vector3 value = held.SampleOver(age, baseline);
        int width = VfxCurve.SampleWidth(held.ConstantWidth, held.Times, held.Values?.Length ?? 0, held.ValueWidths, age, 3);
        for (int channel = 0; channel < width; channel++) value[channel] = Flicker(value[channel], held.Prob, channel, serial, now);
        return value;
    }

    private static Vector4 LifeColor(VfxCurve4? curve, float age, uint serial, float now)
    {
        if (curve is not { } held) return Vector4.One;
        Vector4 value = held.SampleOver(age, Vector4.One);
        int width = VfxCurve.SampleWidth(held.ConstantWidth, held.Times, held.Values?.Length ?? 0, held.ValueWidths, age, 4);
        for (int channel = 0; channel < width; channel++) value[channel] = Flicker(value[channel], held.Prob, channel, serial, now);
        return value;
    }

    private static Vector3 MatrixTranslation(in Particle particle, EmitterState state)
    {
        Vector3 position = particle.LocalPosition;
        if (state.Def.IsEmitterSpace) position += state.Def.EmitterPosition.Sample(EmitterTime(state));
        if (state.Def.ParticleIsLocalOrientation) position = Vector3.TransformNormal(position, state.SystemOrientation);
        position = Vector3.TransformNormal(position, OrbitalTurn(particle.BirthOrbitalVelocity * particle.Age));
        return Vector3.Transform(position, state.DefinitionTransform);
    }

    private static Vector3 DrawnPosition(in Particle particle, EmitterState state)
    {
        Vector3 position = particle.Pos;
        if (state.Def.Acceleration is { } worldAcceleration && float.IsFinite(particle.Life))
            position += VfxIntegratedValue.Sample(worldAcceleration, ParticleAge01(particle.Age, particle.Life), 2)
                * (particle.Life * particle.Life);
        return position;
    }

    private static void RebuildRotation(ref Particle particle, EmitterState state)
    {
        particle.BirthRotation = particle.InitialRotation + particle.Age *
            (particle.RotationalVelocity + particle.RotationalAcceleration * (0.5f * particle.Age));
        if (!state.Def.IsRotationEnabled) return;
        float age = ParticleAge01(particle.Age, particle.Life);
        if (state.Def.IsSimpleEmitter)
        {
            particle.BirthRotation.Z = (state.Def.LegacyRotation?.Sample(age) ?? 0f) * (MathF.PI / 180f);
            return;
        }
        if (!float.IsFinite(particle.Life)) return;
        Vector3 integrated = particle.LingerFrom >= 0f && state.Def.Linger?.Rotation is { } linger
            ? linger.Sample(age)
            : state.Def.RotationOverLife is { } rotation ? VfxIntegratedValue.Sample(rotation, age) : Vector3.Zero;
        particle.BirthRotation += integrated * (60f * MathF.PI / 180f * particle.Life);
    }

    private static Vector2 IntegratedUv(VfxCurve2? curve, in Particle particle)
        => float.IsFinite(particle.Life) && curve is { } held
            ? VfxIntegratedValue.Sample(held, ParticleAge01(particle.Age, particle.Life)) * particle.Life : Vector2.Zero;

    private static float IntegratedUv(VfxCurveF? curve, in Particle particle)
        => float.IsFinite(particle.Life) && curve is { } held
            ? VfxIntegratedValue.Sample(held, ParticleAge01(particle.Age, particle.Life)) * particle.Life : 0f;
}
