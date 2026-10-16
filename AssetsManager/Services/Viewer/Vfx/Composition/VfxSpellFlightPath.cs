using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Composition;

/// <summary>
/// Where a spell missile is at each moment of its flight, simulated at a fixed 120 Hz step
/// from its authored movement component, height solver, and vertical facing.
/// </summary>
public sealed class VfxSpellFlightPath
{
    public const double FlightStep = 1d / 120d;
    public const double LongestFlight = 30d;
    public const float FallbackSpeed = 1800f;

    private const double ArcStep = 1d / 128d;
    private const double BlendExponent = 0.75d;
    private const float GroundHeight = 0f;
    private const float GroundClearance = 4f;

    private readonly Vector3[] _positions;
    private readonly Vector3[] _velocities;

    private VfxSpellFlightPath(
        Vector3[] positions,
        Vector3[] velocities,
        double duration,
        double? arrival,
        Vector3 start,
        Vector3 end)
    {
        _positions = positions;
        _velocities = velocities;
        Duration = duration;
        Arrival = arrival;
        Start = start;
        End = end;
    }

    /// <summary>Seconds from launch to arrival, or <see cref="LongestFlight"/> for an unbounded loop.</summary>
    public double Duration { get; }

    /// <summary>Seconds until the movement completes, or null if it runs past <see cref="LongestFlight"/>.</summary>
    public double? Arrival { get; }

    /// <summary>The launch point, before the movement's first step applies any spline offset.</summary>
    public Vector3 Start { get; }

    /// <summary>The resolved target, or the centre for a circling movement.</summary>
    public Vector3 End { get; }

    /// <summary>Position and travel direction <paramref name="elapsed"/> seconds after launch.</summary>
    public (Vector3 Position, Vector3 Direction) Sample(double elapsed)
        => (PositionAt(elapsed), DirectionAt(elapsed));

    /// <summary>Interpolated position <paramref name="elapsed"/> seconds into the flight.</summary>
    public Vector3 PositionAt(double elapsed)
    {
        int last = _positions.Length - 1;
        if (last <= 0) return _positions.Length == 1 ? _positions[0] : Start;
        double at = Math.Clamp(elapsed / FlightStep, 0d, last);
        int low = (int)Math.Floor(at);
        int high = Math.Min(low + 1, last);
        float share = (float)(at - low);
        return Vector3.Lerp(_positions[low], _positions[high], share);
    }

    /// <summary>Normalized 3D travel direction <paramref name="elapsed"/> seconds into the flight.</summary>
    public Vector3 DirectionAt(double elapsed)
    {
        int last = _velocities.Length - 1;
        if (last < 0) return Vector3.UnitZ;
        int at = Math.Clamp((int)Math.Floor(Math.Max(0d, elapsed) / FlightStep + .5d), 0, last);
        return UnitOrForward(_velocities[at]);
    }

    internal static bool IsCircle(VfxSpellMissileMovementKind kind)
        => kind is VfxSpellMissileMovementKind.Circle or VfxSpellMissileMovementKind.SyncCircle;

    /// <summary>
    /// Returns true if <paramref name="missile"/> can be simulated by the standalone system rig
    /// (with schema defaults applied for omitted speeds), matching LTK's <c>flightOf(spec) !== null</c>.
    /// </summary>
    internal static bool CanSimulateForRig(VfxSpellMissilePreview missile)
    {
        if (missile == null) return false;
        return missile.MovementKind is
            VfxSpellMissileMovementKind.FixedSpeed or
            VfxSpellMissileMovementKind.FixedTime or
            VfxSpellMissileMovementKind.Accelerating or
            VfxSpellMissileMovementKind.DecelToLocation or
            VfxSpellMissileMovementKind.FixedSpeedSpline or
            VfxSpellMissileMovementKind.FixedTimeSpline or
            VfxSpellMissileMovementKind.Physics or
            VfxSpellMissileMovementKind.Circle or
            VfxSpellMissileMovementKind.SyncCircle;
    }

