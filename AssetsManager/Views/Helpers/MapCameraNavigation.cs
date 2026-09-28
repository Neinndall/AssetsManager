using System;
using System.Windows;
using System.Windows.Media.Media3D;

namespace AssetsManager.Views.Helpers
{
    /// <summary>
    /// Camera pose as the OpenGL preview projects it: FieldOfView is vertical (CreatePerspectiveFieldOfView)
    /// and orthographic presets use Width over the surface aspect.
    /// </summary>
    internal readonly record struct MapCameraPose(
        Point3D Position,
        Vector3D Look,
        Vector3D Up,
        double FieldOfView,
        double OrthographicWidth,
        bool Orthographic);

    /// <summary>
    /// Ground-anchored navigation for large MAP scenes: zoom toward the terrain under the cursor that
    /// glides past its closest approach, drag that keeps the grabbed ground point under the cursor, and
    /// WASD travel over the ground. The ground is the horizontal plane at the scene stand height.
    /// </summary>
    internal static class MapCameraNavigation
    {
        /// <summary>Closest the camera approaches the point under the cursor before gliding over it.</summary>
        internal const double MinimumApproach = 80.0;
        /// <summary>Lowest camera height above the ground plane.</summary>
        internal const double MinimumHeight = 30.0;
        internal const double ZoomFraction = 0.18;
        /// <summary>Shortest wheel step, so gliding past the cursor point keeps a usable pace.</summary>
        internal const double MinimumZoomStep = 60.0;
        internal const double MinimumWalkSpeed = 300.0;
        internal const double MaximumWalkSpeed = 9000.0;

        /// <summary>World ray through a surface pixel, matching the preview projection.</summary>
        internal static bool TryGetRay(
            MapCameraPose pose,
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

        internal static bool TryGetGroundPoint(MapCameraPose pose, Size surface, Point pixel, double groundY, out Point3D hit)
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
            double travel = Math.Max(distance * ZoomFraction, MinimumZoomStep) * Math.Max(0.01, speed);

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
        /// Horizontal translation that brings <paramref name="grabbed"/> back under the cursor, whose
        /// ray currently meets the ground at <paramref name="current"/>.
        /// </summary>
        internal static Vector3D DragTranslation(Point3D grabbed, Point3D current) =>
            new(grabbed.X - current.X, 0, grabbed.Z - current.Z);

        /// <summary>
        /// WASD travel over the ground. <paramref name="forward"/> and <paramref name="strafe"/> are
        /// -1, 0 or 1; the pace scales with the camera height so a whole map stays a few seconds away.
        /// </summary>
        internal static Vector3D Walk(
            MapCameraPose pose,
            double forward,
            double strafe,
            double seconds,
            double speed,
            double groundY)
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

            double reach = pose.Orthographic
                ? Math.Max(1.0, pose.OrthographicWidth) * 0.6
                : Math.Max(pose.Position.Y - groundY, MinimumHeight) * 1.5;
            double pace = Math.Clamp(reach, MinimumWalkSpeed, MaximumWalkSpeed) * Math.Max(0.01, speed);
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

        private static Point3D AboveGround(Point3D position, double groundY)
        {
            if (position.Y < groundY + MinimumHeight)
                position.Y = groundY + MinimumHeight;
            return position;
        }
    }
}
