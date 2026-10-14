using System;
using System.Collections.Generic;
using System.Numerics;

namespace AssetsManager.Views.Models.Viewer;

/// <summary>Authored pose modifiers and attachment points, independent of GPU resources.</summary>
internal sealed record SkinPoseDefinition
{
    internal static readonly SkinPoseDefinition Empty = new();
    internal IReadOnlyList<SkinSpringDefinition> Springs { get; init; } = Array.Empty<SkinSpringDefinition>();
    internal IReadOnlyList<SkinSocketDefinition> Sockets { get; init; } = Array.Empty<SkinSocketDefinition>();
    internal IReadOnlyList<SkinConformDefinition> Conforms { get; init; } = Array.Empty<SkinConformDefinition>();
    internal IReadOnlyList<SkinOrientationDefinition> Orientations { get; init; } = Array.Empty<SkinOrientationDefinition>();
    internal IReadOnlyList<uint> UnsupportedClasses { get; init; } = Array.Empty<uint>();
    internal bool HasDynamics => Springs.Count + Conforms.Count + Orientations.Count > 0;
}

internal sealed record SkinSpringDefinition(uint Name, uint Joint, float Mass = 0.1f,
    float Stiffness = 2.5f, float Damping = 1f, bool DoTranslation = false, bool DoRotation = false,
    float MaxDistance = 0f, float MaxAngle = 0f, bool Invert = false, bool DefaultOn = true);

internal enum SkinSocketKind { Joint, World, Unsupported }

internal sealed record SkinSocketDefinition(string Name, SkinSocketKind Kind, uint Parent,
    Vector3 Position, Vector3 Rotation = default, Vector3 FreezePosition = default,
    Vector3 FreezeRotation = default);

internal sealed record SkinExtraConformChain(uint Start, uint End, float RightBias);

internal sealed record SkinConformDefinition(uint Start, uint End, uint Mask, float MaxAngle = 65f,
    float Damping = 10f, float Frequency = 10f, float VelocityMultiplier = -0.5f,
    bool OnlyInTurns = false, float ActivationAngle = 0.5f, float ActivationDistance = 200f,
    float BlendDistance = 400f)
{
    internal IReadOnlyList<SkinExtraConformChain> ExtraChains { get; init; } = Array.Empty<SkinExtraConformChain>();
}

internal sealed record SkinOrientationDefinition(IReadOnlyList<uint> Joints, uint SourceClass,
    Vector3? ConstantSource, byte OrientationType = 0, byte PlaneConstraint = 1,
    byte TiltAxis = 0, byte AimAxis = 3, bool AimNegated = true, bool Flipped = false,
    float MaxAngle = 180f, bool DefaultOn = true);