    /// <summary>
    /// Returns true if <paramref name="missile"/> writes a positive speed or travel time for isolated spell preview.
    /// </summary>
    internal static bool WritesPace(VfxSpellMissilePreview missile)
    {
        if (missile == null) return false;
        return missile.MovementKind switch
        {
            VfxSpellMissileMovementKind.FixedSpeed or VfxSpellMissileMovementKind.FixedSpeedSpline =>
                Positive(missile.Speed),
            VfxSpellMissileMovementKind.FixedTime or VfxSpellMissileMovementKind.FixedTimeSpline =>
                Positive(missile.Duration),
            _ => CanSimulateForRig(missile)
        };
    }

    /// <summary>
    /// Compiles the flight between <paramref name="from"/> (the launch point) and <paramref name="to"/>, or null when
    /// the movement cannot be flown or never arrives in time.
    /// </summary>
    internal static VfxSpellFlightPath Compile(
        VfxSpellMissilePreview missile,
        Vector3 from,
        Vector3 to,
        bool allowUnbounded = false)
    {
        if (!CanSimulateForRig(missile)) return null;
        if (!allowUnbounded && !WritesPace(missile)) return null;

        VfxSpellMissileHeightKind heightKind = ResolveHeightKind(missile);
        VfxSpellMissileFacingKind facingKind = missile.FacingKind switch
        {
            VfxSpellMissileFacingKind.MatchVelocity => VfxSpellMissileFacingKind.MatchVelocity,
            VfxSpellMissileFacingKind.FaceTarget => VfxSpellMissileFacingKind.FaceTarget,
            _ => VfxSpellMissileFacingKind.None
        };

        float targetY = ResolveTargetY(missile, from.Y);
        Vector3 start = from;
        Vector3 target = new(to.X, targetY, to.Z);
        if (start.X == target.X && start.Z == target.Z)
            target.Z += 5f;

        IMovementStepper stepper = CreateStepper(missile, start, target);
        if (stepper == null) return null;

        int most = (int)Math.Ceiling(LongestFlight / FlightStep);
        var positions = new List<Vector3>(most + 1) { start };
        var velocities = new List<Vector3>(most + 1) { stepper.InitialVelocity };
        Vector3 position = start;
        double? arrival = null;

        for (int step = 1; step <= most; step++)
        {
            double now = step * FlightStep;
            MovementTick tick = stepper.Step(position, now, FlightStep);
            Vector3 next = position + tick.Displacement;

            if (heightKind != VfxSpellMissileHeightKind.None)
            {
                float fraction = tick.Fraction ?? TravelFraction(start, next, target, tick.Arrived);
                next.Y = SolveHeight(missile, heightKind, start, target, position, next, tick.Velocity, fraction);
            }

            Vector3 velocity = tick.Velocity;
            if (facingKind == VfxSpellMissileFacingKind.MatchVelocity)
                velocity.Y = (float)((next.Y - position.Y) / FlightStep);
            else if (facingKind == VfxSpellMissileFacingKind.FaceTarget)
                velocity.Y = tick.Speed * UnitOrForward(target - next).Y;

            position = next;
            positions.Add(position);
            velocities.Add(velocity);

            if (tick.Arrived)
            {
                arrival = now;
                break;
            }
        }

        if (!allowUnbounded && !arrival.HasValue)
            return null;

        if (velocities.Count >= 2 &&
            stepper.InitialVelocity == Vector3.Zero)
        {
            velocities[0] = velocities[1];
        }

        double duration = arrival ?? LongestFlight;
        return new VfxSpellFlightPath(
            positions.ToArray(),
            velocities.ToArray(),
            duration,
            arrival,
            start,
            target);
    }

    private static VfxSpellMissileHeightKind ResolveHeightKind(VfxSpellMissilePreview missile)
    {
        if (missile.HeightKind is not VfxSpellMissileHeightKind.None and not VfxSpellMissileHeightKind.Unsupported)
            return missile.HeightKind;
        if (missile.HeightKind == VfxSpellMissileHeightKind.Unsupported)
            return VfxSpellMissileHeightKind.None;
        if (missile.Gravity > 0f && float.IsFinite(missile.Gravity))
            return VfxSpellMissileHeightKind.Gravity;
        if (missile.LandsOnTargetHeight)
            return VfxSpellMissileHeightKind.BlendedLinear;
        if (missile.SineAmplitude != 0f && missile.SinePeriods != 0f &&
            float.IsFinite(missile.SineAmplitude) && float.IsFinite(missile.SinePeriods))
            return VfxSpellMissileHeightKind.Sinusoidal;
        return VfxSpellMissileHeightKind.None;
    }

