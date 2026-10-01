using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Services.Viewer.Vfx.Resources;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    public sealed partial class VfxOpenGlRenderer
    {
        internal static float PaletteSelectorAtZero(VfxPaletteDefinition palette)
            => VfxShaderParameterUtils.SamplePaletteSelectorAtZero(palette);

        internal static float PaletteRowNormalized(VfxPaletteDefinition palette)
            => VfxShaderParameterUtils.ResolvePaletteRowNormalized(palette);

        internal static Vector3 ResolveCameraForward(Vector3 cameraRight, Vector3 cameraUp)
            => VfxGeometryUtils.ResolveCameraForward(cameraRight, cameraUp);

        internal static bool ShouldDirectionOrientBillboard(VfxEmitterDefinition definition)
            => VfxGeometryUtils.ShouldDirectionOrientBillboard(definition);

        internal static int ResolveEmitterDrawCount(
            VfxEmitterDefinition definition,
            int alreadyUsed,
            int instanceCount)
        {
            if (definition is null || instanceCount <= 0) return 0;

            int used = Math.Max(0, alreadyUsed);
            if (definition.DrawsAsTrail)
            {
                if (used >= VfxTrailGeometry.TrailPointsPerEmitter) return 0;
                int perSource = VfxTrailGeometry.ResolvePointCount(instanceCount);
                int remaining = VfxTrailGeometry.TrailPointsPerEmitter - used;
                // ribbon.writeTrail rejects a whole strand when its vertices do not fit. It
                // never truncates that source merely to consume the tail of the shared buffer;
                // a later, smaller source may still fit into the same remaining capacity.
                return perSource <= remaining ? perSource : 0;
            }

            int limit = definition.PrimitiveKind == VfxPrimitiveKind.AttachedMesh
                ? AttachedMeshesPerEmitter
                : definition.IsMeshPrimitive
                    ? MeshesPerEmitter
                    : definition.DrawsAsBeam
                        ? BeamsPerEmitter
                        : definition.DrawsAsQuad || definition.DrawsAsProjection
                            ? QuadsPerEmitter
                            : int.MaxValue;

            if (limit == int.MaxValue) return instanceCount;
            if (used >= limit) return 0;
            return Math.Min(instanceCount, limit - used);
        }

        internal static int ResolveAttachedMeshDrawCount(int alreadyUsed, int instanceCount)
        {
            if (instanceCount <= 0 || alreadyUsed >= AttachedMeshesPerEmitter) return 0;
            return Math.Min(instanceCount, Math.Max(0, AttachedMeshesPerEmitter - Math.Max(0, alreadyUsed)));
        }

        internal static bool ShouldSortInstances(VfxEmitterDefinition definition, int instanceCount)
            => definition is not null &&
               instanceCount > 1 &&
               definition.DrawsAsQuad &&
               (definition.HasResolvedCustomMaterial
                   ? definition.CustomMaterial.RenderState.Blending != ModelMaterialBlendMode.Opaque
                   : VfxBlendModes.ShouldSortBackToFront(definition.BlendMode));

        private static float ClampScale(float value)
            => float.IsFinite(value) ? value : 1f;

        private static bool HasAttachedDrawMatch(
            IReadOnlyList<VfxMeshRangeData> ranges,
            IReadOnlyList<uint> draw)
        {
            if (ranges == null || draw == null || draw.Count == 0) return false;
            for (int rangeIndex = 0; rangeIndex < ranges.Count; rangeIndex++)
            {
                if (ContainsHash(draw, ranges[rangeIndex].Hash)) return true;
            }
            return false;
        }

        private static bool ContainsHash(IReadOnlyList<uint> values, uint hash)
        {
            if (values == null) return false;
            for (int index = 0; index < values.Count; index++)
            {
                if (values[index] == hash) return true;
            }
            return false;
        }

        internal static bool ShouldDrawAttachedRange(
            uint hash,
            bool narrowed,
            IReadOnlyList<uint> draw,
            IReadOnlyList<uint> always,
            ISet<uint> hidden)
            => ((!narrowed || ContainsHash(draw, hash)) && !(hidden?.Contains(hash) ?? false)) ||
               ContainsHash(always, hash);

        internal static bool ShouldSampleBaseTexture(VfxEmitterDefinition definition, uint textureHandle)
            => VfxGeometryUtils.ShouldSampleBaseTexture(definition, textureHandle);

        internal static float ResolveEmitterPhase(VfxEmitterDefinition definition, float age)
            => VfxGeometryUtils.ResolveEmitterPhase(definition, age);

        internal static Vector2? ResolvePolygonOffset(VfxEmitterDefinition definition)
            => VfxGeometryUtils.ResolvePolygonOffset(definition);

        internal static bool HasTextureMultLayer(VfxEmitterDefinition definition)
            => VfxGeometryUtils.HasTextureMultLayer(definition);

        internal static bool ShouldUseColorRamp(VfxEmitterDefinition definition, bool hasColorRampTexture)
            => VfxGeometryUtils.ShouldUseColorRamp(definition, hasColorRampTexture);

        internal static bool ShouldProjectToGround(VfxEmitterDefinition definition)
            => VfxGeometryUtils.ShouldProjectToGround(definition);

        internal static bool ShouldUseSoftParticles(VfxEmitterDefinition definition, bool hasSceneDepth)
            => VfxGeometryUtils.ShouldUseSoftParticles(definition, hasSceneDepth);

        internal static Vector4 ResolveSoftParticleParams(VfxSoftParticleDefinition soft)
            => VfxShaderParameterUtils.ResolveSoftParticleParams(soft);

        internal static Vector4 ResolveSoftParticleControl(int blendMode)
            => VfxShaderParameterUtils.ResolveSoftParticleControl(blendMode);
    }
}
