using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Session
{
    public enum VfxRigPreset
    {
        Still,
        Burst,
        Missile,
        Trail
    }

    public enum VfxRigMotionKind
    {
        Still,
        Path,
        Orbit
    }

    public readonly record struct VfxRigSettings(
        VfxRigPreset Preset,
        float Height,
        float FlightRange,
        float FlightSpeed,
        float OrbitRadius,
        float OrbitPeriod,
        bool IsLooping,
        float? StopAt)
    {
        public static VfxRigSettings ForPreset(VfxRigPreset preset)
            => new(
                preset,
                VfxRigMotion.StandHeight,
                VfxRigMotion.FlightRange,
                VfxRigMotion.FlightSpeed,
                VfxRigMotion.OrbitRadius,
                VfxRigMotion.OrbitPeriod,
                preset is VfxRigPreset.Burst or VfxRigPreset.Missile,
                null);

        public VfxRigMotionKind MotionKind => Preset switch
        {
            VfxRigPreset.Missile => VfxRigMotionKind.Path,
            VfxRigPreset.Trail => VfxRigMotionKind.Orbit,
            _ => VfxRigMotionKind.Still
        };
    }

    public readonly record struct VfxRigStep(
        Matrix4x4 Transform,
        Vector3 Origin,
        Vector3 Moved,
        bool IsStopped,
        float Phase,
        float TotalSpan)
    {
        public Vector3 Target { get; init; }
    }

    /// <summary>
    /// Implements rig motion simulation for VFX playback (Still, Burst, Missile flight path, and Trail orbit),
    /// adhering to the exact mathematical conventions of League of Legends and LTK Manager.
    /// </summary>
    public static class VfxRigMotion
    {
        public const float ChampionHeight = 200f;
        public const float FlightRange = ChampionHeight * 6f;  // 1200 engine units
        public const float FlightSpeed = ChampionHeight * 8f;  // 1600 engine units/second
        public const float StandHeight = ChampionHeight * 0.5f; // 100 engine units
        public const float OrbitRadius = ChampionHeight * 1.5f; // 300 engine units
        public const float OrbitPeriod = 3.0f;                  // 3 seconds per revolution
        public const float TargetReach = ChampionHeight * 3f;   // 600 engine units


        public static double RunLength(
            VfxRigPreset preset,
            VfxSystemDefinition system,
            float flightRange = FlightRange,
            float flightSpeed = FlightSpeed,
            float orbitPeriod = OrbitPeriod)
        {
            VfxRigSettings settings = VfxRigSettings.ForPreset(preset) with
            {
                FlightRange = flightRange,
                FlightSpeed = flightSpeed,
                OrbitPeriod = orbitPeriod
            };
            return RunLength(settings, system);
        }

        public static double RunLength(VfxRigSettings settings, VfxSystemDefinition system)
        {
            double span = VfxDurationCalculator.SystemSpan(system);
            if (settings.MotionKind == VfxRigMotionKind.Path)
            {
                double flight = settings.FlightSpeed > 0f
                    ? settings.FlightRange / settings.FlightSpeed
                    : 0d;
                return flight > 0d
                    ? flight + VfxDurationCalculator.LingerTail(system, flight)
                    : span;
            }

            return settings.MotionKind == VfxRigMotionKind.Orbit
                ? Math.Max(settings.OrbitPeriod, span)
                : span;
        }

        /// <summary>
        /// Evaluates origin, orientation basis, displacement, and lifecycle state at simulation time.
        /// </summary>
        public static VfxRigStep Evaluate(
            VfxRigPreset preset,
            double time,
            double runSpan,
            Vector3? lastOrigin = null,
            float flightRange = FlightRange,
            float flightSpeed = FlightSpeed,
            float standHeight = StandHeight,
            float orbitRadius = OrbitRadius,
            float orbitPeriod = OrbitPeriod)
        {
            VfxRigSettings settings = VfxRigSettings.ForPreset(preset) with
            {
                Height = standHeight,
                FlightRange = flightRange,
                FlightSpeed = flightSpeed,
                OrbitRadius = orbitRadius,
                OrbitPeriod = orbitPeriod
            };
            return Evaluate(settings, time, runSpan, lastOrigin);
        }

        public static VfxRigStep Evaluate(
            VfxRigSettings settings,
            double time,
            double runSpan,
            Vector3? lastOrigin = null)
        {
            float totalSpan = (float)Math.Max(runSpan, 0d);
            float phase = settings.IsLooping && totalSpan > 0f
                ? (float)(time % totalSpan)
                : (float)Math.Max(0d, time);
            switch (settings.MotionKind)
            {
                case VfxRigMotionKind.Path:
                {
                    float flightTime = settings.FlightSpeed > 0f
                        ? settings.FlightRange / settings.FlightSpeed
                        : 0f;
                    totalSpan = Math.Max(totalSpan, flightTime);
                    phase = settings.IsLooping && totalSpan > 0f
                        ? (float)(time % totalSpan)
                        : (float)Math.Max(0d, time);
                    float progress = flightTime > 0f ? Math.Clamp(phase / flightTime, 0f, 1f) : 1f;
                    bool isStopped =
                        (settings.StopAt is { } stopAt && phase >= stopAt) ||
                        (flightTime > 0f && phase >= flightTime);

                    Vector3 from = new(-settings.FlightRange * 0.5f, settings.Height, 0f);
                    Vector3 to = new(settings.FlightRange * 0.5f, settings.Height, 0f);
                    Vector3 origin = Vector3.Lerp(from, to, progress);

                    // Missile-attached VFX author travel on local +Y. The rig therefore
                    // carries +Y along the flight path, +X to the side, and +Z downward.
                    Matrix4x4 flightBasis = new(
                        0f,  0f, -1f, 0f,
                        1f,  0f,  0f, 0f,
                        0f, -1f,  0f, 0f,
                        0f,  0f,  0f, 1f
                    );
                    Matrix4x4 transform = flightBasis * Matrix4x4.CreateTranslation(origin);
                    Vector3 moved = lastOrigin.HasValue &&
                                    phase >= 0.001f &&
                                    (!settings.IsLooping || phase < totalSpan - 0.05f)
                        ? origin - lastOrigin.Value
                        : Vector3.Zero;

                    return new VfxRigStep(transform, origin, moved, isStopped, phase, totalSpan)
                    {
                        Target = to
                    };
                }

                case VfxRigMotionKind.Orbit:
                {
                    totalSpan = Math.Max(totalSpan, settings.OrbitPeriod);
                    phase = settings.IsLooping && totalSpan > 0f
                        ? (float)(time % totalSpan)
                        : (float)Math.Max(0d, time);
                    float turn = settings.OrbitPeriod > 0f
                        ? (phase / settings.OrbitPeriod) * MathF.PI * 2f
                        : 0f;

                    float x = MathF.Cos(turn) * settings.OrbitRadius;
                    float z = MathF.Sin(turn) * settings.OrbitRadius;
                    Vector3 origin = new(x, settings.Height, z);
                    float dirX = -MathF.Sin(turn);
                    float dirZ = MathF.Cos(turn);

                    Matrix4x4 yawBasis = new(
                        dirZ, 0f, -dirX, 0f,
                        0f,   1f,  0f,   0f,
                        dirX, 0f,  dirZ, 0f,
                        0f,   0f,  0f,   1f
                    );
                    Matrix4x4 transform = yawBasis * Matrix4x4.CreateTranslation(origin);
                    Vector3 moved = lastOrigin.HasValue &&
                                    phase >= 0.001f &&
                                    (!settings.IsLooping || phase < totalSpan - 0.05f)
                        ? origin - lastOrigin.Value
                        : Vector3.Zero;

                    bool isStopped = settings.StopAt is { } stopAt && phase >= stopAt;
                    return new VfxRigStep(transform, origin, moved, isStopped, phase, totalSpan)
                    {
                        Target = new Vector3(0f, settings.Height, 0f)
                    };
                }

                case VfxRigMotionKind.Still:
                default:
                {
                    Vector3 origin = new(0f, settings.Height, 0f);
                    Matrix4x4 transform = Matrix4x4.CreateTranslation(origin);
                    bool isStopped = settings.StopAt is { } stopAt && phase >= stopAt;
                    return new VfxRigStep(transform, origin, Vector3.Zero, isStopped, phase, totalSpan)
                    {
                        Target = origin + new Vector3(TargetReach, 0f, 0f)
                    };
                }
            }
        }

        /// <summary>
        /// Evaluates the same path rig used by missile preview, but for explicit endpoints and
        /// absolute scene times. Ability previews and the standalone Missile preset therefore
        /// share one orientation convention instead of maintaining separate flight math.
        /// </summary>
        internal static Matrix4x4 PathTransform(
            Vector3 from,
            Vector3 to,
            double time,
            double startTime,
            double stopTime)
        {
            double span = Math.Max(0d, stopTime - startTime);
            float progress = span > 0d
                ? (float)Math.Clamp((time - startTime) / span, 0d, 1d)
                : 1f;
            Vector3 origin = Vector3.Lerp(from, to, progress);
            Vector3 forward = new(to.X - from.X, 0f, to.Z - from.Z);
            if (forward.LengthSquared() == 0f) forward = Vector3.UnitZ;
            else forward = Vector3.Normalize(forward);

            Vector3 side = Vector3.Cross(Vector3.UnitY, forward);
            if (side.LengthSquared() == 0f) side = Vector3.UnitX;
            else side = Vector3.Normalize(side);
            Vector3 down = Vector3.Cross(side, forward);
            if (down.LengthSquared() == 0f) down = -Vector3.UnitY;
            else down = Vector3.Normalize(down);

            return new Matrix4x4(
                side.X, side.Y, side.Z, 0f,
                forward.X, forward.Y, forward.Z, 0f,
                down.X, down.Y, down.Z, 0f,
                origin.X, origin.Y, origin.Z, 1f);
        }
    }
}
