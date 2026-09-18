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
            double span = VfxDurationCalculator.SystemSpan(system);
            if (preset == VfxRigPreset.Missile)
            {
                double flight = flightSpeed > 0f ? flightRange / flightSpeed : 0d;
                return flight > 0d ? flight + VfxDurationCalculator.LingerTail(system, flight) : span;
            }
            return preset == VfxRigPreset.Trail ? Math.Max(orbitPeriod, span) : span;
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
            switch (preset)
            {
                case VfxRigPreset.Missile:
                {
                    float flightTime = flightSpeed > 0f ? flightRange / flightSpeed : 0f;
                    float totalSpan = (float)Math.Max(runSpan, flightTime);
                    float phase = totalSpan > 0f ? (float)(time % totalSpan) : (float)time;
                    float progress = flightTime > 0f ? Math.Clamp(phase / flightTime, 0f, 1f) : 1f;
                    bool isStopped = flightTime > 0f && phase >= flightTime;

                    Vector3 from = new(-flightRange * 0.5f, standHeight, 0f);
                    Vector3 to = new(flightRange * 0.5f, standHeight, 0f);
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
                    Vector3 moved = lastOrigin.HasValue && phase >= 0.001f && phase < totalSpan - 0.05f
                        ? origin - lastOrigin.Value
                        : Vector3.Zero;

                    return new VfxRigStep(transform, origin, moved, isStopped, phase, totalSpan)
                    {
                        Target = to
                    };
                }

                case VfxRigPreset.Trail:
                {
                    float totalSpan = (float)Math.Max(runSpan, orbitPeriod);
                    // LTK's trail preview has life:"once": its clock phase keeps advancing
                    // even though the orbit position itself repeats every OrbitPeriod.
                    float phase = (float)Math.Max(0d, time);
                    float turn = orbitPeriod > 0f ? (phase / orbitPeriod) * MathF.PI * 2f : 0f;

                    float x = MathF.Cos(turn) * orbitRadius;
                    float z = MathF.Sin(turn) * orbitRadius;
                    Vector3 origin = new(x, standHeight, z);

                    // Tangent vector: (-sin(turn), 0, cos(turn))
                    float dirX = -MathF.Sin(turn);
                    float dirZ = MathF.Cos(turn);

                    // Yaw basis:
                    // Row 0:  dirZ, 0, -dirX, 0
                    // Row 1:     0, 1,     0, 0
                    // Row 2:  dirX, 0,  dirZ, 0
                    Matrix4x4 yawBasis = new(
                        dirZ, 0f, -dirX, 0f,
                        0f,   1f,  0f,   0f,
                        dirX, 0f,  dirZ, 0f,
                        0f,   0f,  0f,   1f
                    );
                    Matrix4x4 transform = yawBasis * Matrix4x4.CreateTranslation(origin);
                    Vector3 moved = lastOrigin.HasValue && phase >= 0.001f
                        ? origin - lastOrigin.Value
                        : Vector3.Zero;

                    return new VfxRigStep(transform, origin, moved, false, phase, totalSpan)
                    {
                        Target = new Vector3(0f, standHeight, 0f)
                    };
                }

                case VfxRigPreset.Burst:
                {
                    float totalSpan = (float)runSpan;
                    float phase = totalSpan > 0f ? (float)(time % totalSpan) : (float)time;
                    Vector3 origin = new(0f, standHeight, 0f);
                    Matrix4x4 transform = Matrix4x4.CreateTranslation(origin);
                    return new VfxRigStep(transform, origin, Vector3.Zero, false, phase, totalSpan)
                    {
                        Target = origin + new Vector3(TargetReach, 0f, 0f)
                    };
                }

                case VfxRigPreset.Still:
                default:
                {
                    float totalSpan = (float)runSpan;
                    Vector3 origin = new(0f, standHeight, 0f);
                    Matrix4x4 transform = Matrix4x4.CreateTranslation(origin);
                    return new VfxRigStep(transform, origin, Vector3.Zero, false, (float)time, totalSpan)
                    {
                        Target = origin + new Vector3(TargetReach, 0f, 0f)
                    };
                }
            }
        }
    }
}