    private static float ResolveTargetY(VfxSpellMissilePreview missile, float launchY)
    {
        float initialOffset = FiniteOr(missile.InitialTargetHeight, 0f);
        float targetAugment = FiniteOr(missile.TargetHeight, 0f);
        return initialOffset + (missile.HeightOffsetAtEnd == true ? launchY + targetAugment : GroundHeight);
    }

    private static float TravelFraction(Vector3 start, Vector3 next, Vector3 target, bool arrived)
    {
        float whole = Flat(target - start);
        if (arrived || whole <= 0f) return 1f;
        return Math.Clamp(Flat(next - start) / whole, 0f, 1f);
    }

    private static float SolveHeight(
        VfxSpellMissilePreview missile,
        VfxSpellMissileHeightKind kind,
        Vector3 start,
        Vector3 target,
        Vector3 position,
        Vector3 next,
        Vector3 velocity,
        float t)
    {
        switch (kind)
        {
            case VfxSpellMissileHeightKind.BlendedLinear:
            {
                float y = start.Y + (target.Y - start.Y) * (float)Math.Pow(t, BlendExponent);
                return y < GroundHeight ? GroundHeight + GroundClearance : y;
            }
            case VfxSpellMissileHeightKind.Gravity:
            {
                float gravity = FiniteOr(missile.HeightSolverGravity, FiniteOr(missile.Gravity, 0f));
                float across = Flat(target - start);
                if (across <= 0f) return next.Y;
                float flatSpeed = Flat(velocity);
                double total = across / (flatSpeed + 1e-5d);
                double time = total * t;
                double rise = (target.Y - start.Y) / total + 0.5d * gravity * total;
                return (float)(start.Y + rise * time - 0.5d * gravity * time * time);
            }
            case VfxSpellMissileHeightKind.FollowTerrain:
            {
                float heightOffset = FiniteOr(missile.HeightOffset, 0f);
                float maxSlope = FiniteOr(missile.MaxSlope, 10f);
                float most = Flat(next - position) * maxSlope;
                return position.Y + Math.Clamp(GroundHeight + heightOffset - position.Y, -most, most);
            }
            case VfxSpellMissileHeightKind.Sinusoidal:
            {
                float amplitude = FiniteOr(missile.SineAmplitudeRaw, FiniteOr(missile.SineAmplitude, 0f));
                float rawPeriods = FiniteOr(missile.SinePeriodsRaw, FiniteOr(missile.SinePeriods, 0f));
                float periods = rawPeriods != 0f ? rawPeriods : 1f;
                float verticalOffset = FiniteOr(missile.SineVerticalOffset, 0f);
                float phase = FiniteOr(missile.SinePhaseShift, 0f);
                double wave = Math.Sin((periods * t + phase) * Math.PI * 2d) * amplitude;
                return (float)(wave + verticalOffset + (1f - t) * start.Y + t * target.Y);
            }
            case VfxSpellMissileHeightKind.CurveTheDifference:
            {
                float lifted = FiniteOr(missile.CurveInitialTargetHeightOffset, 0f);
                return (1f - t) * (t * lifted + (1f - t) * start.Y) + t * (t * target.Y + (1f - t) * start.Y);
            }
            default:
                return next.Y;
        }
    }

    private readonly record struct MovementTick(
        Vector3 Displacement,
        Vector3 Velocity,
        float Speed,
        bool Arrived,
        float? Fraction = null);

    private interface IMovementStepper
    {
        Vector3 InitialVelocity { get; }
        MovementTick Step(Vector3 position, double now, double dt);
    }

