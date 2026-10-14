using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Animation.Builders;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer;

public sealed class SkinPoseTests
{
    private static uint H(string name) => Fnv1a.HashLower(name);
    private static BinTreeStruct Struct(string field, string type, params BinTreeProperty[] fields) => new(H(field), H(type), fields);
    private static BinTreeContainer List(string name, params BinTreeProperty[] values) => new(H(name), BinPropertyType.Struct, values);

    [Fact]
    public void ReaderUsesAuthoredValuesAndConstructorDefaultsAndPreservesUnsupportedClasses()
    {
        var mesh = Struct("skinMeshProperties", "SkinMeshDataProperties",
            List("rigPoseModifierData", Struct("", "SpringPhysicsRigPoseModifierData",
                new BinTreeString(H("Joint"), "Hand"), new BinTreeHash(H("name"), H("Hair")),
                new BinTreeBitBool(H("DoTranslation"), true), new BinTreeF32(H("Mass"), float.NaN)),
                Struct("", "ConformToPathRigPoseModifierData"), Struct("", "DynamicsChainRigPoseModifierData")),
            List("SocketDefinitions", Struct("", "SocketDefinitionWorld", new BinTreeString(H("name"), "Anchor"),
                new BinTreeVector3(H("PositionOffset"), new(1, 2, 3)))));

        SkinPoseDefinition definition = SkinPoseReader.Read(mesh);
        SkinSpringDefinition spring = Assert.Single(definition.Springs);
        Assert.Equal(H("Hand"), spring.Joint);
        Assert.Equal(0.1f, spring.Mass);
        Assert.True(spring.DoTranslation);
        Assert.True(spring.DefaultOn);
        Assert.Equal(2.5f, spring.Stiffness);
        Assert.Equal(65f, Assert.Single(definition.Conforms).MaxAngle);
        Assert.Equal(H("DynamicsChainRigPoseModifierData"), Assert.Single(definition.UnsupportedClasses));
        Assert.Equal(new Vector3(1, 2, 3), Assert.Single(definition.Sockets).Position);
    }

    [Fact]
    public void ResolverKeepsSelectedSkinAndDoesNotBorrowDynamicsFromDependencies()
    {
        BinTreeObject Skin(string name, string path, string joint) => new(name, "SkinCharacterDataProperties", new BinTreeProperty[]
        {
            Struct("skinMeshProperties", "SkinMeshDataProperties", new BinTreeString(H("simpleSkin"), path),
                List("rigPoseModifierData", Struct("", "SpringPhysicsRigPoseModifierData", new BinTreeString(H("Joint"), joint))))
        });
        var primary = new BinTree(new[] { Skin("Characters/Test/Skins/Skin1", "assets/test.skn", "Hand") }, Array.Empty<string>());
        var dependency = new BinTree(new[] { Skin("Characters/Other/Skins/Skin0", "assets/other.skn", "Wrong") }, Array.Empty<string>());
        SknMaterialTextureMetadata metadata = SknMaterialTextureResolver.ReadMetadata(new[] { primary, dependency }, targetSknPath: "assets/test.skn");
        Assert.Equal(H("Hand"), Assert.Single(metadata.PoseDefinition.Springs).Joint);
        Assert.Equal(H("Hand"), Assert.Single(SknMaterialTextureResolver.Resolve(metadata, Array.Empty<string>()).PoseDefinition.Springs).Joint);
    }

    [Fact]
    public void SocketUsesBindAxesThenFollowsAnimatedRotationAndScale()
    {
        var socket = new SkinSocketDefinition("tip", SkinSocketKind.Joint, H("Hand"), Vector3.UnitX * 2f);
        Matrix4x4 bind = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateRotationZ(MathF.PI / 2f) * Matrix4x4.CreateTranslation(3, 0, 0);
        Matrix4x4 posed = Matrix4x4.CreateScale(4f) * Matrix4x4.CreateTranslation(10, 0, 0);
        Assert.True(SkinSocketResolver.Resolve(socket, posed, bind, out Matrix4x4 resolved));
        Near(new(10f, -4f, 0f), resolved.Translation);
        Assert.True(SkinSocketResolver.Resolve(socket with { FreezePosition = Vector3.One }, posed, bind, out resolved));
        Near(new(5f, 0f, 0f), resolved.Translation);
    }

