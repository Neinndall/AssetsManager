using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Utils.Rendering
{
    /// <summary>
    /// Utility functions for camera, billboard geometry, and emitter render pass semantics.
    /// </summary>
    public static class VfxGeometryUtils
    {
        public static Vector3 ResolveCameraForward(Vector3 cameraRight, Vector3 cameraUp)
        {
            Vector3 forward = Vector3.Cross(cameraUp, cameraRight);
            return forward.LengthSquared() > 0f ? Vector3.Normalize(forward) : -Vector3.UnitZ;
        }

        public static bool ShouldDirectionOrientBillboard(VfxEmitterDefinition definition)
        {
            if (definition is null || !definition.IsDirectionOriented) return false;
            if (definition.IsSimpleEmitter) return false;
            return definition.PrimitiveKind is VfxPrimitiveKind.CameraQuad or VfxPrimitiveKind.CameraUnitQuad;
        }

        public static bool ShouldDirectionOrientTail(VfxEmitterDefinition definition)
        {
            if (definition is null || !definition.IsDirectionOriented) return false;
            return definition.PrimitiveKind is VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail;
        }

        public static Vector2? ResolvePolygonOffset(VfxEmitterDefinition definition)
        {
            if (definition is null) return null;

            if (definition.DepthBiasFactors is { } authored &&
                (authored.X != 0f || authored.Y != 0f))
            {
                return authored;
            }

            // AttachedMesh takes an overlay depth bias whenever no explicit bias is authored.
            return definition.PrimitiveKind == VfxPrimitiveKind.AttachedMesh
                ? new Vector2(-1f, -1f)
                : null;
        }

        public static bool HasTextureMultLayer(VfxEmitterDefinition definition)
            => definition is not null &&
               (definition.AuthoredFeatures?.HasTextureMultLayer == true ||
                !string.IsNullOrWhiteSpace(definition.TextureMultPath));

        public static bool ShouldUseColorRamp(VfxEmitterDefinition definition, bool hasColorRampTexture)
        {
            if (!hasColorRampTexture || definition is null) return false;

            bool mesh = definition.PrimitiveKind is VfxPrimitiveKind.Mesh or VfxPrimitiveKind.AttachedMesh;
            bool fixedAlphaUv = definition.UvMode == 2 && !mesh;
            bool erosionEnabled = definition.AlphaErosion is not null && !fixedAlphaUv;
            bool hasMultLayer = HasTextureMultLayer(definition);
            // Original mesh MULT_PASS shaders omit the ramp; quad/ribbon MULT_PASS samples it except in LOCK_ALPHA.
            return !erosionEnabled && !(hasMultLayer && (mesh || definition.UvMode == 2));
        }

        public static bool ShouldProjectToGround(VfxEmitterDefinition definition)
            => definition is not null && definition.IsGroundLayer;

        public static bool ShouldUseSoftParticles(VfxEmitterDefinition definition, bool hasSceneDepth)
        {
            if (!hasSceneDepth || definition?.SoftParticle is null) return false;
            if (definition.PrimitiveKind is VfxPrimitiveKind.AttachedMesh or VfxPrimitiveKind.PlanarProjection) return false;

            bool fixedAlphaUv = definition.UvMode == 2 &&
                definition.PrimitiveKind != VfxPrimitiveKind.Mesh &&
                definition.PrimitiveKind != VfxPrimitiveKind.AttachedMesh;
            return !fixedAlphaUv;
        }

        public static bool ShouldSampleBaseTexture(VfxEmitterDefinition definition, uint textureHandle)
        {
            if (definition?.HasResolvedCustomMaterial == true)
            {
                return textureHandle != 0 &&
                    !string.IsNullOrWhiteSpace(definition.CustomMaterial?.BaseTextureName);
            }

            return textureHandle != 0 || string.IsNullOrWhiteSpace(definition?.TexturePath);
        }

        public static float ResolveEmitterPhase(VfxEmitterDefinition definition, float age)
        {
            return definition is null || !float.IsFinite(age) ? 0f
                : AssetsManager.Services.Viewer.Vfx.Runtime.VfxPlaybackRuntime.EmitterPhase(definition, age);
        }
    }
}