    private static IMovementStepper CreateStepper(
        VfxSpellMissilePreview missile,
        Vector3 start,
        Vector3 target)
    {
        switch (missile.MovementKind)
        {
            case VfxSpellMissileMovementKind.FixedSpeed:
            {
                float speed = PositiveOr(missile.Speed, FallbackSpeed);
                return new FixedSpeedStepper(speed, start, target);
            }
            case VfxSpellMissileMovementKind.FixedTime:
            {
                float travelTime = PositiveOr(missile.Duration, 0f);
                return new FixedTimeStepper(travelTime, start, target);
            }
            case VfxSpellMissileMovementKind.Accelerating:
            case VfxSpellMissileMovementKind.DecelToLocation:
            {
                bool brakes = missile.MovementKind == VfxSpellMissileMovementKind.DecelToLocation;
                return AcceleratingStepper.TryCreate(missile, start, target, brakes);
            }
            case VfxSpellMissileMovementKind.FixedSpeedSpline:
            {
                float speed = PositiveOr(missile.Speed, FallbackSpeed);
                var stepper = new SplineStepper(missile, start, target, speed, null);
                return stepper;
            }
            case VfxSpellMissileMovementKind.FixedTimeSpline:
            {
                float travelTime = PositiveOr(missile.Duration, 0f);
                var stepper = new SplineStepper(missile, start, target, null, travelTime);
                return stepper;
            }
            case VfxSpellMissileMovementKind.Physics:
                return new PhysicsStepper(
                    NonNegativeOr(missile.InitialSpeed, 0f),
                    FiniteOr(missile.Drag, 1f),
                    NonNegativeOr(missile.Lifetime, 0f),
                    start,
                    target);
            case VfxSpellMissileMovementKind.Circle:
                return new CircleStepper(
                    FiniteOr(missile.AngularVelocity, 0f),
                    FiniteOr(missile.LinearVelocity, 0f),
                    FiniteOr(missile.RadialVelocity, 0f),
                    NonNegativeOr(missile.Lifetime, 0f),
                    start,
                    target);
            case VfxSpellMissileMovementKind.SyncCircle:
                return new SyncCircleStepper(
                    FiniteOr(missile.AngularVelocity, 0f),
                    NonNegativeOr(missile.Lifetime, 0f),
                    missile.SyncCircleAxis ?? 1,
                    start,
                    target);
            default:
                return null;
        }
    }

    private sealed class FixedSpeedStepper : IMovementStepper
    {
        private readonly float _speed;
        private readonly Vector3 _target;

        public FixedSpeedStepper(float speed, Vector3 start, Vector3 target)
        {
            _speed = speed;
            _target = target;
            InitialVelocity = HorizontalLength(target - start, speed);
        }

        public Vector3 InitialVelocity { get; }

        public MovementTick Step(Vector3 position, double now, double dt)
        {
            Vector3 velocity = HorizontalLength(_target - position, _speed);
            return Landing(position, _target, velocity, velocity * (float)dt, _speed);
        }
    }

    private sealed class FixedTimeStepper : IMovementStepper
    {
        private readonly double _travelTime;
        private readonly Vector3 _target;
        private float _speed;

        public FixedTimeStepper(float travelTime, Vector3 start, Vector3 target)
        {
            _travelTime = travelTime;
            _target = target;
            _speed = travelTime > 0f ? Flat(target - start) / travelTime : 0f;
            InitialVelocity = HorizontalLength(target - start, _speed);
        }

        public Vector3 InitialVelocity { get; }

        public MovementTick Step(Vector3 position, double now, double dt)
        {
            double remaining = _travelTime - (now - dt);
            if (remaining >= 1e-9d) _speed = (float)(Flat(_target - position) / remaining);
            Vector3 velocity = HorizontalLength(_target - position, _speed);
            bool arrived = now + 1e-9d >= _travelTime;
            Vector3 displacement = arrived ? _target - position : velocity * (float)Math.Min(remaining, dt);
            return new MovementTick(displacement, velocity, _speed, arrived);
        }
    }

    private sealed class AcceleratingStepper : IMovementStepper
    {
        private readonly Vector3 _target;
        private readonly double _least;
        private readonly double _most;
        private readonly double _rate;
        private readonly bool _brakes;
        private double _speed;
        private Vector3 _velocity;

