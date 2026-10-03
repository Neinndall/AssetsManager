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

        private static Vector3 ResolveMeshScale(EmitterState state, in Particle particle)
        {
            VfxEmitterDefinition definition = state.Def;
            Vector3 scale = particle.BirthSize * ResolveScaleMultiplier(state, particle);
            if (definition.PrimitiveKind == VfxPrimitiveKind.Mesh && definition.IsDirectionOriented &&
                definition.AuthoredFeatures?.HasLegacySimple != true && particle.Travel.LengthSquared() > 0f)
                scale.Z *= MathF.Max(definition.DirectionVelocityMinScale, particle.Travel.Length() * definition.DirectionVelocityScale);
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
