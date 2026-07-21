using System;
using System.Collections.Generic;
using System.Numerics;

namespace AssetsManager.Services.Viewer.Vfx
{
    /// <summary>
    /// Maintains deterministic, graphics-independent playback state for one placed effect graph.
    /// </summary>
    public sealed class VfxPlaybackRuntime
    {
        /// <summary>Per-emitter live state + drawable output. One batch renders with one texture/blend.</summary>
        public sealed class EmitterState
        {
            public required VfxEmitterDefinition Def { get; init; }
            public int SourceOrder { get; init; }
            public Vector3 BasePos;                 // world spawn origin (placement + emitterPosition)
            public Vector3 PlacementRight, PlacementUp, PlacementForward;
            public uint Texture;                    // GL handle for this emitter's sprite (0 = not uploaded/skip)
            public int TextureWidth, TextureHeight;
            public uint TextureMult;                // optional Riot multiplier/noise texture stage
            public uint DistortionTexture;          // normal map for screen-space heat haze/refraction
            public object PendingTexture;
            public object PendingTextureMult;
            public object PendingDistortionTexture;
            /// <summary>Pending mesh data (positions, uvs, indices) for deferred GL upload of .scb/.sco mesh primitives.</summary>
            public (float[] Positions, float[] Uvs, uint[] Indices)? PendingMesh;
            // CPU copy of particleColorTexture (RGBA8, top-left origin).
            public byte[] ColorGradient;
            public int ColorGradientW, ColorGradientH;
            public float SpriteAspect = 1f;         // legacy scalar quads preserve one atlas cell's width/height
            internal float SpawnAccum;
            internal float Age;                     // emitter age (seconds)
            internal bool BurstDone;                // for isSingleParticle
            internal readonly List<Particle> Particles = new();

            /// <summary>Packed instance data for the renderer: position, 3D scale, color,
            /// rotation/frame, age/velocity, and Euler rotation.</summary>
            public float[] Instances = System.Array.Empty<float>();
            public int InstanceCount;

            // Mesh-primitive emitters (0 = billboard)
            public uint MeshVao, MeshVbo, MeshEbo;
            public int MeshVertexCount, MeshIndexCount;
            public float[] MeshInterleaved;
            /// <summary>Emitter age in seconds — drives UV scroll + wing-flap animation time.</summary>
            public float EmitterAge => Age;
        }

        internal struct Particle
        {
            public Vector3 Pos, Vel, BirthAccel, BirthOrbitalVelocity, BirthDrag;
            public float Age, Life;
            public Vector3 BirthSize;
            public Vector4 BirthColor;
            public Vector3 BirthRotation;
            public float Rot, RotVel;
            public float StartFrame, FrameRate;
            public float ColorRandom;   // stable per-particle 0..1 roll for the colour-gradient variant axis
        }

        public IReadOnlyList<EmitterState> Emitters => _emitters;
        private readonly List<EmitterState> _emitters = new();
        private readonly Random _rng;
        private Matrix4x4 _worldTransform = Matrix4x4.Identity;
        private Matrix4x4 _inverseWorldTransform = Matrix4x4.Identity;
        public int LiveParticleCount { get; private set; }
        public object UserTag { get; set; }
        private const int MaxParticlesPerEmitter = 4000;

        public void SetTransform(Matrix4x4 worldTransform)
        {
            _worldTransform = worldTransform;
            if (!Matrix4x4.Invert(worldTransform, out _inverseWorldTransform))
                _inverseWorldTransform = Matrix4x4.Identity;

            foreach (var es in _emitters)
            {
                es.BasePos = Vector3.Transform(es.Def.EmitterPosition, worldTransform);
                es.PlacementRight = SafeNormal(Vector3.TransformNormal(Vector3.UnitX, worldTransform), Vector3.UnitX);
                es.PlacementUp = SafeNormal(Vector3.TransformNormal(Vector3.UnitY, worldTransform), Vector3.UnitY);
                es.PlacementForward = SafeNormal(Vector3.TransformNormal(Vector3.UnitZ, worldTransform), Vector3.UnitZ);
            }
        }

        public VfxPlaybackRuntime(int seed = 1234) => _rng = new Random(seed);

        /// <summary>Configure from a system placed at worldPos. Only visual emitters are simulated.</summary>
        public void SetSystem(VfxSystemDefinition system, Vector3 worldPos, bool includeNonVisual = false)
            => SetSystem(system, Matrix4x4.CreateTranslation(worldPos), includeNonVisual);