        private AcceleratingStepper(
            Vector3 start,
            Vector3 target,
            double initialSpeed,
            double rate,
            double least,
            double most,
            bool brakes)
        {
            _target = target;
            _least = least;
            _most = most;
            _rate = rate;
            _brakes = brakes;
            _speed = initialSpeed;
            _velocity = HorizontalLength(target - start, (float)initialSpeed);
            InitialVelocity = _velocity;
        }

        public Vector3 InitialVelocity { get; }

        public static AcceleratingStepper TryCreate(
            VfxSpellMissilePreview missile,
            Vector3 start,
            Vector3 target,
            bool brakes)
        {
            double accel = missile.Acceleration ?? 0d;
            double least = missile.MinSpeed ?? 0d;
            double most = missile.MaxSpeed ?? 0d;
            if (!double.IsFinite(accel)) return null;
            double initial = missile.InitialSpeed ?? 0d;
            double rate = brakes ? Math.Abs(accel) : accel;
            return new AcceleratingStepper(start, target, initial, rate, least, most, brakes);
        }

        public MovementTick Step(Vector3 position, double now, double dt)
        {
            float remaining = Flat(_target - position);
            double brakeDivisor = 2d * _rate != 0d ? 2d * _rate : 1d;
            if (_brakes && (_speed * _speed) / brakeDivisor >= remaining)
            {
                Vector3 ahead = HorizontalLength(_target - position, 1f);
                double travel = Math.Max(_speed * dt - 0.5d * _rate * dt * dt, 0d);
                _speed = Math.Max(_speed - _rate * dt, 0d);
                _velocity = ahead * (float)_speed;
                return Landing(position, _target, _velocity, ahead * (float)travel, (float)_speed, _speed <= 0d);
            }

            Vector3 along = UnitOrForward(_velocity);
            double held = Math.Min(Math.Max(_speed, _least), _most);
            bool pinned = (_rate < 0d && _speed == _least) || (_rate > 0d && _speed == _most);
            Vector3 displacement;
            if (pinned || _rate == 0d)
            {
                displacement = _velocity * (float)dt;
            }
            else if (held + _rate * dt > _least && held + _rate * dt < _most)
            {
                displacement = _velocity * (float)dt + along * (float)(0.5d * _rate * dt * dt);
            }
            else
            {
                double limit = _rate > 0d ? _most : _least;
                double until = (limit - held) / _rate;
                Vector3 ramp = _velocity * (float)until + along * (float)(0.5d * _rate * until * until);
                displacement = ramp + along * (float)((dt - until) * limit);
            }

            if (remaining > 0.001f)
            {
                if (_rate != 0d) _speed = Math.Min(Math.Max(_speed + _rate * dt, _least), _most);
                _velocity = HorizontalLength(_target - position, (float)_speed);
            }

            return Landing(position, _target, _velocity, displacement, (float)_speed);
        }
    }

    private sealed class PhysicsStepper : IMovementStepper
    {
        private readonly float _initialSpeed;
        private readonly double _reach;
        private readonly float _lifetime;
        private readonly Vector3 _start;
        private readonly Vector3 _direction;

        public PhysicsStepper(float initialSpeed, float drag, float lifetime, Vector3 start, Vector3 target)
        {
            _initialSpeed = initialSpeed;
            _reach = drag != 0f ? 1d / drag : 1e6d;
            _lifetime = lifetime;
            _start = start;
            _direction = UnitOrForward(target - start);
            InitialVelocity = _direction * initialSpeed;
        }

        public Vector3 InitialVelocity { get; }

        private Vector3 PositionAt(double time)
        {
            float along = (float)(_initialSpeed * _reach * (1d - Math.Exp(-time / _reach)));
            return new Vector3(_start.X + _direction.X * along, _start.Y, _start.Z + _direction.Z * along);
        }

        public MovementTick Step(Vector3 position, double now, double dt)
        {
            Vector3 displacement = PositionAt(now) - position;
            // Differencing rounded world positions amplifies float error as drag slows the missile.
            double distance = _initialSpeed * _reach * (Math.Exp(-(now-dt)/_reach)-Math.Exp(-now/_reach));
            Vector3 velocity = _direction * (float)(distance/dt);
            velocity.Y = displacement.Y / (float)dt;
            return new MovementTick(displacement, velocity, Flat(velocity), now > _lifetime);
        }
    }

