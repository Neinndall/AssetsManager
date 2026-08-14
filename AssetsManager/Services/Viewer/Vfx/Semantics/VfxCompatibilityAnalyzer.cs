using System;
using System.Collections.Generic;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Semantics
{
    public static class VfxCompatibilityAnalyzer
    {
        public static VfxCompatibilityReport Analyze(
            VfxSystemDefinition system,
            VfxOwnerSceneContext ownerSceneContext = null)
        {
            ArgumentNullException.ThrowIfNull(system);
            var issues = new List<VfxCompatibilityIssue>();

            if (system.AuthoredFeatures?.HasMaterialOverrides == true)
            {
                Add(issues, "SYSTEM_MATERIAL_OVERRIDES", null, "Material overrides",
                    "The system declares material overrides that are not reproduced by the standalone VFX shader.",
                    VfxCompatibilitySeverity.Unsupported);
            }

            if (system.AuthoredFeatures?.HasAssetRemapping == true)
            {
                Add(issues, "ASSET_REMAPPING", null, "Asset remapping",
                    "The system remaps authored assets and may require skin or gameplay context.",
                    VfxCompatibilitySeverity.Context);
            }

            foreach (VfxEmitterDefinition emitter in system.Emitters)
            {
                if (emitter.Disabled) continue;
                string emitterName = string.IsNullOrWhiteSpace(emitter.Name) ? "Emitter" : emitter.Name;
                VfxEmitterAuthoredFeatures authored = emitter.AuthoredFeatures;

                if (emitter.PrimitiveKind == VfxPrimitiveKind.Unsupported)
                {
                    string primitiveHash = authored?.PrimitiveClassHash > 0
                        ? $"0x{authored.PrimitiveClassHash:x8}"
                        : "unknown";
                    Add(issues, "UNSUPPORTED_PRIMITIVE", emitterName, "Unsupported primitive",
                        $"Primitive {primitiveHash} has no renderer implementation.",
                        VfxCompatibilitySeverity.Unsupported);
                }
                else if (emitter.PrimitiveKind == VfxPrimitiveKind.AttachedMesh)
                {
                    bool hasOwnerGeometry = !string.IsNullOrWhiteSpace(ownerSceneContext?.MeshPath) &&
                        emitter.AttachedSubmeshHashes is { Count: > 0 };
                    Add(
                        issues,
                        hasOwnerGeometry ? "OWNER_BIND_POSE" : "OWNER_ATTACHED_MESH",
                        emitterName,
                        hasOwnerGeometry ? "Owner bind-pose approximation" : "Owner model required",
                        hasOwnerGeometry
                            ? "AttachedMesh uses the authored owner submesh and skin scale, but no gameplay animation pose is available."
                            : "AttachedMesh requires the owning champion mesh and authored submesh mask; no proxy is rendered.",
                        hasOwnerGeometry ? VfxCompatibilitySeverity.Approximation : VfxCompatibilitySeverity.Context);
                }
                else if (emitter.PrimitiveKind == VfxPrimitiveKind.PlanarProjection)
                {
                    Add(issues, "PLANAR_PROJECTION", emitterName, "Planar projection approximation",
                        "Planar projection is displayed on the preview ground plane without Riot scene projection data.",
                        VfxCompatibilitySeverity.Approximation);
                }

                if (emitter.IsFollowingTerrain || emitter.UseNavmeshMask)
                {
                    Add(issues, "TERRAIN_CONTEXT", emitterName, "Terrain context required",
                        "Terrain and navmesh authored placement is approximated on the flat preview ground.",
                        VfxCompatibilitySeverity.Approximation);
                }

                if (emitter.RateByVelocityFunction is not null)
                {
                    Add(issues, "RATE_BY_VELOCITY", emitterName, "Velocity-driven emission",
                        "The authored emission rate depends on owner movement that is unavailable in standalone playback.",
                        VfxCompatibilitySeverity.Context);
                }

                if (emitter.HasPostRotateOrientation || authored?.HasPostRotateOrientationAxis == true)
                {
                    Add(issues, "POST_ROTATE_ORIENTATION", emitterName, "Post-rotation orientation",
                        "The authored post-rotation stage is not yet evaluated by the particle runtime.",
                        VfxCompatibilitySeverity.Approximation);
                }

                byte stencilMode = (emitter.RenderState ?? VfxEmitterRenderState.Default).StencilMode;
                if (!VfxStencilSemantics.TryGetDescriptor(stencilMode, out _))
                {
                    Add(issues, "STENCIL_MODE", emitterName, "Unsupported stencil mode",
                        $"Authored stencil mode {stencilMode} has no verified preview operation.",
                        VfxCompatibilitySeverity.Unsupported);
                }

                AddUnsupportedAuthoredFeatures(issues, emitterName, authored);
            }

            VfxCompatibilityLevel level = ResolveLevel(issues);
            return new VfxCompatibilityReport(level, issues);
        }

        private static void AddUnsupportedAuthoredFeatures(
            ICollection<VfxCompatibilityIssue> issues,
            string emitterName,
            VfxEmitterAuthoredFeatures authored)
        {
            if (authored is null) return;
            if (authored.HasCustomMaterial)
                Add(issues, "CUSTOM_MATERIAL", emitterName, "Custom material", "The emitter requires a Riot custom material shader.", VfxCompatibilitySeverity.Unsupported);
            if (authored.HasEmissionMesh || authored.HasEmissionSurface)
                Add(issues, "SURFACE_EMISSION", emitterName, "Mesh or surface emission", "Particle births from authored mesh surfaces are not yet sampled.", VfxCompatibilitySeverity.Unsupported);
            if (authored.UsesEmissionMeshNormal)
                Add(issues, "EMISSION_NORMAL", emitterName, "Emission mesh normals", "Particle direction from emission-surface normals is not yet evaluated.", VfxCompatibilitySeverity.Unsupported);
            if (authored.HasTranslationOverride || authored.HasRotationOverride || authored.HasScaleOverride)
                Add(issues, "TRANSFORM_OVERRIDES", emitterName, "Transform overrides", "Authored emitter transform overrides are not yet applied.", VfxCompatibilitySeverity.Unsupported);
            if (authored.HasPeriodControl)
                Add(issues, "PERIOD_CONTROL", emitterName, "Active period control", "Authored period and active-window timing is not yet evaluated.", VfxCompatibilitySeverity.Unsupported);
        }

        private static VfxCompatibilityLevel ResolveLevel(IReadOnlyCollection<VfxCompatibilityIssue> issues)
        {
            bool unsupported = false;
            bool approximation = false;
            bool context = false;
            foreach (VfxCompatibilityIssue issue in issues)
            {
                unsupported |= issue.Severity == VfxCompatibilitySeverity.Unsupported;
                approximation |= issue.Severity == VfxCompatibilitySeverity.Approximation;
                context |= issue.Severity == VfxCompatibilitySeverity.Context;
            }

            if (unsupported) return VfxCompatibilityLevel.Limited;
            if (approximation) return VfxCompatibilityLevel.Approximate;
            if (context) return VfxCompatibilityLevel.ContextRequired;
            return VfxCompatibilityLevel.Ready;
        }

        private static void Add(
            ICollection<VfxCompatibilityIssue> issues,
            string code,
            string emitterName,
            string title,
            string detail,
            VfxCompatibilitySeverity severity)
            => issues.Add(new VfxCompatibilityIssue(code, emitterName, title, detail, severity));
    }
}