    [Theory]
    [InlineData(20f, 30f, 40f)]
    [InlineData(0f, 70f, 90f)]
    [InlineData(-40f, 15f, -30f)]
    public void SocketEulerRotationUsesXZYAndFrozenAxesRetainBindAngles(float x, float y, float z)
    {
        Quaternion rotation = SkinSocketResolver.Euler(new Vector3(x, y, z));
        Quaternion restored = SkinSocketResolver.Euler(SkinSocketResolver.Euler(rotation));
        Assert.InRange(MathF.Abs(Quaternion.Dot(rotation, restored)), 0.99999f, 1.00001f);
        var socket = new SkinSocketDefinition("tip", SkinSocketKind.Joint, 1, Vector3.Zero,
            FreezeRotation: Vector3.One);
        Matrix4x4 bind = Matrix4x4.CreateFromQuaternion(rotation);
        Assert.True(SkinSocketResolver.Resolve(socket, Matrix4x4.CreateRotationX(1f), bind, out Matrix4x4 result));
        MatrixNear(bind, result);
    }

    [Fact]
    public void JointNamesWinAndFirstInvalidSocketDoesNotFallThrough()
    {
        RigResource skeleton = Rig();
        var definition = new SkinPoseDefinition { Sockets = new[]
        {
            new SkinSocketDefinition("Hand", SkinSocketKind.World, 0, new(99, 0, 0)),
            new SkinSocketDefinition("Missing", SkinSocketKind.Joint, H("Absent"), Vector3.Zero),
            new SkinSocketDefinition("Missing", SkinSocketKind.World, 0, Vector3.One),
            new SkinSocketDefinition("Anchor", SkinSocketKind.Joint, H("Hand"), Vector3.UnitX)
        } };
        var provider = AnimationService.CreateBindBoneTransformProvider(skeleton, definition);
        Near(new(2, 0, 0), provider("Hand", 0).Value.Translation);
        Assert.Null(provider("Missing", 0));
        Assert.Null(provider(null, H("Missing")));
        Near(new(3, 0, 0), provider(null, H("Anchor")).Value.Translation);
    }

    [Fact]
    public void AnimatedSocketFollowsFinalSnapAndDoesNotAddSkinningBones()
    {
        RigResource skeleton = Rig();
        using var animation = new MovingAnimation();
        using var evaluator = new AnimationService();
        evaluator.ConfigurePose(new SkinPoseDefinition { Sockets = new[]
        {
            new SkinSocketDefinition("Anchor", SkinSocketKind.Joint, H("Hand"), Vector3.UnitX)
        } }, Matrix4x4.Identity);
        evaluator.SetPoseCues(new[] { new AnimationJointSnapCue(0, null, H("Hand"), H("Root"), new Vector3(7, 0, 0)) });
        Matrix4x4[] palette = evaluator.EvaluateSkinningTransforms(0.5f, animation, skeleton);
        Assert.Equal(3, palette.Length);
        Assert.True(evaluator.TryGetBoneTransformExactName("Anchor", out Matrix4x4 socket));
        Assert.True(evaluator.TryGetBoneTransformFnv(H("Anchor"), out Matrix4x4 byHash));
        MatrixNear(socket, byHash);
        Near(new(8.5f, 0, 0), socket.Translation);
        Near(new(9.5f, 0, 0), evaluator.WorldBoneTransforms[2].Translation);
    }

    [Fact]
    public void JointSnapCanTargetASocket()
    {
        using var evaluator = new AnimationService();
        using var animation = new MovingAnimation();
        evaluator.ConfigurePose(new SkinPoseDefinition { Sockets = new[]
        {
            new SkinSocketDefinition("Target", SkinSocketKind.World, 0, new(20, 0, 0))
        } }, Matrix4x4.Identity);
        evaluator.SetPoseCues(new[] { new AnimationJointSnapCue(0, null, H("Hand"), H("Target"), Vector3.Zero) });
        evaluator.EvaluateSkinningTransforms(0.5f, animation, Rig());
        Near(new(20, 0, 0), evaluator.WorldBoneTransforms[1].Translation);
        Near(new(22, 0, 0), evaluator.WorldBoneTransforms[2].Translation);
    }

