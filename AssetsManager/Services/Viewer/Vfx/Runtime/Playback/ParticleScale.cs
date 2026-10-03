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
                multiplier = definition.ScaleOverLife?.SampleOver(ParticleAge01(particle.Age, particle.Life), Vector3.One) ?? Vector3.One;
            return definition.IsUniformScale ? new Vector3(multiplier.X) : multiplier;
        }

        private static float ResolveDirectionStretch(VfxEmitterDefinition definition, Vector3 travel)
        {
            // Ordinary meshes stretch local Z; quads stretch their long axis. Legacy
            // particles, rays and attachment meshes retain their authored dimensions.
            if (!definition.IsDirectionOriented || definition.PrimitiveKind == VfxPrimitiveKind.Ray ||
                definition.AuthoredFeatures?.HasLegacySimple == true ||
                (!definition.DrawsAsQuad && definition.PrimitiveKind != VfxPrimitiveKind.Mesh) ||
                !(travel.LengthSquared() > 0f))
                return 1f;
            return MathF.Max(definition.DirectionVelocityMinScale, travel.Length() * definition.DirectionVelocityScale);
        }

        private static Vector3 ResolveMeshScale(EmitterState state, in Particle particle)
        {
            VfxEmitterDefinition definition = state.Def;
            Vector3 scale = particle.BirthSize * ResolveScaleMultiplier(state, particle);
            if (definition.PrimitiveKind == VfxPrimitiveKind.Mesh)
                scale.Z *= ResolveDirectionStretch(definition, particle.Travel);
            // Bone positions follow the drawn mesh; children retain their own authored dimensions.
            scale = new Vector3(float.IsFinite(scale.X) ? scale.X : 1f,
                float.IsFinite(scale.Y) ? scale.Y : 1f, float.IsFinite(scale.Z) ? scale.Z : 1f);
            if (definition.PrimitiveKind == VfxPrimitiveKind.AttachedMesh &&
                float.IsFinite(state.MeshOwnerScale) && state.MeshOwnerScale > 0f)
                scale *= state.MeshOwnerScale;
            return scale;
        }
    }
}
