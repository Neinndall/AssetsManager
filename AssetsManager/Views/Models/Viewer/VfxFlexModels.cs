namespace AssetsManager.Views.Models.Viewer;

public enum VfxEffectsQuality { VeryLow, Low, Medium, High, VeryHigh }
public sealed record VfxFlexFloat(int Channel, VfxCurveF Value);
public sealed record VfxFlexVector2(int Channel, VfxCurve2 Value);
public sealed record VfxFlexVector3(int Channel, VfxCurve3 Value);
public sealed record VfxFlexScale(int Channel, float Value);
public sealed record VfxFlexDefinition(
    VfxFlexFloat Rate = null,
    VfxFlexFloat Lifetime = null,
    VfxFlexVector3 Velocity = null,
    VfxFlexVector3 RotationalVelocity = null,
    VfxFlexVector2 UvScroll = null,
    VfxFlexVector2 UvScrollMult = null,
    VfxFlexVector2 UvOffset = null,
    VfxFlexScale BirthScale = null,
    VfxFlexScale InstanceScale = null,
    VfxFlexVector3 BirthTranslation = null,
    VfxFlexScale EmitOffsetScale = null);
