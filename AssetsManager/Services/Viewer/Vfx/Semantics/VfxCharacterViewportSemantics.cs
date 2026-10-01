using System;
using System.Numerics;

namespace AssetsManager.Services.Viewer.Vfx.Semantics
{
    internal static class VfxCharacterViewportSemantics
    {
        internal static bool ResolveSubmeshVisibility(bool authoredVisible, bool hasManualOverride, bool manualVisible) =>
            hasManualOverride ? manualVisible : authoredVisible;

        internal static bool CanAdoptLoadedBackdrop(bool enabled, string requestedKey, string loadedKey) =>
            enabled &&
            !string.IsNullOrWhiteSpace(requestedKey) &&
            !string.IsNullOrWhiteSpace(loadedKey) &&
            string.Equals(requestedKey, loadedKey, StringComparison.OrdinalIgnoreCase);

        internal static bool MapCharacterClipOwnsVfxRenderer(bool hasActiveClip, bool groupHasPreviewClip) =>
            hasActiveClip || groupHasPreviewClip;

        internal static double AdvanceAutoRotation(double degrees, double deltaSeconds, double speedDegreesPerSecond = 30d)
        {
            if (!double.IsFinite(degrees)) degrees = 0d;
            if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0d) return NormalizeDegrees(degrees);
            if (!double.IsFinite(speedDegreesPerSecond)) speedDegreesPerSecond = 30d;
            return NormalizeDegrees(degrees + deltaSeconds * speedDegreesPerSecond);
        }

        private static double NormalizeDegrees(double degrees)
        {
            double normalized = degrees % 360d;
            return normalized < 0d ? normalized + 360d : normalized;
        }

        /// <summary>
        /// Creates the complete world transform for character-attached VFX in the VFX Studio viewport.
        /// Scales X by -scaleMultiplier to match the character mesh mirror convention (GlMeshRenderer.CreateWorldMatrix with mirrorCharacterX: true).
        /// </summary>
        internal static Matrix4x4 CharacterPlacementWorld(
            double rotationX,
            double rotationY,
            double rotationZ,
            double scaleMultiplier,
            double positionX,
            double positionY,
            double positionZ)
        {
            float pitch = (float)(rotationX * Math.PI / 180d);
            float yaw = (float)(rotationY * Math.PI / 180d);
            float roll = (float)(rotationZ * Math.PI / 180d);
            float scale = (float)scaleMultiplier;
            return Matrix4x4.CreateScale(-scale, scale, scale) *
                   Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll) *
                   Matrix4x4.CreateTranslation((float)positionX, (float)positionY, (float)positionZ);
        }
    }
}
