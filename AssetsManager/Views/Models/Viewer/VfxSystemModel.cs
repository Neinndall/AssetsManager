using System.Collections.Generic;
using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
    public class VfxSystemModel
    {
        public string Name { get; set; } = string.Empty;
        public string ParticlePath { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public List<VfxEmitterModel> Emitters { get; set; } = new();
    }

    public class VfxEmitterModel
    {
        public string Name { get; set; } = string.Empty;
        public string TexturePath { get; set; } = string.Empty;
        public string MeshPath { get; set; } = string.Empty;
        public string AttachToBone { get; set; } = string.Empty;

        public float Lifetime { get; set; } = 1.0f;
        public float Delay { get; set; } = 0.0f;
        public float Duration { get; set; } = 2.0f;
        public float EmissionRate { get; set; } = 10.0f;

        public Vector3 InitialVelocity { get; set; } = Vector3.Zero;
        public Vector3 Acceleration { get; set; } = Vector3.Zero;
        public Vector3 InitialScale { get; set; } = Vector3.One;
        public Vector4 StartColor { get; set; } = Vector4.One;
        public Vector4 EndColor { get; set; } = Vector4.One;

        public int BlendMode { get; set; } = 0; // 0: AlphaBlend, 1: Additive, 2: Modulate
        public ushort NumFrames { get; set; } = 1;
        public bool IsLooping { get; set; } = true;
    }

    public class VfxParticleInstance
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 Scale;
        public Vector4 Color;
        public float Age;
        public float MaxLifetime;
        public float Rotation;
        public ushort FrameIndex;

        public bool IsAlive => Age < MaxLifetime;
    }
}
