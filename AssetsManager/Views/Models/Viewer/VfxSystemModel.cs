using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using AssetsManager.Services.Viewer.Vfx;

namespace AssetsManager.Views.Models.Viewer
{
    public class VfxSystemModel : INotifyPropertyChanged
    {
        public string Name { get; set; } = string.Empty;
        public string ParticlePath { get; set; } = string.Empty;
        public List<VfxEmitterModel> Emitters { get; set; } = new();
        public VfxSystemDefinition Definition { get; set; }
        public IReadOnlyDictionary<uint, VfxSystemDefinition> SystemCatalog { get; set; }
            = new Dictionary<uint, VfxSystemDefinition>();
        public IReadOnlyDictionary<uint, uint> ResourceMap { get; set; }
            = new Dictionary<uint, uint>();
        public string SearchDirectory { get; set; } = string.Empty;
        public int EmitterCount => Definition?.Emitters.Count ?? Emitters.Count;
        public double TotalDuration { get; set; }
        public bool HasFiniteDuration => !double.IsInfinity(TotalDuration) && TotalDuration > 0;
        public double TimelineMaximum => HasFiniteDuration ? TotalDuration : 1.0;
        public string DurationText => HasFiniteDuration ? $"{TotalDuration:F1}s" : "LOOP";
        public string FrameRateText { get; set; } = "Realtime";

        private bool _isPlaying;
        private double _speed = 1.0;
        private double _currentTime;

        public bool IsPlaying
        {
            get => _isPlaying;
            set => SetField(ref _isPlaying, value);
        }

        public double Speed
        {
            get => _speed;
            set => SetField(ref _speed, value);
        }

        public double CurrentTime
        {
            get => _currentTime;
            set => SetField(ref _currentTime, Math.Max(0, value));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
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