        /// <summary>Configure a system with its complete authored placement transform.</summary>
        public void SetSystem(VfxSystemDefinition system, Matrix4x4 worldTransform, bool includeNonVisual = false)
        {
            _emitters.Clear();
            _worldTransform = worldTransform;
            if (!Matrix4x4.Invert(worldTransform, out _inverseWorldTransform))
                _inverseWorldTransform = Matrix4x4.Identity;
            for (int emitterIndex = 0; emitterIndex < system.Emitters.Count; emitterIndex++)
            {
                var e = system.Emitters[emitterIndex];
                if (!includeNonVisual && !e.IsVisual) continue;
                _emitters.Add(new EmitterState
                {
                    Def = e,
                    SourceOrder = emitterIndex,
                    BasePos = Vector3.Transform(e.EmitterPosition, worldTransform),
                    PlacementRight = SafeNormal(Vector3.TransformNormal(Vector3.UnitX, worldTransform), Vector3.UnitX),
                    PlacementUp = SafeNormal(Vector3.TransformNormal(Vector3.UnitY, worldTransform), Vector3.UnitY),
                    PlacementForward = SafeNormal(Vector3.TransformNormal(Vector3.UnitZ, worldTransform), Vector3.UnitZ),
                });
            }
            Reset();
        }

        public void ApplyRenderOrder()
        {
            _emitters.Sort((left, right) =>
            {
                int leftPass = (left.Def.RenderState ?? VfxEmitterRenderState.Default).RenderPass;
                int rightPass = (right.Def.RenderState ?? VfxEmitterRenderState.Default).RenderPass;
                int passOrder = leftPass.CompareTo(rightPass);
                return passOrder != 0 ? passOrder : left.SourceOrder.CompareTo(right.SourceOrder);
            });
        }

        private static Vector3 SafeNormal(Vector3 value, Vector3 fallback)
            => value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : fallback;

        /// <summary>Move the whole system WITHOUT resetting live particles.</summary>
        public void SetWorldTransform(Matrix4x4 worldTransform)
        {
            _worldTransform = worldTransform;
            if (!Matrix4x4.Invert(worldTransform, out _inverseWorldTransform))
                _inverseWorldTransform = Matrix4x4.Identity;
            foreach (var s in _emitters)
            {
                s.BasePos = Vector3.Transform(s.Def.EmitterPosition, worldTransform);
                s.PlacementRight = SafeNormal(Vector3.TransformNormal(Vector3.UnitX, worldTransform), Vector3.UnitX);
                s.PlacementUp = SafeNormal(Vector3.TransformNormal(Vector3.UnitY, worldTransform), Vector3.UnitY);
                s.PlacementForward = SafeNormal(Vector3.TransformNormal(Vector3.UnitZ, worldTransform), Vector3.UnitZ);
            }
        }

        public void Reset()
        {
            foreach (var s in _emitters) { s.Particles.Clear(); s.SpawnAccum = 0; s.Age = 0; s.BurstDone = false; s.InstanceCount = 0; }
            LiveParticleCount = 0;
        }

        private float _startDelay;
        public void SetStartDelay(float seconds) => _startDelay = MathF.Max(0f, seconds);

        public void Update(float dt)
        {
            if (dt <= 0f) return;
            if (_startDelay > 0f)
            {
                _startDelay -= dt;
                if (_startDelay > 0f) return;
                dt = MathF.Min(dt, -_startDelay);   // only the portion past the trigger point
                _startDelay = 0f;
                if (dt <= 0f) return;
            }
            dt = MathF.Min(dt, 0.1f);   // clamp big frame gaps so bursts don't teleport
            int live = 0;
            foreach (var s in _emitters)
            {
                UpdateEmitter(s, dt);
                BuildInstances(s);
                live += s.InstanceCount;
            }
            LiveParticleCount = live;
        }

