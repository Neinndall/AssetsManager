using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Animation;

/// <summary>Reads the selected skin's effective mesh properties, including gear overlays.</summary>
internal static class SkinPoseReader
{
    internal static SkinPoseDefinition Read(BinTreeStruct mesh)
    {
        if (mesh == null) return SkinPoseDefinition.Empty;

        var springs = new List<SkinSpringDefinition>();
        var conforms = new List<SkinConformDefinition>();
        var orientations = new List<SkinOrientationDefinition>();
        var unsupported = new List<uint>();
        foreach (BinTreeStruct modifier in Items(Get(mesh, "rigPoseModifierData")).OfType<BinTreeStruct>())
        {
            if (modifier.ClassHash == Hash("SpringPhysicsRigPoseModifierData"))
            {
                springs.Add(new(Reference(Get(modifier, "name")), Reference(Get(modifier, "Joint")),
                    Number(modifier, "Mass", 0.1f), Number(modifier, "SpringStiffness", 2.5f),
                    Number(modifier, "Damping", 1f), Flag(modifier, "DoTranslation"), Flag(modifier, "DoRotation"),
                    Number(modifier, "maxDistance"), Number(modifier, "maxAngle"), Flag(modifier, "Invert"),
                    Flag(modifier, "DefaultOn", true)));
            }
            else if (modifier.ClassHash == Hash("ConformToPathRigPoseModifierData"))
            {
                conforms.Add(new(Reference(Get(modifier, "mStartingJointName")), Reference(Get(modifier, "mEndingJointName")),
                    Reference(Get(modifier, "mDefaultMaskName")), Number(modifier, "mMaxBoneAngle", 65f),
                    Number(modifier, "mDampingValue", 10f), Number(modifier, "mFrequency", 10f),
                    Number(modifier, "mVelMultiplier", -0.5f), Flag(modifier, "OnlyActivateInTurns"),
                    Number(modifier, "ActivationAngle", 0.5f), Number(modifier, "ActivationDistance", 200f),
                    Number(modifier, "BlendDistance", 400f))
                {
                    ExtraChains = Items(Get(modifier, "ExtraJointChains")).OfType<BinTreeStruct>()
                        .Select(extra => new SkinExtraConformChain(Reference(Get(extra, "StartingJointName")),
                            Reference(Get(extra, "EndingJointName")), Number(extra, "RightBias"))).ToArray()
                });
            }
            else if (modifier.ClassHash == Hash("JointOrientationRigPoseModifierData"))
            {
                var source = Get(modifier, "OrientationSource") as BinTreeStruct;
                orientations.Add(new(Items(Get(modifier, "Joints")).Select(Reference).ToArray(),
                    source?.ClassHash ?? 0u, null, Byte(Get(modifier, "orientationType")),
                    Byte(Get(modifier, "PlaneConstraint"), 1), Byte(Get(modifier, 0xa57f0269)),
                    Byte(Get(modifier, 0xae1cbd5f), 3), Bool(Get(modifier, 0x57722010), true),
                    Bool(Get(modifier, 0x1a30a486)), Number(Get(modifier, 0x420b233d), 180f),
                    Flag(modifier, "DefaultOn", true)));
            }
            else if (modifier.ClassHash != Hash("JointSnapRigPoseModifilerData") &&
                     modifier.ClassHash != Hash("LockRootOrientationRigPoseModifierData"))
            {
                unsupported.Add(modifier.ClassHash);
            }
        }

        var sockets = new List<SkinSocketDefinition>();
        foreach (BinTreeStruct socket in Items(Get(mesh, "SocketDefinitions")).OfType<BinTreeStruct>())
        {
            string name = (Get(socket, "name") as BinTreeString)?.Value ?? string.Empty;
            SkinSocketKind kind = socket.ClassHash == Hash("SocketDefinitionSingleJoint") ? SkinSocketKind.Joint
                : socket.ClassHash == Hash("SocketDefinitionWorld") ? SkinSocketKind.World : SkinSocketKind.Unsupported;
            sockets.Add(new(name, kind, Reference(Get(socket, "ParentJoint")), Vector(Get(socket, "PositionOffset")),
                Vector(Get(socket, "RotationOffset")), Flags(socket, "FreezePosition"), Flags(socket, "FreezeRotation")));
        }

        return new() { Springs = springs.ToArray(), Sockets = sockets.ToArray(), Conforms = conforms.ToArray(),
            Orientations = orientations.ToArray(), UnsupportedClasses = unsupported.Distinct().ToArray() };
    }

    internal static uint Hash(string name) => Fnv1a.HashLower(name);
    private static BinTreeProperty Get(BinTreeStruct data, string name) => Get(data, Hash(name));
    private static BinTreeProperty Get(BinTreeStruct data, uint hash)
    {
        BinTreeProperty value = data?.Properties.GetValueOrDefault(hash);
        return value is BinTreeOptional optional ? optional.Value : value;
    }
    private static IEnumerable<BinTreeProperty> Items(BinTreeProperty property) =>
        property is BinTreeContainer container ? container.Elements : Array.Empty<BinTreeProperty>();
    internal static uint Reference(BinTreeProperty property) => property switch
    {
        BinTreeHash hash => hash.Value,
        BinTreeString text => string.IsNullOrEmpty(text.Value) ? 0u : Hash(text.Value),
        BinTreeU32 word => word.Value,
        _ => 0u
    };
    private static float Number(BinTreeStruct data, string name, float fallback = 0f) => Number(Get(data, name), fallback);
    private static float Number(BinTreeProperty property, float fallback) =>
        property is BinTreeF32 number && float.IsFinite(number.Value) ? number.Value : fallback;
    private static byte Byte(BinTreeProperty property, byte fallback = 0) => property is BinTreeU8 value ? value.Value : fallback;
    private static bool Bool(BinTreeProperty property, bool fallback = false) => property switch
    {
        BinTreeBool value => value.Value,
        BinTreeBitBool value => value.Value,
        _ => fallback
    };
    private static bool Flag(BinTreeStruct data, string name, bool fallback = false) => Bool(Get(data, name), fallback);
    private static Vector3 Vector(BinTreeProperty property) =>
        property is BinTreeVector3 value && IsFinite(value.Value) ? value.Value : Vector3.Zero;
    private static Vector3 Flags(BinTreeStruct data, string prefix) => new(
        Flag(data, prefix + "X") ? 1f : 0f, Flag(data, prefix + "Y") ? 1f : 0f, Flag(data, prefix + "Z") ? 1f : 0f);
    internal static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