    private sealed class CircleStepper : IMovementStepper
    {
        private readonly float _angularVelocity;
        private readonly float _linearVelocity;
        private readonly float _radialVelocity;
        private readonly float _lifetime;
        private readonly Vector3 _center;
        private double _radius;
        private double _angle;

        public CircleStepper(
            float angularVelocity,
            float linearVelocity,
            float radialVelocity,
            float lifetime,
            Vector3 start,
            Vector3 center)
        {
            _angularVelocity = angularVelocity;
            _linearVelocity = linearVelocity;
            _radialVelocity = radialVelocity;
            _lifetime = lifetime;
            _center = center;
            _radius = Math.Sqrt((start.X - center.X) * (start.X - center.X) + (start.Z - center.Z) * (start.Z - center.Z));
            _angle = Math.Atan2(start.Z - center.Z, start.X - center.X);
        }

        public Vector3 InitialVelocity => Vector3.Zero;

        public MovementTick Step(Vector3 position, double now, double dt)
        {
            double turn = _angularVelocity != 0f
                ? _angularVelocity
                : _radius > 0d
                    ? _linearVelocity / _radius
                    : 0d;
            _angle += turn * dt;
            Vector3 next = new(
                (float)(_center.X + _radius * Math.Cos(_angle)),
                _center.Y,
                (float)(_center.Z + _radius * Math.Sin(_angle)));
            _radius += _radialVelocity * dt;
            Vector3 displacement = next - position;
            float speed = (float)Math.Abs(turn * _radius);
            if (speed == 0f) speed = _linearVelocity;
            return new MovementTick(
                displacement,
                displacement * (float)(1d / dt),
                speed,
                _lifetime > 0f && now > _lifetime);
        }
    }

    private sealed class SyncCircleStepper : IMovementStepper
    {
        private readonly float _angularVelocity;
        private readonly float _lifetime;
        private readonly byte _axis;
        private readonly Vector3 _center;
        private readonly double _radius;
        private readonly double _firstAngle;

        public SyncCircleStepper(
            float angularVelocity,
            float lifetime,
            byte axis,
            Vector3 start,
            Vector3 center)
        {
            _angularVelocity = angularVelocity;
            _lifetime = lifetime;
            _axis = axis;
            _center = center;
            _radius = Math.Sqrt((start.X - center.X) * (start.X - center.X) + (start.Z - center.Z) * (start.Z - center.Z));
            _firstAngle = Math.Atan2(start.Z - center.Z, start.X - center.X);
        }

        public Vector3 InitialVelocity => Vector3.Zero;

        private Vector3 PositionAt(double time)
        {
            double angle = _firstAngle + _angularVelocity * time;
            float cos = (float)(_radius * Math.Cos(angle));
            float sin = (float)(_radius * Math.Sin(angle));
            if (_axis == 0) return new Vector3(_center.X, _center.Y + cos, _center.Z + sin);
            if (_axis == 2) return new Vector3(_center.X + cos, _center.Y + sin, _center.Z);
            return new Vector3(_center.X + cos, _center.Y, _center.Z + sin);
        }

        public MovementTick Step(Vector3 position, double now, double dt)
        {
            Vector3 displacement = PositionAt(now) - position;
            return new MovementTick(
                displacement,
                displacement * (float)(1d / dt),
                (float)Math.Abs(_angularVelocity * _radius),
                _lifetime > 0f && now > _lifetime);
        }
    }

    private sealed class SplineStepper : IMovementStepper
    {
        private readonly Vector3 _origin, _forward, _right, _offset, _end, _t0, _t1;
        private readonly float _distance;
        private readonly float? _speed, _travelTime;
        private double _t;