        private void UpdateEmitter(EmitterState s, float dt)
        {
            var d = s.Def;
            s.Age += dt;

            // spawn
            bool emitting = s.Age >= d.TimeBeforeFirstEmission
                            && (d.EmitterLifetime is not { } life || s.Age <= d.TimeBeforeFirstEmission + life);
            if (emitting)
            {
                if (d.IsSingleParticle)
                {
                    if (!s.BurstDone) { Spawn(s); s.BurstDone = true; }
                }
                else
                {
                    float emitterT = d.EmitterLifetime is > 0f
                        ? Math.Clamp((s.Age - d.TimeBeforeFirstEmission) / d.EmitterLifetime.Value, 0f, 1f)
                        : 0f;
                    float rate = MathF.Max(0f, d.Rate.Sample(emitterT));
                    s.SpawnAccum += rate * dt;
                    while (s.SpawnAccum >= 1f && s.Particles.Count < MaxParticlesPerEmitter)
                    {
                        Spawn(s);
                        s.SpawnAccum -= 1f;
                    }
                    if (s.Particles.Count >= MaxParticlesPerEmitter) s.SpawnAccum = 0f;
                }
            }

            // integrate + cull
            for (int i = s.Particles.Count - 1; i >= 0; i--)
            {
                var p = s.Particles[i];
                p.Age += dt;
                if (p.Age >= p.Life) { s.Particles.RemoveAt(i); continue; }
                float particleT = float.IsPositiveInfinity(p.Life) ? 0f : Math.Clamp(p.Age / p.Life, 0f, 1f);
                var worldAccel = d.Acceleration?.Sample(particleT) ?? Vector3.Zero;
                worldAccel = Vector3.TransformNormal(worldAccel, _worldTransform);
                p.Vel += (p.BirthAccel + worldAccel) * dt;
                var dragOverLife = d.DragOverLife?.Sample(particleT) ?? Vector3.Zero;
                var drag = Vector3.Max(Vector3.Zero, p.BirthDrag + dragOverLife);
                p.Vel *= new Vector3(MathF.Exp(-drag.X * dt), MathF.Exp(-drag.Y * dt), MathF.Exp(-drag.Z * dt));
                p.Pos += p.Vel * dt;
                if (p.BirthOrbitalVelocity.LengthSquared() > 1e-8f)
                {
                    var localRelative = Vector3.TransformNormal(p.Pos - s.BasePos, _inverseWorldTransform);
                    var angularStep = p.BirthOrbitalVelocity * dt;
                    var orbit = Quaternion.CreateFromYawPitchRoll(angularStep.Y, angularStep.X, angularStep.Z);
                    p.Pos = s.BasePos + Vector3.TransformNormal(Vector3.Transform(localRelative, orbit), _worldTransform);
                }
                p.Rot += p.RotVel * dt;
                s.Particles[i] = p;
            }

            if (d.IsSingleParticle && s.BurstDone && s.Particles.Count == 0 && d.EmitterLifetime is null)
                s.BurstDone = false;
        }

        private void Spawn(EmitterState s)
        {
            var d = s.Def;
            float sampledLife = d.ParticleLifetime.SampleBirth(_rng);
            float life = sampledLife < 0f ? float.PositiveInfinity : MathF.Max(0.05f, sampledLife);
            var birthScale = d.BirthScale.SampleBirth(_rng);
            var vel = d.BirthVelocity?.SampleBirth(_rng) ?? Vector3.Zero;
            var birthAccel = d.BirthAcceleration?.SampleBirth(_rng) ?? Vector3.Zero;
            var birthOrbitalVelocity = d.BirthOrbitalVelocity?.SampleBirth(_rng) ?? Vector3.Zero;
            var birthDrag = d.BirthDrag?.SampleBirth(_rng) ?? Vector3.Zero;
            var birthRotation = d.BirthRotation?.SampleBirth(_rng) ?? Vector3.Zero;
            var rotVel = d.BirthRotationalVelocity?.SampleBirth(_rng) ?? Vector3.Zero;

            var localOffset = d.SpawnShape?.SampleOffset(_rng) ?? Vector3.Zero;
            var worldOffset = Vector3.TransformNormal(localOffset, _worldTransform);
            vel = Vector3.TransformNormal(vel, _worldTransform);
            birthAccel = Vector3.TransformNormal(birthAccel, _worldTransform);

            s.Particles.Add(new Particle
            {
                Pos = s.BasePos + worldOffset,
                Vel = vel,
                BirthAccel = birthAccel,
                BirthOrbitalVelocity = birthOrbitalVelocity,
                BirthDrag = birthDrag,
                Age = 0f,
                Life = life,
                BirthSize = new Vector3(
                    birthScale.X,
                    birthScale.Y == 0 ? birthScale.X : birthScale.Y,
                    birthScale.Z == 0 ? birthScale.X : birthScale.Z),
                BirthColor = d.BirthColor.SampleBirth(_rng),
                BirthRotation = birthRotation * (MathF.PI / 180f),
                Rot = d.IsMeshPrimitive ? 0f : birthRotation.X * (MathF.PI / 180f),
                RotVel = rotVel.X * (MathF.PI / 180f),
                StartFrame = d.RandomStartFrame && d.NumFrames > 1
                    ? _rng.Next(d.NumFrames)
                    : Math.Clamp(d.StartFrame, 0f, Math.Max(0, d.NumFrames - 1)),
                FrameRate = d.BirthFrameRate?.SampleBirth(_rng) ?? d.FrameRate ?? 0f,
                ColorRandom = (float)_rng.NextDouble(),
            });
        }