    [Fact]
    public void VfxSamplingRestoresTheVisiblePoseAndPaletteAtTheSameTime()
    {
        using var evaluator = new AnimationService();
        using var animation = new MovingAnimation();
        RigResource rig = Rig();
        Matrix4x4[] before = evaluator.EvaluateSkinningTransforms(0.8f, animation, rig).ToArray();
        Matrix4x4[] visible = evaluator.WorldBoneTransforms.ToArray();
        Assert.True(evaluator.TrySampleBoneTransform(0.2f, "Hand", 0, out Matrix4x4 sampled));
        Near(new(2.2f, 0, 0), sampled.Translation);
        for (int at = 0; at < visible.Length; at++)
        {
            MatrixNear(visible[at], evaluator.WorldBoneTransforms[at]);
            MatrixNear(before[at], evaluator.FinalBoneTransforms[at]);
        }
        Assert.True(evaluator.TrySampleRootTransform(0.1f, out sampled));
        Near(new(0.1f, 0, 0), sampled.Translation);
        MatrixNear(visible[0], evaluator.WorldBoneTransforms[0]);
    }

    [Fact]
    public void SpringTrailsMovementAfterWarmupAndSettlesWithinItsLimits()
    {
        var spring = new SkinSpringRuntime(new(H("spring"), H("Hand"), DoTranslation: true, MaxDistance: 3f));
        spring.Step(Matrix4x4.Identity, 1f / 60f);
        spring.Step(Matrix4x4.Identity, 1f / 60f);
        spring.Step(Matrix4x4.CreateTranslation(10, 0, 0), 1f / 60f);
        Assert.InRange(spring.Offset.X, -3f, -2.9f);
        Assert.Equal(Vector3.Zero, spring.Velocity);
        for (int at = 0; at < 1200; at++) spring.Step(Matrix4x4.CreateTranslation(10, 0, 0), 1f / 60f);
        Assert.InRange(spring.Offset.Length(), 0f, 0.001f);
    }

