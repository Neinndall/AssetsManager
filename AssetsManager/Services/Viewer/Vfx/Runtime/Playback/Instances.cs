using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Utils.Rendering;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    public sealed partial class VfxPlaybackRuntime
    {
        private void EnsureInstances(EmitterState state, int requestedCount)
        {
            int wanted = Math.Clamp(requestedCount, 0, state.Particles.Count);
            if (!state.InstancesDirty && state.PreparedInstanceCount >= wanted)
                return;
            BuildInstances(state, wanted);
        }

        private void BuildInstances(EmitterState s, int maxCount)
        {
            var d = s.Def;
            int n = Math.Min(s.Particles.Count, Math.Max(0, maxCount));
            int requiredLength = n * InstanceStride;
            if (s.InstanceBufferCapacity < requiredLength)
            {
                int currentParticleCapacity = Math.Max(4, s.InstanceBufferCapacity / InstanceStride);
                int nextParticleCapacity = currentParticleCapacity;
                while (nextParticleCapacity < n)
                {
                    int doubled = nextParticleCapacity * 2;
                    nextParticleCapacity = Math.Min(_particleCapacity, Math.Max(n, doubled));
                    if (nextParticleCapacity >= n) break;
                }
                s.ResetInstanceBuffer(nextParticleCapacity * InstanceStride);
            }
            var buf = s.RawInstances;
            float emitterT = EmitterTime(s);
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                var p = s.Particles[i];
                float t = ParticleAge01(p.Age, p.Life);
                float particleLingerT = 0f;
                bool lingering = p.LingerFrom >= 0f;
                if (lingering)
                {
                    float window = d.ParticleLingerType == 0
                        ? LingerSeconds(d)
                        : MathF.Max(0f, p.Life - p.LingerFrom);
                    particleLingerT = window > 0f
                        ? Math.Clamp((p.Age - p.LingerFrom) / window, 0f, 1f)
                        : 1f;
                }
                Vector3 scaleMul = ResolveScaleMultiplier(s, p);
                Vector4 col = lingering && d.Linger?.Color is { } lingerColor
                    ? p.BirthColor * lingerColor.SampleOver(particleLingerT, Vector4.One)
                    : p.BirthColor * LifeColor(d.ColorOverLife, t, p.Serial, s.RenderTime);
                col *= d.ModulationFactor ?? Vector4.One;
                // UNLIT_DECAL consumes MODULATE_COLOR directly, without billboard preprocessing.
                if (!d.DrawsAsProjection)
                    col = VfxColorSemantics.PremultiplyForAddOrSubtract(
                        col,
                        d.BlendMode,
                        d.DrawsAsDistortion,
                        d.HasResolvedCustomMaterial);

                // League keeps one logical flipbook counter for both texture layers. Each
                // sampler wraps that counter against its own texDiv grid at draw time, so do
                // not collapse it to the base grid here (texDivMult may be different).
                int authoredFrames = Math.Max(1, d.NumFrames);
                float playedFrame = PositiveModulo(p.StartFrame + (p.FrameRate > 0f ? p.Age * p.FrameRate : 0f), authoredFrames);
                float frame = MathF.Floor(d.StartFrame + playedFrame);

                Vector3 position = DrawnPosition(p, s);
                Vector3 orbitalAngles = p.BirthOrbitalVelocity * p.Age;
                Matrix4x4 orbitalTurn = OrbitalTurn(orbitalAngles);
                Vector3 drawnSize = ResolveDrawnSize(s, p);
                float sizeX = drawnSize.X * scaleMul.X;
                float sizeY = drawnSize.Y * scaleMul.Y;
                float sizeZ = drawnSize.Z * scaleMul.Z;
                if (d.PrimitiveKind == VfxPrimitiveKind.ArbitraryQuad)
                {
                    sizeX *= 2f;
                    sizeY *= 2f;
                    if (d.IsGroundLayer && d.IsUniformScale)
                        sizeY = sizeX;
                }
                Vector3 direction = p.Travel;

                float stretch = ResolveDirectionStretch(d, direction);
                if (stretch != 1f)
                {
                    if (d.PrimitiveKind == VfxPrimitiveKind.ArbitraryQuad) sizeX *= stretch;
                    else sizeY *= stretch;
                }
                buf[k++] = position.X; buf[k++] = position.Y; buf[k++] = position.Z;
                buf[k++] = sizeX;
                buf[k++] = sizeY;
                buf[k++] = col.X; buf[k++] = col.Y; buf[k++] = col.Z; buf[k++] = col.W;
                int rotationSlot = k++;
                buf[k++] = frame;
                buf[k++] = t;
                buf[k++] = direction.X; buf[k++] = direction.Y; buf[k++] = direction.Z;
                Vector3 currentRotation = p.BirthRotation;
                float legacyRollDegrees = 0f;
                float legacyRoll = legacyRollDegrees * (MathF.PI / 180f);
                if (d.IsSimpleEmitter)
                {
                    float spinDegrees = currentRotation.Z * (180f / MathF.PI) + legacyRollDegrees;
                    spinDegrees = ((MathF.Truncate(spinDegrees) % 360f) + 360f) % 360f;
                    buf[rotationSlot] = spinDegrees * (MathF.PI / 180f);
                }
                else
                {
                    buf[rotationSlot] = currentRotation.X;
                }
                bool authoredPlane = d.IsArbitraryQuad || d.PrimitiveKind is
                    VfxPrimitiveKind.ArbitraryTrail or VfxPrimitiveKind.PlanarProjection;
                if (d.IsDirectionOriented && !authoredPlane && direction.LengthSquared() > 0f)
                {
                    Vector3 dir = Vector3.Normalize(direction);
                    float yaw = MathF.Atan2(dir.X, dir.Z);
                    float pitch = MathF.Asin(Math.Clamp(-dir.Y, -1f, 1f));
                    buf[k++] = pitch + currentRotation.X;
                    buf[k++] = yaw + currentRotation.Y;
                    buf[k++] = currentRotation.Z;
                }
                else
                {
                    buf[k++] = currentRotation.X;
                    buf[k++] = currentRotation.Y;
                    buf[k++] = currentRotation.Z;
                }
                buf[k++] = sizeZ;
                VfxEmitterRenderState renderState = d.RenderState ?? VfxEmitterRenderState.Default;
                Vector2 uvRamp = VfxUvSemantics.BirthRamp(
                    p.BirthUvOffset + p.BirthUvScrollRate * p.Age,
                    renderState.ClampUvScroll);
                Vector2 uvOffset = VfxUvSemantics.Periodic(
                    uvRamp + IntegratedUv(d.ParticleUvScrollRate, p),
                    renderState.TextureAddressMode);
                Vector2 uvScale = d.UvScale?.SampleOver(t, Vector2.One) ?? Vector2.One;
                float uvRotationDegrees = (d.UvRotation?.Sample(t) ?? 0f) + p.BirthUvRotateRate * p.Age
                    + IntegratedUv(d.ParticleUvRotateRate, p);
                float uvRotation = uvRotationDegrees * (MathF.PI / 180f);
                buf[k++] = uvOffset.X; buf[k++] = uvOffset.Y;
                buf[k++] = uvScale.X; buf[k++] = uvScale.Y;
                buf[k++] = uvRotation;
                float erosionDrive = lingering && d.AlphaErosion?.LingerDrive is { } lingerErosion
                    ? lingerErosion.Sample(particleLingerT)
                    : d.AlphaErosion?.Drive.Sample(t) ?? 1f;
                buf[k++] = erosionDrive;
                Vector4 erosionMixer = d.AlphaErosion?.ChannelMixer?.Sample(0f) ?? new Vector4(0f, 0f, 0f, 1f);
                buf[k++] = erosionMixer.X; buf[k++] = erosionMixer.Y;
                buf[k++] = erosionMixer.Z; buf[k++] = erosionMixer.W;
                Vector2 textureMultRamp = VfxUvSemantics.BirthRamp(
                    p.TextureMultBirthUvOffset + p.TextureMultBirthUvScrollRate * p.Age,
                    d.TextureMultClampUvScroll);
                Vector2 textureMultUvOffset = VfxUvSemantics.Periodic(
                    textureMultRamp + IntegratedUv(d.TextureMultParticleUvScroll, p),
                    d.TextureMultAddressMode);
                Vector2 textureMultUvScale = d.TextureMultUvScale?.SampleOver(t, Vector2.One) ?? Vector2.One;

                float textureMultUvRotationDegrees = (d.TextureMultUvRotation?.Sample(t) ?? 0f)
                    + p.TextureMultBirthUvRotateRate * p.Age
                    + IntegratedUv(d.TextureMultParticleUvRotate, p);
                buf[k++] = textureMultUvOffset.X; buf[k++] = textureMultUvOffset.Y;
                buf[k++] = textureMultUvScale.X; buf[k++] = textureMultUvScale.Y;
                buf[k++] = textureMultUvRotationDegrees * (MathF.PI / 180f);
                // Birth random drives ColorLookUpType=3 exactly like LTK's pool.roll.
                buf[k++] = p.RangeRandom;
                // Palette selection is an emitter uniform sampled at t=0; keep this final lane
                // as padding so basis attributes remain at offsets 36/39/42.
                buf[k++] = 0f;

                Matrix4x4 basis = ParticleBasis(p, s, direction, orbitalTurn, legacyRoll);
                Vector3 basisX = VectorMathUtils.NormalizeOr(Vector3.TransformNormal(Vector3.UnitX, basis), Vector3.UnitX);
                Vector3 basisY = VectorMathUtils.NormalizeOr(Vector3.TransformNormal(Vector3.UnitY, basis), Vector3.UnitY);
                Vector3 basisZ = VectorMathUtils.NormalizeOr(Vector3.TransformNormal(Vector3.UnitZ, basis), Vector3.UnitZ);
                buf[k++] = basisX.X; buf[k++] = basisX.Y; buf[k++] = basisX.Z;
                buf[k++] = basisY.X; buf[k++] = basisY.Y; buf[k++] = basisY.Z;
                buf[k++] = basisZ.X; buf[k++] = basisZ.Y; buf[k++] = basisZ.Z;
            }
            s.MarkInstancesPrepared(k / InstanceStride);
        }

        private static float ParticleAge01(float age, float lifetime)
            => lifetime > 0f ? Math.Clamp(age / lifetime, 0f, 1f) : 1f;

        private static float PositiveModulo(float value, float span)
        {
            if (span <= 0f) return 0f;
            float wrapped = value % span;
            return wrapped < 0f ? wrapped + span : wrapped;
        }
    }
}