        private void BuildInstances(EmitterState s)
        {
            var d = s.Def;
            int n = s.Particles.Count;
            if (s.Instances.Length < n * 19) s.Instances = new float[Math.Max(n * 19, 76)];
            var buf = s.Instances;
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                var p = s.Particles[i];
                float t = float.IsPositiveInfinity(p.Life) ? 0f : Math.Clamp(p.Age / p.Life, 0f, 1f);
                var scaleMul = d.ScaleOverLife?.Sample(t) ?? Vector3.One;
                var colMul = d.ColorOverLife?.Sample(t) ?? Vector4.One;
                var col = p.BirthColor * colMul;

                if (s.ColorGradient is { } grad && s.ColorGradientW > 0 && s.ColorGradientH > 0)
                {
                    float speed = p.Vel.Length();
                    float u = LookupCoord(d.ColorLookUpTypeX ?? 0, t, speed, p.ColorRandom);
                    float v = d.ColorLookUpTypeY is { } ty ? LookupCoord(ty, t, speed, p.ColorRandom) : 0.5f;
                    col *= SampleGradient(grad, s.ColorGradientW, s.ColorGradientH, u, v);
                }

                float frame = 0f;
                if (d.NumFrames > 1)
                    frame = MathF.Floor(p.FrameRate > 0f
                        ? (p.StartFrame + p.Age * p.FrameRate) % d.NumFrames
                        : (p.StartFrame + t * d.NumFrames) % d.NumFrames);

                buf[k++] = p.Pos.X; buf[k++] = p.Pos.Y; buf[k++] = p.Pos.Z;
                float sizeX = p.BirthSize.X * scaleMul.X;
                if (d.UseTextureAspect) sizeX *= s.SpriteAspect;
                buf[k++] = sizeX;
                buf[k++] = p.BirthSize.Y * scaleMul.Y;
                buf[k++] = col.X; buf[k++] = col.Y; buf[k++] = col.Z; buf[k++] = col.W;
                buf[k++] = p.Rot;
                buf[k++] = frame;
                buf[k++] = p.Age;
                buf[k++] = p.Vel.X; buf[k++] = p.Vel.Y; buf[k++] = p.Vel.Z;
                buf[k++] = p.Rot; buf[k++] = p.BirthRotation.Y; buf[k++] = p.BirthRotation.Z;
                buf[k++] = p.BirthSize.Z * scaleMul.Z;
            }
            s.InstanceCount = n;
        }

        private static float LookupCoord(int type, float age, float speed, float random) => type switch
        {
            1 => Math.Clamp(speed / 400f, 0f, 1f),
            2 or 3 => random,
            _ => age,
        };

        private static Vector4 SampleGradient(byte[] rgba, int w, int h, float u, float v)
        {
            u = Math.Clamp(u, 0f, 1f);
            v = Math.Clamp(v, 0f, 1f);
            float fx = u * (w - 1), fy = v * (h - 1);
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
            float tx = fx - x0, tyf = fy - y0;
            Vector4 c00 = Texel(rgba, w, x0, y0), c10 = Texel(rgba, w, x1, y0);
            Vector4 c01 = Texel(rgba, w, x0, y1), c11 = Texel(rgba, w, x1, y1);
            return Vector4.Lerp(Vector4.Lerp(c00, c10, tx), Vector4.Lerp(c01, c11, tx), tyf);
        }

        private static Vector4 Texel(byte[] rgba, int w, int x, int y)
        {
            int i = (y * w + x) * 4;
            return new Vector4(rgba[i] / 255f, rgba[i + 1] / 255f, rgba[i + 2] / 255f, rgba[i + 3] / 255f);
        }
    }
}
