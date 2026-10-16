using System;
using System.Numerics;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Views.Models.Viewer;
using Xunit;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

[Collection("VFX missile files")]
public sealed class VfxMissileFlightContractTests
{
    private static VfxSpellMissilePreview Missile(VfxSpellMissileMovementKind kind) =>
        new(kind, 1000, 1, null, null, null, null, null);

    [Theory]
    [InlineData(700f, 650f, 600f, 700f)]
    [InlineData(0f, 650f, 600f, 650f)]
    [InlineData(100001f, 0f, 600f, 600f)]
    [InlineData(-1f, -1f, 100000f, 100000f)]
    [InlineData(0f, 0f, 0f, null)]
    public void SerializedSpellRangeSelectsTheFirstValidRankOneValue(
        float display, float values, float legacy, float? expected)
    {
        BinTreeContainer Ranks(string name, float rankOne) => new(Fnv1a.HashLower(name), BinPropertyType.F32,
            new BinTreeProperty[] { new BinTreeF32(0,99999), new BinTreeF32(0,rankOne), new BinTreeF32(0,800),
                new BinTreeF32(0,800), new BinTreeF32(0,800), new BinTreeF32(0,800), new BinTreeF32(0,800) });
        var spell = new BinTreeObject("Characters/Hero/Spells/Q", "SpellObject", new BinTreeProperty[]
        {
            new BinTreeStruct(Fnv1a.HashLower("mSpell"), Fnv1a.HashLower("SpellDataResource"), new BinTreeProperty[]
            {
                Ranks("castRangeDisplayOverride", display), Ranks("castRange", legacy),
                new BinTreeStruct(Fnv1a.HashLower("castRangeValues"), 0x0a0eddc9, new BinTreeProperty[] { Ranks("values", values) }),
                new BinTreeF32(Fnv1a.HashLower("missileSpeed"), 2000f)
            })
        });
        using var bytes = new MemoryStream();
        new BinTree(new[] { spell }, Array.Empty<string>()).Write(bytes);
        bytes.Position = 0;
        var read = VfxSpellPreviewReader.Read(new BinTree(bytes).Objects[spell.PathHash]);
        Assert.Equal(expected, read.CastRange);
        Assert.Equal(2000f, read.MissileSpeed);
        Assert.Empty(read.Issues);
    }

    [Fact]
    public void SpellSizedRigReachesSessionTargetsAndReplaysThroughCheckpointsAndTuning()
    {
        var emitter = VfxQualityFlexTests.Definition() with
        {
            TexturePath = null, EmitterPosition = VfxCurve3.Const(Vector3.Zero),
            EmitterLifetime = 3f, ParticleLifetime = VfxCurveF.Const(3f)
        };
        var system = new VfxSystemDefinition(1, "projectile", "", new[] { emitter });
        var bundle = new VfxLoadingService.Bundle();
        bundle.Systems.Add(1, system);
        bundle.MissileSpells.Add(1, new[] { new VfxSpellPreview(
            null, null, null, 0, null, 600f, null,
            Missile(VfxSpellMissileMovementKind.FixedSpeed) with { Speed = null, HeightOffsetAtEnd = true }, 0, null, Array.Empty<VfxSpellIssue>())
            { MissileSpeed = 2000f } });
        var rig = VfxSystemRigResolver.ResolveSettings(system, bundle).Settings with { IsLooping = false };

        using var session = new VfxRenderSession();
        session.SetSystem(new VfxSystemModel { Definition = system, Name = system.Name, SystemCatalog = bundle.Systems });
        session.RigSettings = rig;
        var root = Assert.Single(session.Graphs).Root;
        Assert.Equal(-300f, root.WorldTransform.Translation.X, 3);
        Assert.Equal(300f, Assert.Single(root.Emitters).SystemTarget.X, 3);
        session.Seek(0.3);
        Assert.Equal(300f, root.WorldTransform.Translation.X, 2);
        session.Seek(0.5);
        session.Seek(0.1);
        Assert.Equal(-100f, root.WorldTransform.Translation.X, 2);
        session.Seek(0.3);
        Assert.True(session.LastSeekRestoreTime > 0d);
        Assert.Equal(300f, root.WorldTransform.Translation.X, 2);

        session.Seek(0.1);
        session.RigSettings = rig.WithFlightSpeed(1000f) with { FlightRange = 800f };
        Assert.Equal(-300f, root.WorldTransform.Translation.X, 2);
        session.Seek(0.3);
        Assert.Equal(-100f, root.WorldTransform.Translation.X, 2);
        Assert.Equal(400f, Assert.Single(root.Emitters).SystemTarget.X, 2);
        Assert.Equal(1000f, session.RigSettings.Missile.Speed);
        session.RigSettings = rig with { IsLooping = true };
        session.Seek(0d);
        session.Seek(session.RigDuration + 0.1d);
        Assert.InRange(root.WorldTransform.Translation.X, -140f, -60f);
    }

