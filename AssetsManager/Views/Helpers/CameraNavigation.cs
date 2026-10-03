using System;
using System.Windows;
using System.Windows.Media.Media3D;

namespace AssetsManager.Views.Helpers
{
    /// <summary>
    /// Camera pose as the OpenGL preview projects it: FieldOfView is vertical (CreatePerspectiveFieldOfView)
    /// and orthographic presets use Width over the surface aspect.
    /// </summary>
    internal readonly record struct CameraPose(
        Point3D Position,
        Vector3D Look,
        Vector3D Up,
        double FieldOfView,
        double OrthographicWidth,
        bool Orthographic);

    /// <summary>
    /// Viewport navigation math, free of input and WPF state so it is unit tested on its own;
    /// CustomCameraController feeds it the input. MAP scenes navigate against the ground (the horizontal
    /// plane at the scene stand height): zoom toward the terrain under the cursor that glides past its
    /// closest approach and focus poses. WASD travel is shared by every viewport.
    /// </summary>
    internal static class CameraNavigation
    {
        /// <summary>Closest the camera approaches the point under the cursor before gliding over it.</summary>
        internal const double MinimumApproach = 80.0;
        /// <summary>Lowest camera height above the ground plane.</summary>
        internal const double MinimumHeight = 30.0;
        /// <summary>Wheel step as a share of the distance, bounded like the orbit zoom of the other viewports.</summary>
        internal const double ZoomFraction = 0.08;
        internal const double MinimumZoomStep = 5.0;
        internal const double MaximumZoomStep = 120.0;
        internal const double WalkSpeed = 300.0;

        /// <summary>Yaw and pitch with a level horizon; orbiting retains the current focus point.</summary>
        internal static CameraPose Rotate(CameraPose pose, double yawDegrees, double pitchDegrees, bool orbit)
        {
            double distance = pose.Look.Length;
            if (!double.IsFinite(distance) || distance < 1e-6 ||
                !double.IsFinite(yawDegrees) || !double.IsFinite(pitchDegrees) ||
                (yawDegrees == 0 && pitchDegrees == 0))
                return pose;

            Vector3D forward = pose.Look / distance;
            Vector3D heading = new(forward.X, 0, forward.Z);
            double horizontalLength = heading.Length;
            // A vertical preset has no forward heading; its screen-up direction supplies it.
            if (horizontalLength < 1e-6)
                heading = forward.Y < 0
                    ? new Vector3D(pose.Up.X, 0, pose.Up.Z)
                    : new Vector3D(-pose.Up.X, 0, -pose.Up.Z);
            if (heading.LengthSquared < 1e-12)
                heading = new Vector3D(0, 0, -1);
            heading.Normalize();

            double yaw = (yawDegrees % 360) * Math.PI / 180;
            heading = new Vector3D(
                heading.X * Math.Cos(yaw) + heading.Z * Math.Sin(yaw),
                0,
                -heading.X * Math.Sin(yaw) + heading.Z * Math.Cos(yaw));
            double pitch = Math.Atan2(forward.Y, horizontalLength);
            // Preserve exact top/bottom presets on yaw, but prevent pitching through a pole.
            if (pitchDegrees != 0)
                pitch = Math.Clamp(pitch + pitchDegrees * Math.PI / 180, -89 * Math.PI / 180, 89 * Math.PI / 180);

            Vector3D look = (heading * Math.Cos(pitch) + new Vector3D(0, Math.Sin(pitch), 0)) * distance;
            Vector3D up = -heading * Math.Sin(pitch) + new Vector3D(0, Math.Cos(pitch), 0);
            return pose with
            {
                Position = orbit ? pose.Position + pose.Look - look : pose.Position,
                Look = look,
                Up = up
            };
        }

        /// <summary>World ray through a surface pixel, matching the preview projection.</summary>
        internal static bool TryGetRay(
            CameraPose pose,
            Size surface,
            Point pixel,
            out Point3D origin,
            out Vector3D direction)
        {
            origin = pose.Position;
            direction = default;
            if (surface.Width < 1 || surface.Height < 1 || pose.Look.LengthSquared < 1e-12)
                return false;

            Vector3D forward = pose.Look;
            forward.Normalize();
            Vector3D right = Vector3D.CrossProduct(forward, pose.Up);
            if (right.LengthSquared < 1e-12)
                return false;
            right.Normalize();
            Vector3D up = Vector3D.CrossProduct(right, forward);

            double aspect = surface.Width / surface.Height;
            double ndcX = 2.0 * pixel.X / surface.Width - 1.0;
            double ndcY = 1.0 - 2.0 * pixel.Y / surface.Height;

            if (pose.Orthographic)
            {
                double halfWidth = Math.Max(1.0, pose.OrthographicWidth) * 0.5;
                origin = pose.Position + right * (ndcX * halfWidth) + up * (ndcY * halfWidth / aspect);
                direction = forward;
                return true;
            }

            double tanHalf = Math.Tan(pose.FieldOfView * Math.PI / 360.0);
            direction = forward + right * (ndcX * tanHalf * aspect) + up * (ndcY * tanHalf);
            direction.Normalize();
            return true;
        }

