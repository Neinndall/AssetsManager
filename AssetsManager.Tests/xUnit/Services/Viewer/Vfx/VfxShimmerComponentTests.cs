using System;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public sealed class VfxShimmerComponentTests
{
    private static uint H(string name) => name.StartsWith("0x") ? Convert.ToUInt32(name[2..], 16) : Fnv1a.HashLower(name);
    private static BinTreeStruct Node(string field, string type, params BinTreeProperty[] values) => new(H(field), H(type), values);
    private static BinTreeStruct Scalar(string field, float value) => Node(field, "VfxFloatDynamicProperty",
        Node("Float", "VfxFloatConstantDriver", new BinTreeF32(H("Float"), value)));
    private static BinTreeStruct Vector(string field, Vector3 value) => Node(field, "VfxVector3DynamicProperty",
        Node("Vector3", "VfxVector3ConstantDriver", new BinTreeVector3(H("Vector3"), value)));

    private static VfxSystemDefinition System(int burst = 1, float rate = 0, float lifetime = -1,
        float delay = 0, float duration = -1, float spread = 0, bool looping = false, float loopDelay = 0,
        bool copy = false, bool uniform = false, params BinTreeProperty[] modifiers)
    {
        var components = Node("VfxComponents", "VfxComponents",
            Node("GeometryComponent", "VfxGeometryComponent", new BinTreeString(H("mesh"), "assets/cube.gmesh")),
            Node("LifetimeComponent", "VfxLifetimeComponent",
                Node("LifetimeBehavior", looping ? "0xdbb4f634" : "0x7015f762",
                    Scalar("EmitterDuration", duration), Scalar("particleLifetime", lifetime), Scalar("startDelay", delay), Scalar("LoopDelay", loopDelay)),
                Node("SpawnBehavior", "0x31beb841", Scalar("EmissionRate", rate), Scalar("0x10498eed", burst), Scalar("SpawnDuration", spread))),
            Node("PhysicsComponent", "VfxModularPhysicsComponent", new BinTreeContainer(H("Modifiers"), BinPropertyType.Struct,
                new[] {Node("", "VfxPhysicsSimpleModifier", modifiers.Concat(new[] {new BinTreeBool(H("IsUniform"), uniform)}).ToArray())})));
        BinTreeStruct Emitter(bool disabled) => Node("", "VfxEmitterDefinitionData", new BinTreeString(H("emitterName"), disabled ? "copy" : "component"),
            new BinTreeBool(H("disabled"), disabled), components);
        var fields = new System.Collections.Generic.List<BinTreeProperty>
        {
            new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Struct, new[] { Emitter(false) })
        };
        if (copy) fields.Add(new BinTreeContainer(H("shimmerEmitterDefinitionData"), BinPropertyType.Struct, new[] {Emitter(true)}));
        var tree = new BinTree(new[] {new BinTreeObject("Vfx/Shimmer", "VfxSystemDefinitionData", fields)}, Array.Empty<string>());
        using var stream = new MemoryStream(); tree.Write(stream);
        return Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value;
    }

    [Fact]
    public void ReadsComplexComponentsWithoutConventionalNoRateCullingAndPreservesDisabledCopies()
    {
        var system = System(burst: 2, copy: true);
        var active = system.Emitters[0];
        Assert.NotNull(active.Shimmer);
        Assert.False(active.Disabled);
        Assert.Equal(VfxCullReason.None, active.Culled);
        Assert.Equal("assets/cube.gmesh", active.MeshPath);
        Assert.True(system.Emitters[1].Disabled);
        var runtime = new VfxPlaybackRuntime(7); runtime.SetSystem(system, Vector3.Zero);
        Assert.Equal(2, runtime.LiveParticleCount);
        runtime.Seek(1001);
        Assert.Equal(2, runtime.LiveParticleCount);
        Assert.Equal(0, runtime.Emitters[1].InstanceCount);
        runtime.Seek(0);
        Assert.Equal(2, runtime.LiveParticleCount);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 3, 7)]
    [InlineData(2, 3, 9)]
    public void MatchesShimmerBurstAndStreamSchedules(int burst, float rate, int expected)
    {
        var runtime = new VfxPlaybackRuntime(7); runtime.SetSystem(System(burst: burst, rate: rate), Vector3.Zero);
        runtime.Seek(2);
        Assert.Equal(expected, runtime.LiveParticleCount);
        if (rate > 0) { runtime.Seek(3600); Assert.Equal(32, runtime.LiveParticleCount); }
    }

    [Fact]
    public void RespectsStartDelaySpreadLifetimeAndLoopDelay()
    {
        var runtime = new VfxPlaybackRuntime(7);
        runtime.SetSystem(System(burst: 2, delay: 1, spread: 2, lifetime: 1, duration: 2, looping: true, loopDelay: 1), Vector3.Zero);
        Assert.Equal(0, runtime.LiveParticleCount);
        runtime.Seek(1.1f); Assert.Equal(1, runtime.LiveParticleCount);
        runtime.Seek(2.1f); Assert.Equal(1, runtime.LiveParticleCount);
        runtime.Seek(3.1f); Assert.Equal(0, runtime.LiveParticleCount);
        runtime.Seek(4.1f); Assert.Equal(1, runtime.LiveParticleCount);
    }

    [Fact]
    public void UsesDegreesOnceAndAnalyticLocalMotionWithAuthoredScale()
    {
        var definition = System(uniform: true, modifiers: new[] {
            Vector("InitialScale", new Vector3(2, 4, 6)), Vector("KeyedScale", new Vector3(3, 5, 7)),
            Vector("InitialRotation", new Vector3(180, 0, 0)), Vector("InitialVelocity", new Vector3(0, 2, 0)),
            Vector("Acceleration", new Vector3(0, 3, 0)) });
        var runtime = new VfxPlaybackRuntime(7); runtime.SetSystem(definition, Vector3.Zero);
        runtime.Seek(2);
        var state = Assert.Single(runtime.Emitters);
        var particle = Assert.Single(state.Particles);
        Assert.InRange(particle.LocalPosition.Y, 9.99f, 10.01f);
        Assert.InRange(particle.BirthRotation.X, MathF.PI - 0.0001f, MathF.PI + 0.0001f);
        Assert.Equal(new Vector3(2), particle.BirthSize);
        Assert.Equal(6f, state.Instances[3]);
        runtime.SetEffectsQuality(VfxEffectsQuality.VeryLow);
        Assert.False(state.Def.Disabled);
    }

    [Fact]
    public void ReplayAndSnapshotKeepTheSameComponentInstances()
    {
        var runtime = new VfxPlaybackRuntime(7); runtime.SetSystem(System(burst: 2), Vector3.Zero);
        runtime.Seek(2); var expected = runtime.Emitters[0].Instances.ToArray();
        var snapshot = runtime.CaptureSnapshot();
        runtime.Seek(3); runtime.RestoreSnapshot(snapshot);
        Assert.Equal(expected, runtime.Emitters[0].Instances);
        runtime.Reset(); runtime.Seek(2);
        Assert.Equal(expected, runtime.Emitters[0].Instances);
    }

    [Fact]
    public void FiniteComponentEmissionCompletesAfterItsLastParticleDies()
    {
        var runtime = new VfxPlaybackRuntime(7);
        runtime.SetSystem(System(burst: 1, lifetime: 1), Vector3.Zero);
        Assert.False(runtime.IsComplete);
        runtime.Seek(2);
        Assert.Equal(0, runtime.LiveParticleCount);
        Assert.True(runtime.IsComplete);
    }

    [Fact]
    public void ComponentParticlesUseTheMapPlacementTransform()
    {
        var runtime = new VfxPlaybackRuntime(7);
        runtime.SetSystem(System(), Vector3.Zero);
        runtime.SetTransform(Matrix4x4.CreateTranslation(100, 20, -30));
        runtime.Seek(2);
        Assert.Equal(new Vector3(100, 20, -30), Assert.Single(runtime.Emitters[0].Particles).Pos);
    }

    [Fact]
    public void ComponentsDoNotConsumeTheConventionalEmittersRandomStream()
    {
        var system = System();
        var conventional = system.Emitters[0] with { Name = "ordinary", Shimmer = null, Rate = VfxCurveF.Const(10),
            IsSingleParticle = false, ParticleLifetime = VfxCurveF.Const(3), RandomStartFrame = true, NumFrames = 16 };
        var baseline = new VfxPlaybackRuntime(7331);
        var mixed = new VfxPlaybackRuntime(7331);
        baseline.SetSystem(system with { Emitters = new[] { conventional } }, Vector3.Zero);
        mixed.SetSystem(system with { Emitters = new[] { system.Emitters[0], conventional } }, Vector3.Zero);
        for (int frame = 0; frame < 60; frame++) { baseline.Update(1f / 30); mixed.Update(1f / 30); }
        Assert.Equal(baseline.RandomState, mixed.RandomState);
        Assert.Equal(baseline.Emitters[0].Instances, mixed.Emitters[1].Instances);
    }

    [Fact]
    public void EvaluatesParticleFrequencyKeyedScaleAndColorOverLifeCurves()
    {
        var keyedScaleCurve = Node("KeyedScale", "VfxVector3DynamicProperty",
            Node("Vector3", "0x2d42ea41",
                new BinTreeU8(H("frequency"), 2),
                Node("Vector3", "ValueVector3",
                    Node("dynamics", "VfxAnimatedVector3fVariableData",
                        new BinTreeContainer(H("times"), BinPropertyType.F32, new[] { new BinTreeF32(0, 0f), new BinTreeF32(0, 0.5f), new BinTreeF32(0, 1f) }),
                        new BinTreeContainer(H("values"), BinPropertyType.Vector3, new[] { new BinTreeVector3(0, Vector3.Zero), new BinTreeVector3(0, new Vector3(2f, 4f, 6f)), new BinTreeVector3(0, Vector3.One) })))));
        var colorOverLifeCurve = Node("ColorOverLife", "VfxVector4DynamicProperty",
            Node("Vector4", "0x7cc5a312",
                new BinTreeU8(H("frequency"), 2),
                Node("colors", "ValueColor",
                    Node("dynamics", "VfxAnimatedColorVariableData",
                        new BinTreeContainer(H("times"), BinPropertyType.F32, new[] { new BinTreeF32(0, 0f), new BinTreeF32(0, 0.5f), new BinTreeF32(0, 1f) }),
                        new BinTreeContainer(H("values"), BinPropertyType.Vector4, new[] { new BinTreeVector4(0, new Vector4(1, 1, 1, 0)), new BinTreeVector4(0, new Vector4(0.25f, 0.5f, 1f, 0.8f)), new BinTreeVector4(0, new Vector4(0, 0, 0, 0)) })))));

        var components = Node("VfxComponents", "VfxComponents",
            Node("GeometryComponent", "VfxGeometryComponent", new BinTreeString(H("mesh"), "assets/cube.gmesh")),
            Node("LifetimeComponent", "VfxLifetimeComponent",
                Node("LifetimeBehavior", "0x7015f762", Scalar("EmitterDuration", -1), Scalar("particleLifetime", 2f), Scalar("startDelay", 0), Scalar("LoopDelay", 0)),
                Node("SpawnBehavior", "0x31beb841", Scalar("EmissionRate", 0), Scalar("0x10498eed", 1), Scalar("SpawnDuration", 0))),
            Node("PhysicsComponent", "VfxModularPhysicsComponent", new BinTreeContainer(H("Modifiers"), BinPropertyType.Struct,
                new[] { Node("", "VfxPhysicsSimpleModifier", keyedScaleCurve) })),
            Node("RenderComponent", "0x5a4c5648",
                Node("Color", "0x3d7e6258", colorOverLifeCurve)));
        var emitter = Node("", "VfxEmitterDefinitionData", new BinTreeString(H("emitterName"), "animated"), components);
        var tree = new BinTree(new[] { new BinTreeObject("Vfx/ShimmerAnimated", "VfxSystemDefinitionData",
            new[] { new BinTreeContainer(H("shimmerEmitterDefinitionData"), BinPropertyType.Struct, new[] { emitter }) }) }, Array.Empty<string>());
        using var stream = new MemoryStream(); tree.Write(stream);
        var system = Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value;

        var runtime = new VfxPlaybackRuntime(7);
        runtime.SetSystem(system, Vector3.Zero);
        runtime.Seek(1f); // particle age 1s / lifetime 2s => age01 = 0.5
        var state = Assert.Single(runtime.Emitters);
        Assert.Equal(new Vector3(2f, 4f, 6f), state.Def.ScaleOverLife!.Value.Sample(0.5f));
        Assert.Equal(new Vector4(0.25f, 0.5f, 1f, 0.8f), state.Def.ColorOverLife!.Value.Sample(0.5f));
        Assert.Equal(2f, state.Instances[3], 3);
        Assert.Equal(4f, state.Instances[4], 3);
        Assert.Equal(6f, state.Instances[18], 3);
        Assert.Equal(0.2f, state.Instances[5], 3);
        Assert.Equal(0.4f, state.Instances[6], 3);
        Assert.Equal(0.8f, state.Instances[7], 3);
        Assert.Equal(1f, state.Instances[8], 3);
    }

    [Fact]
    public void CollapsesUniformKeyedScaleAndKeepsNonParticleFrequencyFallback()
    {
        var keyedScaleCurve = Node("KeyedScale", "VfxVector3DynamicProperty",
            Node("Vector3", "0x2d42ea41",
                new BinTreeU8(H("frequency"), 2),
                Node("Vector3", "ValueVector3",
                    Node("dynamics", "VfxAnimatedVector3fVariableData",
                        new BinTreeContainer(H("times"), BinPropertyType.F32, new[] { new BinTreeF32(0, 0f), new BinTreeF32(0, 1f) }),
                        new BinTreeContainer(H("values"), BinPropertyType.Vector3, new[] { new BinTreeVector3(0, new Vector3(1f, 9f, 9f)), new BinTreeVector3(0, new Vector3(3f, 7f, 5f)) })))));
        var emitterTimeColor = Node("ColorOverLife", "VfxVector4DynamicProperty",
            Node("Vector4", "0x7cc5a312",
                new BinTreeU8(H("frequency"), 0),
                Node("colors", "ValueColor",
                    new BinTreeVector4(0x9fc2a256u, new Vector4(0.2f, 0.3f, 0.4f, 0.5f)),
                    Node("dynamics", "VfxAnimatedColorVariableData",
                        new BinTreeContainer(H("times"), BinPropertyType.F32, new[] { new BinTreeF32(0, 0f), new BinTreeF32(0, 10f) }),
                        new BinTreeContainer(H("values"), BinPropertyType.Vector4, new[] { new BinTreeVector4(0, Vector4.Zero), new BinTreeVector4(0, Vector4.One) })))));

        var components = Node("VfxComponents", "VfxComponents",
            Node("GeometryComponent", "VfxGeometryComponent", new BinTreeString(H("mesh"), "assets/cube.gmesh")),
            Node("LifetimeComponent", "VfxLifetimeComponent",
                Node("LifetimeBehavior", "0x7015f762", Scalar("EmitterDuration", -1), Scalar("particleLifetime", 2f), Scalar("startDelay", 0), Scalar("LoopDelay", 0)),
                Node("SpawnBehavior", "0x31beb841", Scalar("EmissionRate", 0), Scalar("0x10498eed", 1), Scalar("SpawnDuration", 0))),
            Node("PhysicsComponent", "VfxModularPhysicsComponent", new BinTreeContainer(H("Modifiers"), BinPropertyType.Struct,
                new[]
                {
                    Node("", "VfxPhysicsSimpleModifier", keyedScaleCurve),
                    Node("", "VfxPhysicsSimpleModifier", new BinTreeBool(H("IsUniform"), true))
                })),
            Node("RenderComponent", "0x5a4c5648",
                Node("Color", "0x3d7e6258", emitterTimeColor)));
        var emitter = Node("", "VfxEmitterDefinitionData", new BinTreeString(H("emitterName"), "uniform"), components);
        var tree = new BinTree(new[] { new BinTreeObject("Vfx/ShimmerUniform", "VfxSystemDefinitionData",
            new[] { new BinTreeContainer(H("shimmerEmitterDefinitionData"), BinPropertyType.Struct, new[] { emitter }) }) }, Array.Empty<string>());
        using var stream = new MemoryStream(); tree.Write(stream);
        var system = Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value;

        var runtime = new VfxPlaybackRuntime(7);
        runtime.SetSystem(system, Vector3.Zero);
        runtime.Seek(1f); // particle age01 = 0.5 => uniform X = lerp(1, 3, 0.5) = 2 across X/Y/Z
        var state = Assert.Single(runtime.Emitters);
        Assert.Equal(2f, state.Instances[3], 3);
        Assert.Equal(2f, state.Instances[4], 3);
        Assert.Equal(2f, state.Instances[18], 3);
        Assert.Equal(1f, state.Instances[5], 3);
        Assert.Equal(1f, state.Instances[8], 3);
    }
}
