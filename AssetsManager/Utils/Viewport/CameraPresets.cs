using System;
using System.Numerics;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Utils.Viewport
{
    internal readonly record struct CameraStand(
        Vector3 Direction,
        Vector3 Up,
        float FieldOfView,
        bool Orthographic,
        float? Nearest = null,
        float? Farthest = null);

    /// <summary>Camera presets and projection calculations for both viewer viewports.</summary>
    internal static class CameraPresets
    {
        internal const float OrbitFieldOfView = 45f;
        internal const float GameFieldOfView = 40f;
        internal const float GameNearestReach = 1000f;
        internal const float GameFarthestReach = 2250f;
        internal const float StudioNearPlane = 2f;
        internal const float StudioFarPlane = 20000f;

        private static readonly Vector3 OpeningPosition = new(
            0f,
            VfxRigMotion.ChampionHeight * 0.55f,
            VfxRigMotion.ChampionHeight * 1.5f);
        private static readonly Vector3 OpeningTarget = new(
            0f,
            VfxRigMotion.ChampionHeight * 0.40f,
            0f);

        internal static CameraStand ForStudio(VfxPreviewCameraPreset preset)
            => preset switch
            {
                VfxPreviewCameraPreset.Game => new(
                    GameDirection(),
                    Vector3.UnitY,
                    GameFieldOfView,
                    Orthographic: false,
                    Nearest: GameNearestReach,
                    Farthest: GameFarthestReach),
                VfxPreviewCameraPreset.Top => new(
                    Vector3.UnitY,
                    Vector3.UnitZ,
                    OrbitFieldOfView,
                    Orthographic: true),
                VfxPreviewCameraPreset.Front => new(
                    Vector3.UnitZ,
                    Vector3.UnitY,
                    OrbitFieldOfView,
                    Orthographic: true),
                VfxPreviewCameraPreset.Side => new(
                    Vector3.UnitX,
                    Vector3.UnitY,
                    OrbitFieldOfView,
                    Orthographic: true),
                _ => new(
                    Vector3.Normalize(OpeningPosition - OpeningTarget),
                    Vector3.UnitY,
                    OrbitFieldOfView,
                    Orthographic: false)
            };

        internal static Vector3 GameDirection()
        {
            float radians = 56f * (MathF.PI / 180f);
            return Vector3.Normalize(new Vector3(0f, MathF.Sin(radians), -MathF.Cos(radians)));
        }

        internal static float ReachOfOrthographicWidth(
            float width,
            float aspect,
            float verticalFovDegrees = OrbitFieldOfView)
        {
            float safeAspect = float.IsFinite(aspect) && aspect > 0f ? aspect : 1f;
            float visibleHeight = MathF.Max(0.001f, width) / safeAspect;
            float halfFov = MathF.Max(0.001f, verticalFovDegrees) * (MathF.PI / 360f);
            return visibleHeight / (2f * MathF.Tan(halfFov));
        }

        internal static float OrthographicWidthOfReach(
            float reach,
            float aspect,
            float verticalFovDegrees = OrbitFieldOfView)
        {
            float safeAspect = float.IsFinite(aspect) && aspect > 0f ? aspect : 1f;
            float halfFov = MathF.Max(0.001f, verticalFovDegrees) * (MathF.PI / 360f);
            float visibleHeight = MathF.Max(0.001f, reach) * 2f * MathF.Tan(halfFov);
            return visibleHeight * safeAspect;
        }

        internal static float CalculateProjectionNearPlane(
            Vector3 lookDirection,
            bool isMapGeometry = false)
        {
            float cameraDistance = lookDirection.Length();
            if (!float.IsFinite(cameraDistance) || cameraDistance <= 0f)
                return isMapGeometry ? 0.01f : 1f;

            return isMapGeometry
                ? Math.Clamp(cameraDistance * 0.001f, 0.01f, 2.5f)
                : Math.Clamp(cameraDistance * 0.01f, 0.1f, 500f);
        }

        internal static float CalculateProjectionFarPlane(Vector3 lookDirection)
        {
            float cameraDistance = lookDirection.Length();
            if (!float.IsFinite(cameraDistance) || cameraDistance <= 0f)
            {
                return 100000f;
            }

            return Math.Max(100000f, cameraDistance * 4f);
        }

        internal static (
            Point3D Position,
            Vector3D LookDirection,
            Vector3D UpDirection)? CalculateCameraView(
                string viewType,
                Point3D target,
                double distance)
        {
            if (!double.IsFinite(distance) || distance <= 0)
            {
                return null;
            }

            Vector3D worldUp = new Vector3D(0, 1, 0);
            return viewType switch
            {
                "Front" => (
                    target + new Vector3D(0, 0, distance),
                    new Vector3D(0, 0, -distance),
                    worldUp),
                "Back" => (
                    target + new Vector3D(0, 0, -distance),
                    new Vector3D(0, 0, distance),
                    worldUp),
                "Left" => (
                    target + new Vector3D(-distance, 0, 0),
                    new Vector3D(distance, 0, 0),
                    worldUp),
                "Right" => (
                    target + new Vector3D(distance, 0, 0),
                    new Vector3D(-distance, 0, 0),
                    worldUp),
                "Top" => (
                    target + new Vector3D(0, distance, 0),
                    new Vector3D(0, -distance, 0),
                    new Vector3D(0, 0, -1)),
                "Bottom" => (
                    target + new Vector3D(0, -distance, 0),
                    new Vector3D(0, distance, 0),
                    new Vector3D(0, 0, 1)),
                _ => null
            };
        }

        internal static double CalculateMapFrameDistance(double radius, double fovDegrees, double aspect)
        {
            if (!double.IsFinite(radius) || radius <= 0d ||
                !double.IsFinite(fovDegrees) || fovDegrees <= 0d || fovDegrees >= 180d ||
                !double.IsFinite(aspect) || aspect <= 0d)
            {
                return 0d;
            }

            double vertical = fovDegrees * Math.PI / 180d;
            double horizontal = 2d * Math.Atan(Math.Tan(vertical / 2d) * aspect);
            double half = Math.Min(vertical, horizontal) / 2d;
            return radius / Math.Sin(half) * 1.15d;
        }
    }
}