        internal static bool TryIntersectGround(Point3D origin, Vector3D direction, double groundY, out Point3D hit)
        {
            hit = default;
            if (Math.Abs(direction.Y) < 1e-9)
                return false;
            double distance = (groundY - origin.Y) / direction.Y;
            if (!double.IsFinite(distance) || distance <= 0)
                return false;
            hit = origin + direction * distance;
            return true;
        }

        internal static bool TryGetGroundPoint(CameraPose pose, Size surface, Point pixel, double groundY, out Point3D hit)
        {
            hit = default;
            return TryGetRay(pose, surface, pixel, out Point3D origin, out Vector3D direction) &&
                   TryIntersectGround(origin, direction, groundY, out hit);
        }

        /// <summary>
        /// Perspective zoom toward <paramref name="focus"/>. Zooming in approaches it down to
        /// <see cref="MinimumApproach"/> and then glides horizontally, so the camera never stalls;
        /// zooming out backs away up to <paramref name="maximumDistance"/>.
        /// </summary>
        internal static Point3D Zoom(
            Point3D position,
            Point3D focus,
            int delta,
            double speed,
            double groundY,
            double maximumDistance)
        {
            Vector3D toFocus = focus - position;
            double distance = toFocus.Length;
            if (delta == 0 || distance < 1e-6)
                return position;
            Vector3D direction = toFocus / distance;
            double travel = Math.Clamp(distance * ZoomFraction, MinimumZoomStep, MaximumZoomStep) *
                            Math.Max(0.01, speed);

            Point3D next;
            if (delta > 0)
            {
                double approach = Math.Min(travel, Math.Max(0.0, distance - MinimumApproach));
                next = position + direction * approach;
                double glide = travel - approach;
                Vector3D horizontal = new(direction.X, 0, direction.Z);
                if (glide > 0 && horizontal.LengthSquared > 1e-12)
                {
                    horizontal.Normalize();
                    next += horizontal * glide;
                }
            }
            else
            {
                double limit = double.IsFinite(maximumDistance) ? maximumDistance : double.MaxValue;
                double retreat = Math.Min(travel, Math.Max(0.0, limit - distance));
                next = position - direction * retreat;
            }

            return AboveGround(next, groundY);
        }

        /// <summary>
        /// WASD travel on the horizontal plane at a fixed world-space pace in every viewport.
        /// Camera distance and projection must not make MAPs travel faster than models.
        /// </summary>
        internal static Vector3D Walk(
            CameraPose pose,
            double forward,
            double strafe,
            double seconds,
            double speed)
        {
            if ((forward == 0 && strafe == 0) || seconds <= 0 || !double.IsFinite(seconds))
                return default;

            Vector3D ahead = new(pose.Look.X, 0, pose.Look.Z);
            if (ahead.LengthSquared < 1e-6)
                ahead = new Vector3D(pose.Up.X, 0, pose.Up.Z); // top-down view: screen up is ahead
            if (ahead.LengthSquared < 1e-12)
                return default;
            ahead.Normalize();
            Vector3D right = Vector3D.CrossProduct(ahead, new Vector3D(0, 1, 0));
            right.Normalize();

            Vector3D move = ahead * forward + right * strafe;
            if (move.LengthSquared < 1e-12)
                return default;
            move.Normalize();

            double pace = WalkSpeed * Math.Max(0.01, speed);
            return move * (pace * seconds);
        }

        /// <summary>
        /// Look vector that ends on the ground along the same direction, so the orbit centre is the
        /// terrain at the screen centre. Looking at or above the horizon keeps the current length.
        /// </summary>
        internal static Vector3D GroundedLook(Point3D position, Vector3D look, double groundY)
        {
            if (look.LengthSquared < 1e-12)
                return look;
            Vector3D direction = look;
            direction.Normalize();
            return TryIntersectGround(position, direction, groundY, out Point3D hit)
                ? hit - position
                : look;
        }

        /// <summary>Orthographic zoom that keeps the world point under the cursor fixed on screen.</summary>
        internal static Point3D OrthographicZoomPosition(Point3D position, Point3D cursorOnCameraPlane, double widthFactor) =>
            cursorOnCameraPlane + (position - cursorOnCameraPlane) * widthFactor;

        /// <summary>
        /// Pose that frames an engine-space map point (mirrored on X into the preview) along the current
        /// look direction, at most 1500 units away.
        /// </summary>
        internal static (Point3D Target, Point3D Position, Vector3D LookDirection)? FocusPose(
            System.Numerics.Vector3 enginePosition,
            Vector3D currentLookDirection)
        {
            double distance = currentLookDirection.Length;
            if (!double.IsFinite(distance) || distance <= 0.001)
                return null;

            Point3D target = new(-enginePosition.X, enginePosition.Y, enginePosition.Z);
            Vector3D direction = currentLookDirection;
            direction.Normalize();
            double focusDistance = Math.Min(distance, 1500d);
            Vector3D lookDirection = direction * focusDistance;
            Point3D position = target - lookDirection;
            return (target, position, lookDirection);
        }

        private static Point3D AboveGround(Point3D position, double groundY)
        {
            if (position.Y < groundY + MinimumHeight)
                position.Y = groundY + MinimumHeight;
            return position;
        }
    }
}
