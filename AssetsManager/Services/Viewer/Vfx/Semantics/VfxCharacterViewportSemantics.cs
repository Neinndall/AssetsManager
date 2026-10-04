using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Utils.Viewport;
using AssetsManager.Views.Models.Viewer;

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

        internal static void RotateSelectedActors(IEnumerable<VfxSceneActor> actors, double deltaSeconds, string backdropKey)
        {
            foreach (VfxSceneActor actor in actors)
            {
                if (!actor.IsSelected) continue;
                actor.RotationY = ViewportToolUtils.AdvanceAutoRotation(actor.RotationY, deltaSeconds);
                actor.PlacementCustomized = true;
                actor.PlacedOnKey = backdropKey;
            }
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
