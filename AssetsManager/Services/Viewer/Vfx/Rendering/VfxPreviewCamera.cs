using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    internal readonly record struct VfxCameraStand(
        Vector3 Direction,
        Vector3 Up,
        float FieldOfView,
        bool Orthographic,
        float? Nearest = null,
        float? Farthest = null);

    /// <summary>Projection and preset semantics shared by the VFX preview and its framing tests.</summary>
    internal static class VfxPreviewCamera
    {
        internal const float OrbitFieldOfView = 45f;
        internal const float GameFieldOfView = 40f;
        internal const float GameNearestReach = 1000f;
        internal const float GameFarthestReach = 2250f;
        internal const float NearPlane = 2f;
        internal const float FarPlane = 20000f;

        private static readonly Vector3 OpeningPosition = new(
            0f,
            VfxRigMotion.ChampionHeight * 0.55f,
            VfxRigMotion.ChampionHeight * 1.5f);
        private static readonly Vector3 OpeningTarget = new(
            0f,
            VfxRigMotion.ChampionHeight * 0.40f,
            0f);

        internal static VfxCameraStand Stand(VfxPreviewCameraPreset preset)
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
    }
}
