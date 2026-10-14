using System;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Composition;

/// <summary>
/// Where a spell missile is at each moment of its flight, from its authored movement component:
/// a straight line at fixed speed or time, a straight line under acceleration, or a Hermite spline.
/// </summary>
public sealed class VfxSpellFlightPath
{
    private const int SplineSamples = 64;
    private const double MaxFlightSeconds = 30d;

    private readonly Vector3 _from;
    private readonly Vector3 _to;
    private readonly Func<double, float> _progressAt;
    private readonly Vector3 _tangent0;
    private readonly Vector3 _tangent1;
    private readonly bool _spline;
    private readonly float[] _arcLengths;
    private float _gravity;
    private float _sineAmplitude;
    private float _sinePeriods;

    private VfxSpellFlightPath(
        Vector3 from,
        Vector3 to,
        double duration,
        Func<double, float> progressAt,
        Vector3 tangent0 = default,
        Vector3 tangent1 = default,
        bool spline = false,
        float[] arcLengths = null)
    {
        _from = from;
        _to = to;
        Duration = duration;
        _progressAt = progressAt;
        _tangent0 = tangent0;
        _tangent1 = tangent1;
        _spline = spline;
        _arcLengths = arcLengths;
    }

    /// <summary>Seconds from launch to arrival.</summary>
    public double Duration { get; }

    /// <summary>The flight's start, which a spline's start offset moves away from the launch bone.</summary>
    public Vector3 Start => _from;

    /// <summary>Where the flight lands, which a height solver may move down to the target's height.</summary>
    public Vector3 End => _to;

    /// <summary>Position and travel direction <paramref name="elapsed"/> seconds after launch.</summary>
    public (Vector3 Position, Vector3 Direction) Sample(double elapsed)
    {
        double time = Math.Clamp(elapsed, 0d, Duration);
        float progress = Duration > 0d ? _progressAt(time) : 1f;
        Vector3 lift = Vector3.UnitY * HeightAt(time);
        if (!_spline)
            return (Vector3.Lerp(_from, _to, progress) + lift, _to - _from);

        float u = _arcLengths != null ? ParameterAtArcFraction(progress) : progress;
        return (Hermite(u) + lift, HermiteDerivative(u));
    }

    /// <summary>
    /// Height the missile's height solver adds over its straight track: a ballistic arc under the authored gravity
    /// that leaves and lands on the track's ends, or sine waves of the authored amplitude.
    /// </summary>
    private float HeightAt(double time)
    {
        if (!(Duration > 0d)) return 0f;
        if (_gravity > 0f)
            return (float)(0.5d * _gravity * time * (Duration - time));
        if (_sineAmplitude != 0f && _sinePeriods != 0f)
            return (float)(_sineAmplitude * Math.Sin(2d * Math.PI * _sinePeriods * time / Duration));
        return 0f;
    }

    /// <summary>
    /// Compiles the flight between <paramref name="from"/> (the launch point) and <paramref name="to"/>, or null when
    /// the movement cannot be flown in isolation (orbits, wall following, cursor tracking) or never arrives in time.
    /// </summary>
    internal static VfxSpellFlightPath Compile(VfxSpellMissilePreview missile, Vector3 from, Vector3 to)
    {
        if (missile == null) return null;
        // The preview's ground is the character's feet plane, y = 0.
        if (missile.LandsOnTargetHeight)
            to = new Vector3(to.X, missile.TargetHeight is { } augment && float.IsFinite(augment) ? augment : 0f, to.Z);
        Vector3 forward = new(to.X - from.X, 0f, to.Z - from.Z);
        if (forward.LengthSquared() <= 1e-8f) forward = Vector3.UnitZ;
        else forward = Vector3.Normalize(forward);
        // Game space is left-handed with +Y up: the caster's right is up x forward.
        Vector3 right = Vector3.Cross(Vector3.UnitY, forward);
        Vector3 Local(Vector3 value) => right * value.X + Vector3.UnitY * value.Y + forward * value.Z;

        VfxSpellFlightPath path = missile.MovementKind switch
        {
            VfxSpellMissileMovementKind.FixedSpeed when Positive(missile.Speed) =>
                Linear(from, to, Vector3.Distance(from, to) / missile.Speed.Value),
            VfxSpellMissileMovementKind.FixedTime when Positive(missile.Duration) =>
                Linear(from, to, missile.Duration.Value),
            VfxSpellMissileMovementKind.Accelerating => Accelerating(missile, from, to),
            VfxSpellMissileMovementKind.FixedSpeedSpline when Positive(missile.Speed) =>
                Spline(missile, from + Local(missile.SplineStartOffset), to, Local, missile.Speed.Value, null),
            VfxSpellMissileMovementKind.FixedTimeSpline when Positive(missile.Duration) =>
                Spline(missile, from + Local(missile.SplineStartOffset), to, Local, null, missile.Duration.Value),
            _ => null
        };
        if (path != null)
        {
            path._gravity = float.IsFinite(missile.Gravity) ? missile.Gravity : 0f;
            path._sineAmplitude = float.IsFinite(missile.SineAmplitude) ? missile.SineAmplitude : 0f;
            path._sinePeriods = float.IsFinite(missile.SinePeriods) ? missile.SinePeriods : 0f;
        }
        return path;
    }

    private static VfxSpellFlightPath Linear(Vector3 from, Vector3 to, double duration)
    {
        if (!Valid(duration)) return null;
        return new VfxSpellFlightPath(from, to, duration, time => (float)(time / duration));
    }

