using System;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    public sealed partial class VfxPlaybackRuntime
    {
        private static Vector3 ResolveScaleMultiplier(EmitterState state, in Particle particle)
        {
            VfxEmitterDefinition definition = state.Def;
            Vector3 multiplier;
            if (particle.LingerFrom >= 0f && definition.Linger?.Scale is { } lingerScale)
            {
                float window = definition.ParticleLingerType == 0
                    ? LingerSeconds(definition)
                    : MathF.Max(0f, particle.Life - particle.LingerFrom);
                float age = window > 0f
                    ? Math.Clamp((particle.Age - particle.LingerFrom) / window, 0f, 1f)
                    : 1f;
                multiplier = lingerScale.SampleOver(age, Vector3.One);
            }
            else
                multiplier = LifeVector(definition.ScaleOverLife, ParticleAge01(particle.Age, particle.Life),
                    particle.Serial, state.RenderTime, Vector3.One);
            return UsesUniformScale(definition) ? new Vector3(multiplier.X) : multiplier;
        }

        private static bool UsesUniformScale(VfxEmitterDefinition definition)
            => definition.IsUniformScale && definition.PrimitiveKind is VfxPrimitiveKind.CameraQuad or
                VfxPrimitiveKind.CameraUnitQuad or VfxPrimitiveKind.ArbitraryQuad or
                VfxPrimitiveKind.Mesh or VfxPrimitiveKind.AttachedMesh;

        private static float ResolveDirectionStretch(VfxEmitterDefinition definition, Vector3 travel)
        {
            // Only complex camera and arbitrary quads take velocity stretch.
            if (definition.DirectionVelocityScale == 0f || definition.IsSimpleEmitter || definition.PrimitiveKind == VfxPrimitiveKind.Ray ||
                !definition.DrawsAsQuad)
                return 1f;
            return MathF.Max(definition.DirectionVelocityMinScale, travel.Length() * definition.DirectionVelocityScale);
        }

        private static Vector3 ResolveMeshScale(EmitterState state, in Particle particle)
        {
            VfxEmitterDefinition definition = state.Def;
            Vector3 scale = ResolveDrawnSize(state, particle) * ResolveScaleMultiplier(state, particle);
            // Bone positions follow the drawn mesh; children retain their own authored dimensions.
            scale = new Vector3(float.IsFinite(scale.X) ? scale.X : 1f,
                float.IsFinite(scale.Y) ? scale.Y : 1f, float.IsFinite(scale.Z) ? scale.Z : 1f);
            if (definition.PrimitiveKind == VfxPrimitiveKind.AttachedMesh &&
                float.IsFinite(state.MeshOwnerScale) && state.MeshOwnerScale > 0f)
                scale *= state.MeshOwnerScale;
            return scale;
        }

        private static Vector3 ResolveDrawnSize(EmitterState state, in Particle particle)
        {
            var definition = state.Def;
            if (definition.PrimitiveKind is not (VfxPrimitiveKind.Mesh or VfxPrimitiveKind.ArbitraryQuad or VfxPrimitiveKind.PlanarProjection))
                return particle.BirthSize;
            Vector3 birthFrameScale = ExtractScale(state.DefinitionTransform * particle.BirthFrame);
            Vector3 localSize = particle.BirthSize / birthFrameScale;
            if (definition.IsMeshPrimitive && (definition.MeshAlignYawToCamera || definition.MeshAlignPitchToCamera))
                return localSize;
            Matrix4x4 basis = StandingBasis(particle, state, OrbitalTurn(particle.BirthOrbitalVelocity * particle.Age), 0f, normalize: false);
            return localSize * ExtractScale(basis);
        }
    }
}
