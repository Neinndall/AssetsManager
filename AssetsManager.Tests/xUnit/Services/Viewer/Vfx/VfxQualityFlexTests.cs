using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.IO;
using System.Text.Json;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Utils;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public sealed class VfxQualityFlexTests
{
    public static IEnumerable<object[]> Qualities()
    {
        float[][] rates = [[.05f,.1f,.2f,1,1,1],[.1f,.2f,.4f,1,1,1],[.2f,.4f,.6f,1,1,1],[.5f,1,1,1,1,1],[1,1,1,1,1,1]];
        int[][] culled = [[0,1,5],[0,1,5],[0,1],[0,4],[4]];
        for (int q = 0; q < 5; q++)
            for (int importance = 0; importance < 6; importance++)
                yield return [q, importance, rates[q][importance], culled[q].Contains(importance)];
    }

    [Theory, MemberData(nameof(Qualities))]
    public void QualityMatchesTheClientTables(int q, int importance, float rate, bool culled)
    {
        var quality = (VfxEffectsQuality)q;
        Assert.Equal(rate, VfxQuality.RateScale(quality, importance));
        Assert.Equal(culled, VfxQuality.Culled(quality, importance));
        Assert.Equal(new[] {15,15,20,30,30}[q], VfxQuality.StepRate(quality));
        Assert.Equal(new[] {0,1,2,3,3}[q], VfxQuality.SmoothingReach(quality));
        Assert.Equal(q >= 3, VfxQuality.Shadows(quality));
    }

    [Fact]
    public void FixedStepperBanksRemaindersAndCoarsensOnlyBeyondTwentySteps()
    {
        var stepper = new VfxSimulationStepper();
        Assert.Empty(stepper.Advance(.01f, 0, VfxEffectsQuality.VeryHigh));
        Assert.Single(stepper.Advance(.03f, 0, VfxEffectsQuality.VeryHigh));
        Assert.InRange(stepper.Bank, .0066, .0068);
        stepper.Bank = 0;
        var coarse = stepper.Advance(25f/30f, 0, VfxEffectsQuality.VeryHigh).ToArray();
        Assert.Equal(12, coarse.Length);
        Assert.All(coarse, dt => Assert.Equal(2f/30f, dt));
        Assert.Single(new VfxSimulationStepper().Advance(.2f, 8, VfxEffectsQuality.VeryLow));
        Assert.Equal(1f/60f, Assert.Single(new VfxSimulationStepper().Advance(1f/60f, 0xd4, VfxEffectsQuality.VeryHigh)));
    }

    [Fact]
    public void QualityCanRestoreAnImportanceCulledEmitterWithoutRevivingAnAuthoredDisabledOne()
    {
        var def = Definition() with { Importance = 4, Disabled = true, Culled = VfxCullReason.Importance };
        Assert.False(VfxQuality.Apply(def, VfxEffectsQuality.Low).Disabled);
        Assert.True(VfxQuality.Apply(def with { Culled = VfxCullReason.None }, VfxEffectsQuality.Low).Disabled);
        Assert.True(VfxQuality.Apply(def with { ColorblindVisibility = 2 }, VfxEffectsQuality.Low).Disabled);
    }

    [Fact]
    public void FlexBirthUsesBothTermsAndInstanceScaleReadsTheLiveChannel()
    {
        var def = Definition() with { BirthVelocity = VfxCurve3.Const(new Vector3(3,0,0)),
            BirthScale = VfxCurve3.Const(Vector3.One), Flex = new VfxFlexDefinition(
                Velocity: new(0, VfxCurve3.Const(Vector3.UnitX)), InstanceScale: new(0, 99),
                UvOffset: new(0, VfxCurve2.Const(new Vector2(.1f,.2f)))) };
        var runtime = Runtime(def);
        float[] channels = [2,0,0]; runtime.BindFlexChannels(channels);
        runtime.Update(1f/60f);
        var state = Assert.Single(runtime.Emitters);
        var particle = Assert.Single(state.Particles);
        Assert.Equal(15f, particle.Vel.X);
        float first = state.PrepareInstances(1)[3];
        channels[0] = .5f; state.InvalidateInstances();
        Assert.Equal(first * .25f, state.PrepareInstances(1)[3]);
        Assert.InRange(state.PrepareInstances(1)[19], .049f, .051f);
    }

    [Fact]
    public void GraphSnapshotsPreserveTheFixedStepBankAndChildrenShareFlexChannels()
    {
        var child = new VfxSystemDefinition(2,"child","",[Definition()]);
        var set = new VfxChildParticleSetDefinition([new("child",2,0)],false,VfxCurveF.Zero,VfxCurve3.Const(Vector3.Zero),0);
        var root = new VfxSystemDefinition(1,"root","",[Definition() with { ChildParticleSet = set }], Flags: 0);
        var graph = new VfxPlaybackGraphRuntime(root,Matrix4x4.Identity,7,
            new Dictionary<uint,VfxSystemDefinition>{{1,root},{2,child}}, new Dictionary<uint,uint>(),
            (d,t,s) => { var r=new VfxPlaybackRuntime(s);r.SetSystem(d,t);return r; });
        graph.SetFlexChannels(2,3,4);
        graph.Update(.02f);
        var snapshot=graph.CaptureSnapshot();
        graph.Update(.02f);
        Assert.Equal(2,graph.Runtimes.Count);
        Assert.Same(graph.Root.FlexChannels,graph.Runtimes[1].FlexChannels);
        float time=graph.Root.CurrentTime;
        graph.RestoreSnapshot(snapshot);graph.Update(.02f);
        Assert.Equal(time,graph.Root.CurrentTime);
    }

    [Fact]
    public void ShadowOnlyPhasesAndDistortionQualityGatesAreIndependentOfColour()
    {
        var automatic = Definition() with { DoesCastShadow = true };
        Assert.True(VfxRenderPhaseSemantics.CastsShadow(automatic));
        Assert.True(VfxRenderPhaseSemantics.Resolve(automatic).Draws);
        var shadow=automatic with { RenderState=VfxEmitterRenderState.Default with { RenderPhase=1 } };
        Assert.False(VfxRenderPhaseSemantics.Resolve(shadow).Draws);
        Assert.True(VfxRenderPhaseSemantics.CastsShadow(shadow));
        var distortion=automatic with { Distortion=new(1,3,null), EffectsQuality=VfxEffectsQuality.Low };
        Assert.False(VfxRenderPhaseSemantics.Resolve(distortion).Draws);
        Assert.False(VfxRenderPhaseSemantics.CastsShadow(distortion));
        Assert.False(VfxRenderPhaseSemantics.CastsShadow(automatic with { IsGroundLayer=true }));
    }

    [Fact]
    public void ShadowCameraKeepsTheFocusedGroundNearTheCentreAndTheSunNearerInDepth()
    {
        var target=new Vector3(400,0,-250);
        var light=Vector3.Normalize(new Vector3(.25f,.75f,-.05f));
        var matrix=VfxParticleShadowScene.Aim(Matrix4x4.CreateLookAt(new(400,1500,900),target,Vector3.UnitY), light);
        var centre=Vector3.Transform(target,matrix);
        Assert.InRange(MathF.Abs(centre.X),0,2f/2048);
        Assert.InRange(MathF.Abs(centre.Y),0,2f/2048);
        Assert.True(Vector3.Transform(target+light*100,matrix).Z<centre.Z);
    }

    [Fact]
    public void AuthoredFlagsShadowSliceAndAllFlexFieldsSurviveBinRoundTrip()
    {
        uint H(string name) => Fnv1a.HashLower(name);
        BinTreeStruct Node(string name, params BinTreeProperty[] fields) => new(H(name), H("FlexData"), fields);
        BinTreeStruct Flex(string name, BinTreeProperty value) => Node(name, new BinTreeU32(H("mFlexID"), 2), value);
        BinTreeStruct Scalar(string name) => Flex(name, Node("mValue", new BinTreeF32(H("constantValue"), 3)));
        BinTreeStruct Vector(string name) => Flex(name, Node("mValue", new BinTreeVector3(H("constantValue"), new(1,2,3))));
        BinTreeStruct Uv(string name) => Flex(name, Node("mValue", new BinTreeVector2(H("constantValue"), new(4,5))));
        BinTreeStruct Scale(string name) => Flex(name, new BinTreeF32(H("mValue"), 6));
        var emitter = new BinTreeStruct(0, H("VfxEmitterDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeF32(H("rate"), 1), new BinTreeBitBool(H("doesCastShadow"), true),
            new BinTreeF32(H("sliceTechniqueRange"), .2f),
            Scalar("flexRate"), Scalar("flexParticleLifetime"), Vector("flexBirthVelocity"),
            Vector("flexBirthRotationalVelocity0"), Uv("flexBirthUVScrollRate"), Uv("flexBirthUVOffset"),
            Scale("flexScaleBirthScale"), Scale("FlexInstanceScale"),
            Node("textureMult", Uv("flexBirthUVScrollRateMult")),
            Node("FlexShapeDefinition", Vector("flexBirthTranslation"), Scale("flexScaleEmitOffset"))
        });
        var system = new BinTreeObject("Effects/QualityFlex", "VfxSystemDefinitionData", new BinTreeProperty[]
        {
            new BinTreeU32(H("flags"), 8),
            new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Struct, new BinTreeProperty[]{emitter})
        });
        using var stream = new MemoryStream();
        new BinTree(new[]{system}, Array.Empty<string>()).Write(stream);
        var parsed = Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value;
        Assert.Equal(8u, parsed.Flags);
        var def = Assert.Single(parsed.Emitters);
        Assert.Equal(2, def.Importance); Assert.True(def.DoesCastShadow); Assert.Equal(.2f, def.SliceRange);
        var flex = Assert.IsType<VfxFlexDefinition>(def.Flex);
        Assert.Equal(new VfxFlexFloat(2,VfxCurveF.Const(3)), flex.Rate);
        Assert.Equal(flex.Rate, flex.Lifetime);
        Assert.Equal(new Vector3(1,2,3), flex.Velocity.Value.Constant);
        Assert.Equal(2,flex.Velocity.Channel); Assert.Equal(flex.Velocity,flex.RotationalVelocity);
        Assert.Equal(flex.Velocity,flex.BirthTranslation);
        Assert.Equal(new Vector2(4,5),flex.UvScroll.Value.Constant);
        Assert.Equal(2,flex.UvScroll.Channel); Assert.Equal(flex.UvScroll,flex.UvScrollMult); Assert.Equal(flex.UvScroll,flex.UvOffset);
        Assert.Equal(new VfxFlexScale(2,6),flex.BirthScale);
        Assert.Equal(flex.BirthScale,flex.InstanceScale); Assert.Equal(flex.BirthScale,flex.EmitOffsetScale);
    }

    [Fact]
    public void ViewerQualityDefaultsToHighAndPersistsWithChangeNotifications()
    {
        var settings = JsonSerializer.Deserialize<StudioParametersSettings>("{}");
        Assert.Equal(3,settings.EffectsQuality);
        int changes=0;
        settings.PropertyChanged += (_,e) => { if(e.PropertyName==nameof(settings.EffectsQuality)) changes++; };
        settings.EffectsQuality=1; settings.EffectsQuality=-1; settings.EffectsQuality=5;
        Assert.Equal(1,changes);
        var restored=JsonSerializer.Deserialize<StudioParametersSettings>(JsonSerializer.Serialize(settings));
        Assert.Equal(1,restored.EffectsQuality);
    }

    internal static VfxEmitterDefinition Definition() => VfxBeamSupportTests.Definition() with
    { PrimitiveKind=VfxPrimitiveKind.CameraQuad, Beam=null, IsSingleParticle=true, Rate=VfxCurveF.Const(1), Importance=2 };
    private static VfxPlaybackRuntime Runtime(VfxEmitterDefinition def)
    { var r=new VfxPlaybackRuntime();r.SetSystem(new(1,"test","",[def],Flags:8),Vector3.Zero);return r; }
}