    /// <summary>Speed starts at mInitialSpeed and changes by mAcceleration per second, held within mMinSpeed..mMaxSpeed.</summary>
    private static VfxSpellFlightPath Accelerating(VfxSpellMissilePreview missile, Vector3 from, Vector3 to)
    {
        double acceleration = missile.Acceleration ?? 0d;
        double min = Math.Max(0d, missile.MinSpeed ?? 0d);
        double max = missile.MaxSpeed is > 0f ? missile.MaxSpeed.Value : double.PositiveInfinity;
        if (!double.IsFinite(acceleration) || max < min) return null;

        double initial = Math.Clamp(missile.InitialSpeed ?? 0d, min, max);
        double length = Vector3.Distance(from, to);
        // Speed changes until it reaches the bound it moves toward, then holds there.
        double bound = acceleration > 0d ? max : acceleration < 0d ? min : initial;
        double rampTime = acceleration != 0d && double.IsFinite(bound) ? (bound - initial) / acceleration : double.PositiveInfinity;
        double rampLength = double.IsFinite(rampTime) ? initial * rampTime + 0.5d * acceleration * rampTime * rampTime : double.PositiveInfinity;

        double DistanceAt(double time) => time <= rampTime
            ? initial * time + 0.5d * acceleration * time * time
            : rampLength + bound * (time - rampTime);

        double duration;
        if (length <= rampLength)
        {
            duration = acceleration == 0d
                ? (initial > 0d ? length / initial : double.NaN)
                : (-initial + Math.Sqrt(Math.Max(0d, initial * initial + 2d * acceleration * length))) / acceleration;
        }
        else
        {
            duration = bound > 0d ? rampTime + (length - rampLength) / bound : double.NaN;
        }

        if (!Valid(duration)) return null;
        return new VfxSpellFlightPath(
            from,
            to,
            duration,
            time => length > 0d ? (float)Math.Clamp(DistanceAt(time) / length, 0d, 1d) : 1f);
    }

    /// <summary>
    /// A cubic Hermite curve from the offset start to the target. Its control points are the end tangents in the
    /// caster's frame (x right, z forward), measured in units of the start-to-target distance.
    /// </summary>
    private static VfxSpellFlightPath Spline(
        VfxSpellMissilePreview missile,
        Vector3 start,
        Vector3 to,
        Func<Vector3, Vector3> local,
        float? speed,
        float? travelTime)
    {
        float span = Vector3.Distance(start, to);
        Vector3 tangent0 = local(missile.SplineControlPoint1) * span;
        Vector3 tangent1 = local(missile.SplineControlPoint2) * span;

        if (travelTime.HasValue)
        {
            double duration = travelTime.Value;
            if (!Valid(duration)) return null;
            return new VfxSpellFlightPath(start, to, duration, time => (float)(time / duration), tangent0, tangent1, spline: true);
        }

        // Fixed speed: equal distance per second along the curve.
        var shape = new VfxSpellFlightPath(start, to, 1d, time => (float)time, tangent0, tangent1, spline: true);
        var arcLengths = new float[SplineSamples + 1];
        Vector3 previous = shape.Hermite(0f);
        for (int index = 1; index <= SplineSamples; index++)
        {
            Vector3 point = shape.Hermite(index / (float)SplineSamples);
            arcLengths[index] = arcLengths[index - 1] + Vector3.Distance(previous, point);
            previous = point;
        }

        double fixedDuration = arcLengths[SplineSamples] / speed.Value;
        if (!Valid(fixedDuration)) return null;
        return new VfxSpellFlightPath(
            start,
            to,
            fixedDuration,
            time => (float)(time / fixedDuration),
            tangent0,
            tangent1,
            spline: true,
            arcLengths);
    }

    private float ParameterAtArcFraction(float fraction)
    {
        float total = _arcLengths[^1];
        if (total <= 0f) return fraction;
        float target = Math.Clamp(fraction, 0f, 1f) * total;
        int index = Array.BinarySearch(_arcLengths, target);
        if (index >= 0) return index / (float)SplineSamples;
        index = ~index;
        if (index <= 0) return 0f;
        if (index > SplineSamples) return 1f;
        float low = _arcLengths[index - 1], high = _arcLengths[index];
        float blend = high > low ? (target - low) / (high - low) : 0f;
        return (index - 1 + blend) / SplineSamples;
    }

    private Vector3 Hermite(float u)
    {
        float u2 = u * u, u3 = u2 * u;
        return (2f * u3 - 3f * u2 + 1f) * _from +
               (u3 - 2f * u2 + u) * _tangent0 +
               (-2f * u3 + 3f * u2) * _to +
               (u3 - u2) * _tangent1;
    }

    private Vector3 HermiteDerivative(float u)
    {
        float u2 = u * u;
        Vector3 derivative = (6f * u2 - 6f * u) * _from +
                             (3f * u2 - 4f * u + 1f) * _tangent0 +
                             (-6f * u2 + 6f * u) * _to +
                             (3f * u2 - 2f * u) * _tangent1;
        return derivative.LengthSquared() > 1e-8f ? derivative : _to - _from;
    }

    private static bool Positive(float? value) => value is > 0f && float.IsFinite(value.Value);

    private static bool Valid(double duration) =>
        double.IsFinite(duration) && duration >= 0d && duration <= MaxFlightSeconds;
}
