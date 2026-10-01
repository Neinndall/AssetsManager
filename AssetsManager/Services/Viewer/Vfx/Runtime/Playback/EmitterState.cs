using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Runtime
{
    public sealed partial class VfxPlaybackRuntime
    {
        /// <summary>Per-emitter live state + drawable output. One batch renders with one texture/blend.</summary>
        public sealed class EmitterState
        {
            public required VfxEmitterDefinition Def { get; set; }
            public int SourceOrder { get; init; }
            /// <summary>
            /// Render identity of the authored emitter path. LTK groups every live source of the
            /// same graph/path/emitter into one draw component (not one draw component per runtime).
            /// </summary>
            internal object RenderGraphKey { get; set; }
            internal string RenderPath { get; set; } = string.Empty;
            /// <summary>The opened/root emitter this definition descends from, matching LTK DrawnEmitter.root.</summary>
            internal int RenderRootSourceOrder { get; set; }
            /// <summary>Stable definition-tree rank used by the renderer across all live sources of this path.</summary>
            internal int RenderRank { get; set; }
            public bool IsVisible { get; set; } = true;
            public Vector3 BasePos;                 // world spawn origin (placement + emitterPosition)
            /// <summary>The spawn origin where the previous step ended, which a step's births spread from.</summary>
            internal Vector3? StepStartBasePos;
            internal Vector3 FieldBasePos;          // emitterPosition under the frame basis, excluding translationOverride
            public Vector3 SystemOrigin, SystemTarget;
            /// <summary>The system's world turn without scale, which turns a beam's local-space end offsets.</summary>
            internal Matrix4x4 SystemOrientation = Matrix4x4.Identity;
            public Vector3 PlacementRight, PlacementUp, PlacementForward;
            internal Matrix4x4 PlacementTransform;
            public uint Texture;                    // GL handle for this emitter's sprite (0 = not uploaded/skip)
            public int TextureWidth, TextureHeight;
            public uint TextureMult;                // optional Riot multiplier/noise texture stage
            public int TextureMultWidth, TextureMultHeight;
            public uint DistortionTexture;          // normal map for screen-space heat haze/refraction
            public uint ErosionTexture;
            public uint ReflectionTexture;
            public uint PaletteTexture;
            public readonly Dictionary<string, uint> ProgramTextures = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, object> PendingProgramTextures = new(StringComparer.OrdinalIgnoreCase);
            public object PendingTexture;
            public object PendingTextureMult;
            public object PendingDistortionTexture;
            public object PendingErosionTexture;
            public object PendingReflectionTexture;
            public object PendingPaletteTexture;
            /// <summary>Pending mesh data for deferred GL upload of authored VFX mesh primitives.</summary>
            public VfxMeshData? PendingMesh;
            /// <summary>GPU handle for particleColorTexture (0 = unavailable).</summary>
            public uint ColorGradientTexture;
            public object PendingColorGradient;
            internal float SharedRandom;
            internal bool SharedRandomRolled;
            internal float EmittedThrough;
            internal float Age;                     // emitter age (seconds)
            internal float FinishedAt = -1f;
            internal bool BurstDone;                // for isSingleParticle
            internal bool InitialEmissionDone;
            internal readonly List<Particle> Particles = new();

            private VfxPlaybackRuntime _owner;
            private float[] _instances = System.Array.Empty<float>();
            private int _manualInstanceCount;
            private int _preparedInstanceCount;
            private bool _instancesDirty = true;

            /// <summary>
            /// Packed draw data. Runtime-owned emitters build this lazily so simulation does not pay
            /// render preparation for particles the renderer will never consume. Directly-constructed
            /// diagnostic/test states keep the legacy assignable buffer contract.
            /// </summary>
            public float[] Instances
            {
                get
                {
                    _owner?.EnsureInstances(this, Particles.Count);
                    return _instances;
                }
                set
                {
                    _instances = value ?? System.Array.Empty<float>();
                    _preparedInstanceCount = _instances.Length / InstanceStride;
                    _instancesDirty = false;
                }
            }

            /// <summary>Live particle rows for this emitter, independent of the prepared draw buffer.</summary>
            public int InstanceCount
            {
                get => _owner != null ? Particles.Count : _manualInstanceCount;
                set => _manualInstanceCount = Math.Max(0, value);
            }

            internal int PreparedInstanceCount => _owner != null ? _preparedInstanceCount : Math.Min(_manualInstanceCount, _preparedInstanceCount);
            internal int InstanceBufferCapacity => _instances.Length;

            internal void BindOwner(VfxPlaybackRuntime owner)
            {
                _owner = owner;
                _instancesDirty = true;
                _preparedInstanceCount = 0;
            }

            internal void InvalidateInstances()
            {
                if (_owner == null) return;
                _instancesDirty = true;
                _preparedInstanceCount = 0;
            }

            internal void ResetInstanceBuffer(int length)
            {
                _instances = length > 0 ? new float[length] : System.Array.Empty<float>();
                _instancesDirty = true;
                _preparedInstanceCount = 0;
            }

            internal ReadOnlySpan<float> PrepareInstances(int requestedCount)
            {
                int available = InstanceCount;
                int wanted = Math.Clamp(requestedCount, 0, available);
                if (_owner != null)
                    _owner.EnsureInstances(this, wanted);
                else
                    wanted = Math.Min(wanted, _preparedInstanceCount);
                return new ReadOnlySpan<float>(_instances, 0, wanted * InstanceStride);
            }

            internal float[] RawInstances => _instances;
            internal bool InstancesDirty => _instancesDirty;
            internal void MarkInstancesPrepared(int count)
            {
                _preparedInstanceCount = Math.Max(0, count);
                _instancesDirty = false;
            }

            internal float TrailDistance;
            internal Vector3? TrailSpawnedAt;
            internal float[] NoiseLast = Array.Empty<float>();
            internal int[] NoiseFired = Array.Empty<int>();
            internal PreparedNoiseField[] PreparedNoise = Array.Empty<PreparedNoiseField>();

            // Mesh-primitive emitters (0 = billboard)
            public uint MeshVao, MeshVbo, MeshEbo;
            public int MeshVertexCount, MeshIndexCount;
            public float[] MeshInterleaved;
            /// <summary>True when the uploaded mesh carries a valid four-weight skinning layout.</summary>
            public bool MeshHasSkinning;
            /// <summary>Owner skinScale, applied after skeleton skinning for AttachedMesh.</summary>
            public float MeshOwnerScale = 1f;
            /// <summary>Owner SKN draw groups retained so clip visibility can change AttachedMesh live.</summary>
            public VfxMeshRangeData[] MeshRanges = Array.Empty<VfxMeshRangeData>();
            /// <summary>Selected particle-mesh pose. Unlike AttachedMesh, it advances on each particle's age.</summary>
            internal VfxAnimatedMesh MeshAnimation;
            internal VfxAnimatedMesh MeshBaseAnimation;
            internal VfxAnimatedMesh[] MeshAnimationVariants = Array.Empty<VfxAnimatedMesh>();

            /// <summary>Emitter-local age in seconds; drives emitter-phase curves and mesh animation time.</summary>
            public float EmitterAge => Age;
            /// <summary>
            /// Draw clock for this source. Root emitters use their runtime clock; graph children are
            /// overwritten with the root driver's clock so emitterUvScrollRate matches LTK Source.time.
            /// </summary>
            public float RenderTime { get; internal set; }
        }

        internal struct Particle
        {
            public Vector3 Pos, Vel, Travel, BirthOrbitalVelocity, BirthDrag;
            public Vector3 AnalyticTerminal, AnalyticOffset;
            public Matrix4x4 BirthFrame;
            public Quaternion SpawnRotation;
            public float Age, Life;
            public uint Serial;
            public Vector3 TrailTiling;
            public float TrailBirthDistance;
            public Vector3 BirthSize;
            public Vector4 BirthColor;
            public Vector3 BirthRotation;
            public Vector3 RotationalVelocity, RotationalAcceleration;
            public float RangeRandom;
            public Vector2 BirthUvOffset, BirthUvScrollRate;
            public Vector2 IntegratedUvOffset;
            public Vector2 TextureMultBirthUvOffset, TextureMultBirthUvScrollRate;
            public Vector2 IntegratedTextureMultUvOffset;
            public float BirthUvRotateRate, IntegratedUvRotation;
            public float TextureMultBirthUvRotateRate, IntegratedTextureMultUvRotation;
            public float LingerFrom;
            public float Rot, RotVel;
            public float StartFrame, FrameRate;
        }
    }
}