    [Fact]
    public void RepeatedRigSamplesReuseTheCompiledFlight()
    {
        var rig = VfxRigSettings.ForPreset(VfxRigPreset.Missile) with { Missile = Missile(VfxSpellMissileMovementKind.FixedSpeed) };
        VfxRigMotion.OriginAt(rig, .1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 16; i++) VfxRigMotion.OriginAt(rig, i / 60d);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 65536);
    }

    [Fact]
    public void GroundTargetOmitsTargetHeightAugmentUnlessEndAtLaunchHeightIsEnabled()
    {
        var missile = Missile(VfxSpellMissileMovementKind.FixedSpeed) with
        { HeightOffsetAtEnd = false, TargetHeight = 30, InitialTargetHeight = 10 };
        var track = VfxSpellFlightPath.Compile(missile, new(0,100,0), new(1000,0,0), true);
        Assert.Equal(10, track.End.Y);
    }

    [Fact]
    public void CircleRigUsesTheFullFlightRangeAsItsRadius()
    {
        var rig = VfxRigSettings.ForPreset(VfxRigPreset.Missile) with
        { Missile = Missile(VfxSpellMissileMovementKind.Circle) with { AngularVelocity = 1, Lifetime = 1 } };
        var origin = VfxRigMotion.OriginAt(rig, 0);
        Assert.Equal(rig.FlightRange, MathF.Sqrt(origin.X * origin.X + origin.Z * origin.Z));
    }

    [Fact]
    public void AFiniteRadialOnlyCircleCanBePreviewedAsAnAbility()
    {
        var missile = Missile(VfxSpellMissileMovementKind.Circle) with { RadialVelocity = 100, Lifetime = 1 };
        Assert.NotNull(VfxSpellPreviewComposer.CompileFlightPath(missile, new(1000,0,0), Vector3.Zero));
    }

    [Fact]
    public void AFixedTimeRigWithOmittedDurationDoesNotFallBackToAnUnrelatedStraightFlight()
    {
        var rig = VfxRigSettings.ForPreset(VfxRigPreset.Missile) with
        { Missile = Missile(VfxSpellMissileMovementKind.FixedTime) with { Duration = null } };
        Assert.NotNull(VfxRigMotion.CompileRigTrack(rig));
    }

    [Fact]
    public void UnmatchedTrailUsesAStraightMissileInsteadOfOrbit()
    {
        var emitter = VfxQualityFlexTests.Definition() with
        {
            PrimitiveKind = VfxPrimitiveKind.CameraTrail, IsSingleParticle = false,
            BirthVelocity = null, Acceleration = null, BirthAcceleration = null,
            Trail = new(VfxCurve3.Const(Vector3.One),0,1,0,0)
        };
        var system = new VfxSystemDefinition(1,"trail","",[emitter]);
        var rig = VfxSystemRigResolver.ResolveSettings(system,new VfxLoadingService.Bundle()).Settings;
        Assert.Equal(VfxRigMotionKind.Path,rig.MotionKind);
    }

    [Theory]
    [InlineData("mMissileEffectKey", false, false)]
    [InlineData("mMissileEffectPlayerKey", false, false)]
    [InlineData("mMissileEffectEnemyKey", false, false)]
    [InlineData("mMissileEffectKey", true, false)]
    [InlineData("mMissileEffectPlayerKey", true, false)]
    [InlineData("mMissileEffectEnemyKey", true, false)]
    [InlineData("mMissileEffectKey", false, true)]
    [InlineData("mMissileEffectPlayerKey", false, true)]
    [InlineData("mMissileEffectEnemyKey", false, true)]
    [InlineData("mMissileEffectKey", true, true)]
    [InlineData("mMissileEffectPlayerKey", true, true)]
    [InlineData("mMissileEffectEnemyKey", true, true)]
    public void OpeningASystemFindsTheCharacterMissileSpellsWithoutAnExplicitBinDependency(string keyField, bool isolated, bool useSpellSpeed)
    {
        string root = Path.Combine(Path.GetTempPath(), "AssetsManagerReview708", Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root,"data","characters","hero");
        Directory.CreateDirectory(Path.Combine(folder,"skins"));
        void Write(string path, params BinTreeObject[] objects)
        {
            using var stream=File.Create(path);
            new BinTree(objects,Array.Empty<string>()).Write(stream);
        }
        try
        {
            string skin=Path.Combine(folder,"skins","skin0.bin");
            var effect=new BinTreeObject("Characters/Hero/Skins/Skin0/Particles/Q","VfxSystemDefinitionData",Array.Empty<BinTreeProperty>());
            uint key=Fnv1a.HashLower("HeroQ");
            var resolver=new BinTreeObject("Characters/Hero/Skins/Skin0/Resolver","ResourceResolver",new BinTreeProperty[]
            {
                new BinTreeMap(Fnv1a.HashLower("resourceMap"),BinPropertyType.Hash,BinPropertyType.ObjectLink,
                    new[]{new System.Collections.Generic.KeyValuePair<BinTreeProperty,BinTreeProperty>(new BinTreeHash(0,key),new BinTreeObjectLink(0,effect.PathHash))})
            });
            Write(skin,effect,resolver);
            string opened = isolated ? Path.Combine(folder,"q.bin") : skin;
            if (isolated) Write(opened,effect);
            var spell=new BinTreeObject("Characters/Hero/Spells/Q","SpellObject",new BinTreeProperty[]
            {
                new BinTreeStruct(Fnv1a.HashLower("mSpell"),Fnv1a.HashLower("SpellDataResource"),new BinTreeProperty[]
                {
                    new BinTreeHash(Fnv1a.HashLower(keyField),key),
                    new BinTreeF32(Fnv1a.HashLower("missileSpeed"),2000f),
                    new BinTreeContainer(Fnv1a.HashLower("castRange"), BinPropertyType.F32,
                        new BinTreeProperty[] { new BinTreeF32(0,0), new BinTreeF32(0,600), new BinTreeF32(0,600),
                            new BinTreeF32(0,600), new BinTreeF32(0,600), new BinTreeF32(0,600), new BinTreeF32(0,600) }),
                    new BinTreeStruct(Fnv1a.HashLower("mMissileSpec"),Fnv1a.HashLower("MissileSpecification"),new BinTreeProperty[]
                    {
                        new BinTreeStruct(Fnv1a.HashLower("movementComponent"),Fnv1a.HashLower("FixedSpeedMovement"),
                            useSpellSpeed ? Array.Empty<BinTreeProperty>() : new BinTreeProperty[]{new BinTreeF32(Fnv1a.HashLower("mSpeed"),2400)})
                    })
                })
            });
            Write(Path.Combine(folder,"hero.bin"),spell);
            using var service=new VfxLoadingService(binEntryPathResolver: hash => hash == effect.PathHash ? "Characters/Hero/Skins/Skin0/Particles/Q" : null);
            var bundle=service.Load(opened,null);
            var selected = VfxSystemRigResolver.ResolveSettings(bundle.Systems[effect.PathHash], bundle).Settings;
            Assert.Equal(useSpellSpeed ? 2000 : 2400, selected.Missile?.Speed);
            Assert.Equal(600, selected.FlightRange);
            Assert.Equal(2000, Assert.Single(bundle.MissileSpells[effect.PathHash]).MissileSpeed);
            Assert.Empty(bundle.SpellPreviews);
            Assert.Single(bundle.LoadedBins);
        }
        finally { Directory.Delete(root,true); }
    }
    public static IEnumerable<object[]> ReferenceFlights()
    {
        using var stream=typeof(VfxMissileFlightContractTests).Assembly.GetManifestResourceStream(
            "AssetsManager.Tests.xUnit.Services.Viewer.Vfx.Fixtures.MissileFlightReference.json");
        using var json=JsonDocument.Parse(stream);
        foreach(var item in json.RootElement.GetProperty("Cases").EnumerateArray())
            yield return [item.GetProperty("Name").GetString(),item.GetRawText()];
    }

    [Theory]
    [InlineData(VfxSpellMissileMovementKind.FixedSpeed)]
    [InlineData(VfxSpellMissileMovementKind.FixedSpeedSpline)]
    [InlineData(VfxSpellMissileMovementKind.FixedTime)]
    [InlineData(VfxSpellMissileMovementKind.Accelerating)]
    [InlineData(VfxSpellMissileMovementKind.Circle)]
    public void SpeedTuningPreservesTheAuthoredMovement(VfxSpellMissileMovementKind kind)
    {
        var rig=VfxRigSettings.ForPreset(VfxRigPreset.Missile) with { Missile=Missile(kind) };
        var tuned=rig.WithFlightSpeed(2400);
        Assert.Equal(kind,tuned.Missile.MovementKind);
        if(kind is VfxSpellMissileMovementKind.FixedSpeed or VfxSpellMissileMovementKind.FixedSpeedSpline)
            Assert.Equal(2400,tuned.Missile.Speed);
        else Assert.Equal(rig,tuned);
    }

    [Fact]
    public void CachedTracksFollowHeightRangeAndMovementChanges()
    {
        var rig=VfxRigSettings.ForPreset(VfxRigPreset.Missile) with { Missile=Missile(VfxSpellMissileMovementKind.FixedSpeed) };
        var first=VfxRigMotion.CompileRigTrack(rig);
        Assert.Same(first,VfxRigMotion.CompileRigTrack(rig));
        Assert.Equal(200,VfxRigMotion.OriginAt(rig with {Height=200},0).Y);
        Assert.Equal(-1000,VfxRigMotion.OriginAt(rig with {FlightRange=2000},0).X);
        Assert.NotSame(first,VfxRigMotion.CompileRigTrack(rig.WithFlightSpeed(2400)));
    }

    [Theory,MemberData(nameof(ReferenceFlights))]
    public void FlightMatchesPinnedReferenceSamples(string name,string raw)
    {
        using var json=JsonDocument.Parse(raw);
        var data=json.RootElement;
        var options=new JsonSerializerOptions{IncludeFields=true};
        options.Converters.Add(new JsonStringEnumConverter());
        var missile=JsonSerializer.Deserialize<VfxSpellMissilePreview>(data.GetProperty("Input").GetRawText(),options);
        var track=VfxSpellFlightPath.Compile(missile,new(0,100,0),new(900,0,400),true);
        Assert.NotNull(track);
        if(data.GetProperty("Arrival").ValueKind==JsonValueKind.Null) Assert.Null(track.Arrival);
        else Assert.InRange(Math.Abs(track.Arrival.Value-data.GetProperty("Arrival").GetDouble()),0,VfxSpellFlightPath.FlightStep+1e-6);
        foreach(var sample in data.GetProperty("Samples").EnumerateArray())
        {
            double time=sample.GetProperty("Time").GetDouble();
            Vector3 Read(string key){var v=sample.GetProperty(key);return new(v[0].GetSingle(),v[1].GetSingle(),v[2].GetSingle());}
            var expected=Read("Position"); var actual=track.PositionAt(time);
            float length=expected.Length();
            // Integration uses floats; extreme spline/gravity heights also need a few ULPs.
            float tolerance=.1f+4f*(MathF.BitIncrement(length)-length);
            Assert.True(Vector3.Distance(expected,actual)<tolerance,$"{name} at {time}: expected position {expected}, got {actual}, tolerance {tolerance}");
            expected=Read("Direction");actual=track.DirectionAt(time);
            Assert.True(Vector3.Distance(expected,actual)<.01f,$"{name} at {time}: expected direction {expected}, got {actual}");
        }
    }

}

[CollectionDefinition("VFX missile files", DisableParallelization=true)]
public sealed class VfxMissileFileCollection { }
