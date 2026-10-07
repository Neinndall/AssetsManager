using System;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Utils.Rendering;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    public sealed partial class VfxPlaybackRuntime
    {

        private static Vector3 ExtractScale(Matrix4x4 transform)
            => new(
                MathF.Max(1e-6f, Vector3.TransformNormal(Vector3.UnitX, transform).Length()),
                MathF.Max(1e-6f, Vector3.TransformNormal(Vector3.UnitY, transform).Length()),
                MathF.Max(1e-6f, Vector3.TransformNormal(Vector3.UnitZ, transform).Length()));


        private static Matrix4x4 OrbitalTurn(Vector3 radians)
        {
            if (radians.X == 0f && radians.Y == 0f && radians.Z == 0f)
                return Matrix4x4.Identity;

            return Matrix4x4.CreateRotationZ(radians.Z) *
                   Matrix4x4.CreateRotationX(radians.X) *
                   Matrix4x4.CreateRotationY(radians.Y);
        }

        private static Matrix4x4 ParticleBasis(
            in Particle particle,
            EmitterState emitter,
            Vector3 direction,
            Matrix4x4 orbitalTurn,
            float legacyRoll)
        {
            if (emitter.Def.IsDirectionOriented &&
                emitter.Def.PrimitiveKind is VfxPrimitiveKind.CameraQuad or VfxPrimitiveKind.CameraUnitQuad &&
                direction.LengthSquared() > 0f)
            {
                Vector3 up = Vector3.Normalize(direction);
                Vector3 axis = MathF.Abs(up.Y) < 0.99999f ? Vector3.UnitY : Vector3.UnitX;
                Vector3 right = VectorMathUtils.NormalizeOr(Vector3.Cross(axis, up), Vector3.UnitX);
                Vector3 forward = VectorMathUtils.NormalizeOr(Vector3.Cross(right, up), Vector3.UnitZ);
                return new Matrix4x4(
                    right.X, right.Y, right.Z, 0f,
                    up.X, up.Y, up.Z, 0f,
                    forward.X, forward.Y, forward.Z, 0f,
                    0f, 0f, 0f, 1f);
            }

            return StandingBasis(particle, emitter, orbitalTurn, legacyRoll);
        }

        /// <summary>
        /// The particle's own standing frame for geometry paths that deliberately ignore
        /// isDirectionOriented (arbitrary trails and arbitrary beams). It keeps the birth/current
        /// orientation choice, the particle's authored turn, and its orbital turn together.
        /// </summary>
        internal static Matrix4x4 ResolveStandingBasisForRender(EmitterState emitter, in Particle particle)
        {
            ArgumentNullException.ThrowIfNull(emitter);
            float legacyRoll = 0f;
            Matrix4x4 orbitalTurn = OrbitalTurn(particle.BirthOrbitalVelocity * particle.Age);
            return StandingBasis(particle, emitter, orbitalTurn, legacyRoll);
        }

        private static Matrix4x4 StandingBasis(
            in Particle particle,
            EmitterState emitter,
            Matrix4x4 orbitalTurn,
            float legacyRoll,
            bool normalize = true)
        {
            Vector3 rotation = particle.BirthRotation;
            Matrix4x4 standing =
                Matrix4x4.CreateRotationZ(rotation.Z + legacyRoll) *
                Matrix4x4.CreateRotationX(rotation.X) *
                Matrix4x4.CreateRotationY(rotation.Y);

            if (emitter.Def.PostRotateOrientation is { } post)
            {
                post *= MathF.PI / 180f;
                standing *= OrbitalTurn(post);
            }

            Matrix4x4 frame;
            if (emitter.Def.ParticleIsLocalOrientation)
            {
                standing *= emitter.SystemOrientation;
                frame = Matrix4x4.Identity;
            }
            else
            {
                frame = particle.BirthFrame;
            }

            Matrix4x4 basis = standing * orbitalTurn;
            Matrix4x4 definition = emitter.DefinitionTransform;
            definition.Translation = Vector3.Zero;
            basis *= definition;
            if (emitter.Def.IsDirectionOriented && emitter.Def.PrimitiveKind is
                VfxPrimitiveKind.ArbitraryQuad or VfxPrimitiveKind.Mesh or VfxPrimitiveKind.AttachedMesh &&
                particle.Drift.LengthSquared() > 0f)
            {
                Vector3 forward = Vector3.Normalize(particle.Drift);
                Vector3 axis = MathF.Abs(forward.Y) < 0.99999f ? Vector3.UnitY : Vector3.UnitX;
                Vector3 right = Vector3.Normalize(Vector3.Cross(axis, forward));
                Vector3 up = Vector3.Cross(forward, right);
                basis *= new Matrix4x4(right.X, right.Y, right.Z, 0f,
                    up.X, up.Y, up.Z, 0f, forward.X, forward.Y, forward.Z, 0f, 0f, 0f, 0f, 1f);
            }
            basis *= frame;
            return normalize ? VectorMathUtils.OrientationOnly(basis) : basis;
        }
    }
}