        public SplineStepper(VfxSpellMissilePreview missile, Vector3 start, Vector3 target, float? speed, float? travelTime)
        {
            _origin = missile.SplineFromMissile ? start : new Vector3(start.X, GroundHeight, start.Z);
            _forward = HorizontalLength(target - _origin, 1f);
            _right = new Vector3(_forward.Z, 0f, -_forward.X);
            Vector3 relative = target - _origin;
            _end = new Vector3(Vector3.Dot(relative, _right), relative.Y, Vector3.Dot(relative, _forward));
            _offset = missile.SplineStartOffset;
            _distance = Flat(_end - _offset);
            _t0 = missile.SplineControlPoint1 * _distance;
            _t1 = missile.SplineControlPoint2 * _distance;
            _speed = speed;
            _travelTime = travelTime;
            InitialVelocity = WorldDirection(Tangent(0f));
        }

        public Vector3 InitialVelocity { get; }

        private Vector3 Local(float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return (2f*t3-3f*t2+1f)*_offset + (t3-2f*t2+t)*_t0 +
                (3f*t2-2f*t3)*_end + (t3-t2)*_t1;
        }

        private Vector3 Tangent(float t)
            => (6f*t*t-6f*t)*(_offset-_end) + (3f*t*t-4f*t+1f)*_t0 + (3f*t*t-2f*t)*_t1;

        private Vector3 World(Vector3 p)
            => new(_origin.X+p.X*_right.X+p.Z*_forward.X, _origin.Y+p.Y, _origin.Z+p.X*_right.Z+p.Z*_forward.Z);

        private Vector3 WorldDirection(Vector3 p)
            => UnitOrForward(new(p.X*_right.X+p.Z*_forward.X, p.Y, p.X*_right.Z+p.Z*_forward.Z));

        public MovementTick Step(Vector3 position, double now, double dt)
        {
            if (_travelTime.HasValue)
                _t = _travelTime.Value > 0f ? _t + dt / _travelTime.Value : 1d;
            else
            {
                double left = dt * (_speed ?? 0f);
                while (left > 0d && _t < 1d)
                {
                    double piece = Flat(Tangent((float)_t)) * ArcStep;
                    if (piece <= 0d) break;
                    if (piece >= left) { _t += ArcStep * left / piece; left = 0d; }
                    else { _t += ArcStep; left -= piece; }
                }
            }
            bool arrived = _t >= 1d;
            float at = (float)Math.Min(_t,1d);
            float speed = _speed ?? (_travelTime is > 0f ? _distance / _travelTime.Value : 0f);
            return new(World(Local(at))-position, WorldDirection(Tangent(at)), speed, arrived, at);
        }
    }

    private static MovementTick Landing(
        Vector3 position,
        Vector3 target,
        Vector3 velocity,
        Vector3 displacement,
        float speed,
        bool stopped = false)
    {
        Vector3 next = position + displacement;
        Vector3 toTarget = target - position;
        bool passed =
            (position.X == target.X && position.Z == target.Z) ||
            (FlatDot(velocity, toTarget) > 0f && FlatDot(velocity, target - next) <= 0f);
        bool arrived = passed || stopped || Flat(target - next) <= 1e-2f;
        return new MovementTick(arrived ? toTarget : displacement, velocity, speed, arrived);
    }

    private static Vector3 HorizontalLength(Vector3 vector, float length)
    {
        float across = Flat(vector);
        return across == 0f ? Vector3.Zero : vector * (length / across);
    }

    private static float Flat(Vector3 vector)
        => MathF.Sqrt(vector.X * vector.X + vector.Z * vector.Z);

    private static float FlatDot(Vector3 a, Vector3 b)
        => a.X * b.X + a.Z * b.Z;

    private static Vector3 UnitOrForward(Vector3 vector)
    {
        float length = vector.Length();
        return length <= 1e-8f ? Vector3.UnitZ : vector / length;
    }

    private static bool Positive(float? value) => value is > 0f && float.IsFinite(value.Value);

    private static float PositiveOr(float? value, float fallback)
        => value is > 0f && float.IsFinite(value.Value) ? value.Value : fallback;

    private static float NonNegativeOr(float? value, float fallback)
        => value is >= 0f && float.IsFinite(value.Value) ? value.Value : fallback;

    private static float FiniteOr(float? value, float fallback)
        => value is { } v && float.IsFinite(v) ? v : fallback;
}
