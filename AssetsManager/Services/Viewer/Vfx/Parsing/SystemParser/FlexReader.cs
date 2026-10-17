using System.Collections.Generic;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using static AssetsManager.Services.Viewer.Vfx.Parsing.VfxValueParser;

namespace AssetsManager.Services.Viewer.Vfx.Parsing;

internal static partial class VfxSystemParser
{
    private static VfxFlexDefinition ReadFlex(IReadOnlyDictionary<uint, BinTreeProperty> p)
    {
        uint H(string name) => VfxParsingHash.Fnv1a(name);
        BinTreeStruct Node(IReadOnlyDictionary<uint, BinTreeProperty> fields, string name)
            => Get(fields, H(name)) is BinTreeStruct { ClassHash: not 0 } value ? value : null;
        int Channel(BinTreeStruct n) => GetI32(n.Properties, H("mFlexID")) ?? GetU8(n.Properties, H("mFlexID")) ?? 0;
        VfxFlexFloat Scalar(string name) => Node(p, name) is { } n
            ? new(Channel(n), ReadCurveF(n.Properties, H("mValue")) ?? VfxCurveF.Zero) : null;
        VfxFlexVector3 Vector(IReadOnlyDictionary<uint, BinTreeProperty> fields, string name) => Node(fields, name) is { } n
            ? new(Channel(n), ReadCurve3(n.Properties, H("mValue")) ?? VfxCurve3.Const(System.Numerics.Vector3.Zero)) : null;
        VfxFlexVector2 Uv(IReadOnlyDictionary<uint, BinTreeProperty> fields, string name) => Node(fields, name) is { } n
            ? new(Channel(n), ReadCurve2(n.Properties, H("mValue")) ?? VfxCurve2.Const(System.Numerics.Vector2.Zero)) : null;
        VfxFlexScale Scale(IReadOnlyDictionary<uint, BinTreeProperty> fields, string name) => Node(fields, name) is { } n
            ? new(Channel(n), GetF32(n.Properties, H("mValue")) ?? 0f) : null;
        var shape = Node(p, "FlexShapeDefinition")?.Properties ?? new Dictionary<uint, BinTreeProperty>();
        var mult = Node(p, "textureMult")?.Properties ?? new Dictionary<uint, BinTreeProperty>();
        var flex = new VfxFlexDefinition(Scalar("flexRate"), Scalar("flexParticleLifetime"),
            Vector(p, "flexBirthVelocity"), Vector(p, "flexBirthRotationalVelocity0"),
            Uv(p, "flexBirthUVScrollRate"), Uv(mult, "flexBirthUVScrollRateMult"), Uv(p, "flexBirthUVOffset"),
            Scale(p, "flexScaleBirthScale"), Scale(p, "FlexInstanceScale"),
            Vector(shape, "flexBirthTranslation"), Scale(shape, "flexScaleEmitOffset"));
        return flex == new VfxFlexDefinition() ? null : flex;
    }
}