    [Fact]
    public void SpringOffsetIsInParentAxesAndInvertReversesIt()
    {
        var model = new SkinSpringDefinition(1, 2, Stiffness: 0f, Damping: 0f, DoTranslation: true);
        var spring = new SkinSpringRuntime(model);
        spring.Step(Matrix4x4.Identity, 0.02f);
        spring.Step(Matrix4x4.Identity, 0.02f);
        spring.Step(Matrix4x4.CreateTranslation(3, 0, 0), 0.02f);
        Matrix4x4 parent = Matrix4x4.CreateRotationZ(MathF.PI / 2f);
        Matrix4x4 normal = spring.Apply(Matrix4x4.Identity, parent, 1f);
        Near(new(0, 3, 0), normal.Translation);
        MatrixNear(Matrix4x4.Identity, spring.Apply(Matrix4x4.Identity, parent, 0f));
        var inverted = new SkinSpringRuntime(model with { Invert = true });
        inverted.Step(Matrix4x4.Identity, 0.02f);
        inverted.Step(Matrix4x4.Identity, 0.02f);
        inverted.Step(Matrix4x4.CreateTranslation(3, 0, 0), 0.02f);
        Near(-normal.Translation, inverted.Apply(Matrix4x4.Identity, parent, 1f).Translation);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void InvalidMassAndZeroTimeNeverProduceInvalidMatrices(float mass)
    {
        var spring = new SkinSpringRuntime(new(1, 2, Mass: mass, DoTranslation: true, DoRotation: true));
        for (int at = 0; at < 10; at++) spring.Step(Matrix4x4.CreateTranslation(at, 0, 0), 0.02f);
        spring.Step(Matrix4x4.Identity, float.NaN);
        Assert.Equal(Vector3.Zero, spring.Offset);
        Assert.Equal(0f, spring.Angle);
    }

    [Fact]
    public void SpringYawTakesShortestTurnAndHonorsItsAngularLimit()
    {
        var spring = new SkinSpringRuntime(new(1, 2, Stiffness: 0f, Damping: 0f, DoRotation: true, MaxAngle: 10f));
        Matrix4x4 first = Matrix4x4.CreateRotationY(179f * MathF.PI / 180f);
        spring.Step(first, 0.02f);
        spring.Step(first, 0.02f);
        spring.Step(Matrix4x4.CreateRotationY(-179f * MathF.PI / 180f), 0.02f);
        Assert.InRange(spring.Angle * 180f / MathF.PI, 1.99f, 2.01f);
        spring.Step(Matrix4x4.CreateRotationY(-90f * MathF.PI / 180f), 0.02f);
        Assert.InRange(spring.Angle * 180f / MathF.PI, 9.89f, 9.91f);
    }

    [Fact]
    public void ConformTakeIsIndependentOfSeekOrderAndLeavesOtherJointsExact()
    {
        using var evaluator = new AnimationService();
        using var animation = new MovingAnimation();
        RigResource rig = Rig();
        evaluator.ConfigurePose(new SkinPoseDefinition { Conforms = new[] { new SkinConformDefinition(H("Hand"), H("Tip"), 0) } }, Matrix4x4.Identity);
        Matrix4x4[] expected = evaluator.EvaluateSkinningTransforms(0.4f, animation, rig).ToArray();
        evaluator.EvaluateSkinningTransforms(0.9f, animation, rig);
        evaluator.EvaluateSkinningTransforms(0.1f, animation, rig);
        Matrix4x4[] actual = evaluator.EvaluateSkinningTransforms(0.4f, animation, rig);
        for (int at = 0; at < actual.Length; at++) MatrixNear(expected[at], actual[at]);
        Near(new(0.4f, 0, 0), evaluator.WorldBoneTransforms[0].Translation);
    }

    [Fact]
    public void OrientationAimHonorsPlaneAxisAndLimit()
    {
        var model = new SkinOrientationDefinition(new uint[] { 1 }, 1, Vector3.UnitX, AimNegated: false, MaxAngle: 30f);
        Quaternion rotation = SkinOrientationSolver.Aim(model, Vector3.UnitX);
        Vector3 aimed = Vector3.Transform(Vector3.UnitZ, rotation);
        Near(new(0.5f, 0, MathF.Sqrt(3f) / 2f), aimed);
        Assert.Equal(Quaternion.Identity, SkinOrientationSolver.Aim(model, Vector3.Zero));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LiveSpringEventTogglesOnlyTheNamedSpringAndSamplingDoesNotAdvanceIt(bool defaultOn)
    {
        var definition = new SkinPoseDefinition { Springs = new[] {
            new SkinSpringDefinition(H("hair"), H("Hand"), Stiffness: 0, Damping: 0, DoTranslation: true, DefaultOn: defaultOn),
            new SkinSpringDefinition(H("tail"), H("Tip"), Stiffness: 0, Damping: 0, DoTranslation: true) } };
        var runtime = new SkinPoseRuntime(definition, new[] { -1, 0, 1 }, new[] { 0, 1, 2 },
            hash => hash == H("Hand") ? 1 : hash == H("Tip") ? 2 : 0);
        runtime.SetCues(new AnimationClipTimedCue[] { new AnimationSpringCue(0.02, 0.04, H("hair"), 0) }, null);
        runtime.Advance(0, Matrix4x4.Identity, 1f / 60);
        runtime.Advance(0.01f, Matrix4x4.Identity, 1f / 60);
        runtime.Advance(0.03f, Matrix4x4.CreateTranslation(3, 0, 0), 1f / 60);
        Matrix4x4[] Locals() => new[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation(2, 0, 0), Matrix4x4.CreateTranslation(2, 0, 0) };
        var locals = Locals();
        var worlds = new Matrix4x4[3];
        runtime.ApplyPost(0.03f, locals, worlds);
        Near(new(defaultOn ? 2 : -1, 0, 0), locals[1].Translation);
        Near(new(-1, 0, 0), locals[2].Translation);
        locals = Locals();
        runtime.ApplyPost(0.03f, locals, worlds);
        Near(new(-1, 0, 0), locals[2].Translation);
        locals = Locals();
        runtime.ApplyPost(0.05f, locals, worlds);
        Near(new(defaultOn ? -1 : 2, 0, 0), locals[1].Translation);
        runtime.Advance(0.9f, Matrix4x4.CreateTranslation(3, 0, 0), 1f / 60);
        locals = Locals();
        runtime.ApplyPost(0.9f, locals, worlds);
        Near(new(2, 0, 0), locals[1].Translation);
    }

    [Fact]
    public void OrientationLockCapturesBeforeTurningAndBlendsBackAfterItsEnd()
    {
        var runtime = new SkinPoseRuntime(SkinPoseDefinition.Empty, new[] { -1 }, new[] { 0 }, _ => 0);
        runtime.SetCues(new AnimationClipTimedCue[] { new AnimationLockOrientationCue(0, 0.1, H("Root"), 0.2f) }, null);
        runtime.Advance(0, Matrix4x4.Identity, 1f / 60);
        runtime.Advance(0.03f, Matrix4x4.CreateRotationY(MathF.PI / 2), 1f / 60);
        var locals = new[] { Matrix4x4.Identity };
        runtime.ApplyLocks(0.03f, locals);
        Near(-Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitZ, locals[0]));
        locals[0] = Matrix4x4.Identity;
        runtime.ApplyLocks(0.2f, locals);
        Near(new(-MathF.Sqrt(0.5f), 0, MathF.Sqrt(0.5f)), Vector3.TransformNormal(Vector3.UnitZ, locals[0]));
        locals[0] = Matrix4x4.Identity;
        runtime.ApplyLocks(0.4f, locals);
        MatrixNear(Matrix4x4.Identity, locals[0]);
    }

    [Fact]
    public void GraphParserUsesTrackConstructorDefaultAndReadsPoseEventsOnTheGraphClock()
    {
        var events = new BinTreeMap(H("mEventDataMap"), BinPropertyType.Hash, BinPropertyType.Struct, new[] {
            new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, 1), Struct("", "SpringPhysicsEventData",
                new BinTreeF32(H("mStartFrame"), 6), new BinTreeF32(H("mEndFrame"), 12), new BinTreeString(H("SpringToAffect"), "Hair"))),
            new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, 2), Struct("", "LockRootOrientationEventData",
                new BinTreeString(H("JointName"), "Root"))),
            new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, 3), Struct("", "JointOrientationEventData")) });
        var clipMap = new BinTreeMap(H("mClipDataMap"), BinPropertyType.Hash, BinPropertyType.Struct, new[] {
            new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, 1), Struct("", "AtomicClipData", events)) });
        var trackMap = new BinTreeMap(H("mTrackDataMap"), BinPropertyType.Hash, BinPropertyType.Struct, new[] {
            new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, 2), Struct("", "TrackData")) });
        var graph = Assert.Single(AssetsManager.Services.Viewer.Vfx.Parsing.VfxAnimationParser.ExtractAnimationGraphs(
            new BinTree(new[] { new BinTreeObject("Animations/Test", "AnimationGraphData", new BinTreeProperty[] { clipMap, trackMap }) }, Array.Empty<string>()), null, null));
        Assert.Equal(1f, Assert.Single(graph.Tracks).BlendWeight);
        var clip = Assert.Single(graph.Clips);
        Assert.Equal(H("Hair"), Assert.IsType<AnimationSpringEventDefinition>(clip.Events[0]).SpringHash);
        Assert.Equal(0.2f, Assert.IsType<AnimationLockOrientationEventDefinition>(clip.Events[1]).BlendOutSeconds);
        Assert.Null(Assert.IsType<AnimationOrientationEventDefinition>(clip.Events[2]).BlendFromSeconds);
        var cues = MapCharacterAnimationRuntime.BuildTimedCues(new[] { clip }, new[] { new MovingAnimation() });
        var spring = Assert.Single(cues.OfType<AnimationSpringCue>());
        Assert.Equal(0.1, spring.AtSeconds, 6);
        Assert.Equal(0.2, spring.UntilSeconds.Value, 6);
    }

    private static RigResource Rig()
    {
        var builder = new RigResourceBuilder();
        JointBuilder root = builder.CreateJoint("Root").WithLocalTransform(Matrix4x4.Identity).WithInverseBindTransform(Matrix4x4.Identity);
        JointBuilder hand = root.CreateJoint("Hand").WithLocalTransform(Matrix4x4.CreateTranslation(2, 0, 0))
            .WithInverseBindTransform(Matrix4x4.CreateTranslation(-2, 0, 0));
        hand.CreateJoint("Tip").WithLocalTransform(Matrix4x4.CreateTranslation(2, 0, 0))
            .WithInverseBindTransform(Matrix4x4.CreateTranslation(-4, 0, 0));
        return builder.Build();
    }

    private sealed class MovingAnimation : IAnimationAsset
    {
        public float Duration => 1f;
        public float Fps => 60f;
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
        public void Evaluate(float time, IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose)
        {
            pose.Clear();
            pose[Elf.HashLower("Root")] = (Quaternion.Identity, new(time, 0, 0), Vector3.One);
        }
    }

    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0f, 0.001f);
    private static void MatrixNear(Matrix4x4 expected, Matrix4x4 actual)
    {
        Near(expected.Translation, actual.Translation);
        Near(new(expected.M11, expected.M12, expected.M13), new(actual.M11, actual.M12, actual.M13));
        Near(new(expected.M21, expected.M22, expected.M23), new(actual.M21, actual.M22, actual.M23));
        Near(new(expected.M31, expected.M32, expected.M33), new(actual.M31, actual.M32, actual.M33));
    }
}
